using MusicConverter.MusicXml;
using MusicConverter.Neutral;

namespace MusicConverter.Converters;

internal sealed class MidiParser
{
    private const int MaxVoices = 8;
    private const int MaxMeasures = 20000;

    private sealed class TickComparer : IComparer<(long Tick, NeutralNote Note)>
    {
        public static readonly TickComparer Instance = new();
        public int Compare((long Tick, NeutralNote Note) x, (long Tick, NeutralNote Note) y) => x.Tick.CompareTo(y.Tick);
    }

    private sealed record MidiNote(long Start, long End, int Pitch, int Channel);
    private sealed record MidiTrack(string? Name, List<MidiNote> Notes, Dictionary<int, int> Programs);
    private sealed record TimeSignature(long Tick, int Beats, int BeatType);
    private sealed record KeySignature(long Tick, int Fifths);
    private sealed record TempoEvent(long Tick, double BeatsPerMinute);
    private sealed record TimedNote(long Start, long End, int[] Pitches);
    private sealed record Measure(long Start, long End, int Beats, int BeatType, bool TimeChanged, int Fifths, bool KeyChanged);

    private readonly byte[] _data;
    private readonly ConversionContext _context;
    private readonly List<TimeSignature> _timeSignatures = new();
    private readonly List<KeySignature> _keySignatures = new();
    private readonly List<TempoEvent> _tempoEvents = new();
    private int _position;
    private long _totalNotes;
    private int _sourceTicksPerQuarter;

    // All parsed positions are rescaled to the neutral resolution (DurationHelper.TicksPerQuarter per quarter).
    private int _ticksPerQuarter;

    private HashSet<(int Measure, int Beat)> _tripletBeats = new();
    private int _straightGrid = 120;

    private long Scale(long sourceTick) =>
        (long)Math.Round(sourceTick * (double)DurationHelper.TicksPerQuarter / _sourceTicksPerQuarter);

    public MidiParser(byte[] data, ConversionContext context)
    {
        _data = data;
        _context = context;
    }

    public NeutralScore Parse()
    {
        if (!ReadChunkId().Equals("MThd", StringComparison.Ordinal))
            throw _context.ParseError("Not a Standard MIDI File: missing MThd header.");

        var headerLength = ReadUInt32();
        if (headerLength < 6 || headerLength > _data.Length - _position)
            throw _context.ParseError("Invalid MIDI header length.");
        var format = ReadUInt16();
        var trackCount = ReadUInt16();
        var division = ReadUInt16();
        _position += checked((int)headerLength - 6);

        if (format > 1) throw _context.ParseError($"MIDI format {format} is not supported; use format 0 or 1.");
        if (trackCount == 0) throw _context.ParseError("MIDI file declares no tracks.");
        if (format == 0 && trackCount != 1) throw _context.ParseError("MIDI format 0 must contain exactly one track.");
        if ((division & 0x8000) != 0 || division == 0)
            throw _context.ParseError("SMPTE-timed or invalid MIDI files are not supported; ticks per quarter note are required.");
        _sourceTicksPerQuarter = division;
        _ticksPerQuarter = DurationHelper.TicksPerQuarter;

        var tracks = new List<MidiTrack>();
        for (var trackIndex = 0; trackIndex < trackCount; trackIndex++)
        {
            _context.ThrowIfCancelled();
            _context.ReportParsing(trackIndex / (double)trackCount, $"Reading MIDI track {trackIndex + 1}");
            if (!ReadChunkId().Equals("MTrk", StringComparison.Ordinal))
                throw _context.ParseError($"Expected MIDI track chunk {trackIndex + 1}.");
            var length = ReadUInt32();
            if (length > _data.Length - _position) throw _context.ParseError($"MIDI track {trackIndex + 1} is truncated.");
            var end = checked(_position + (int)length);
            tracks.Add(ReadTrack(end, trackIndex));
            if (_position != end) throw _context.ParseError($"Invalid event data in MIDI track {trackIndex + 1}.");
        }

        if (_position != _data.Length) _context.Warn("Trailing bytes after the declared MIDI tracks were ignored.");
        var notes = tracks.SelectMany(t => t.Notes).ToList();
        if (notes.Count == 0) throw _context.ParseError("No note events found in MIDI file.");
        var maxTick = notes.Max(n => n.End);

        var score = new NeutralScore();
        foreach (var partData in tracks.Select((track, index) => (track, index))
                     .SelectMany(item => item.track.Notes.GroupBy(n => n.Channel)
                         .Select(group => (item.track, item.index, Channel: group.Key, Notes: group.ToList())))
                     .Where(p => p.Notes.Count > 0))
        {
            _context.ThrowIfCancelled();
            var isPercussion = partData.Channel == 9;
            var name = !string.IsNullOrWhiteSpace(partData.track.Name) ? partData.track.Name!
                : isPercussion ? "Percussion"
                    : partData.track.Programs.TryGetValue(partData.Channel, out var program) && program < GeneralMidiNames.Length
                        ? GeneralMidiNames[program]
                        : $"Track {partData.index + 1} Channel {partData.Channel + 1}";
            score.Parts.Add(BuildPart(name, partData.Notes, maxTick, score.Parts.Count == 0, ChooseClef(partData.Notes, isPercussion)));
        }
        return score;
    }

    private static (string Sign, int? Line) ChooseClef(IReadOnlyCollection<MidiNote> notes, bool isPercussion)
    {
        if (isPercussion) return ("percussion", null);
        // Parts that sit mostly below middle C read better in the bass clef.
        return notes.Average(n => n.Pitch) < 55 ? ("F", 4) : ("G", 2);
    }

    private static readonly string[] GeneralMidiNames =
    {
        "Acoustic Grand Piano", "Bright Acoustic Piano", "Electric Grand Piano", "Honky-tonk Piano", "Electric Piano 1", "Electric Piano 2",
        "Harpsichord", "Clavinet", "Celesta", "Glockenspiel", "Music Box", "Vibraphone", "Marimba", "Xylophone", "Tubular Bells", "Dulcimer",
        "Drawbar Organ", "Percussive Organ", "Rock Organ", "Church Organ", "Reed Organ", "Accordion", "Harmonica", "Tango Accordion",
        "Acoustic Guitar (nylon)", "Acoustic Guitar (steel)", "Electric Guitar (jazz)", "Electric Guitar (clean)", "Electric Guitar (muted)",
        "Overdriven Guitar", "Distortion Guitar", "Guitar Harmonics", "Acoustic Bass", "Electric Bass (finger)", "Electric Bass (pick)",
        "Fretless Bass", "Slap Bass 1", "Slap Bass 2", "Synth Bass 1", "Synth Bass 2", "Violin", "Viola", "Cello", "Contrabass",
        "Tremolo Strings", "Pizzicato Strings", "Orchestral Harp", "Timpani", "String Ensemble 1", "String Ensemble 2", "Synth Strings 1",
        "Synth Strings 2", "Choir Aahs", "Voice Oohs", "Synth Voice", "Orchestra Hit", "Trumpet", "Trombone", "Tuba", "Muted Trumpet",
        "French Horn", "Brass Section", "Synth Brass 1", "Synth Brass 2", "Soprano Sax", "Alto Sax", "Tenor Sax", "Baritone Sax", "Oboe",
        "English Horn", "Bassoon", "Clarinet", "Piccolo", "Flute", "Recorder", "Pan Flute", "Blown Bottle", "Shakuhachi", "Whistle", "Ocarina",
        "Lead 1 (square)", "Lead 2 (sawtooth)", "Lead 3 (calliope)", "Lead 4 (chiff)", "Lead 5 (charang)", "Lead 6 (voice)", "Lead 7 (fifths)",
        "Lead 8 (bass + lead)", "Pad 1 (new age)", "Pad 2 (warm)", "Pad 3 (polysynth)", "Pad 4 (choir)", "Pad 5 (bowed)", "Pad 6 (metallic)",
        "Pad 7 (halo)", "Pad 8 (sweep)", "FX 1 (rain)", "FX 2 (soundtrack)", "FX 3 (crystal)", "FX 4 (atmosphere)", "FX 5 (brightness)",
        "FX 6 (goblins)", "FX 7 (echoes)", "FX 8 (sci-fi)", "Sitar", "Banjo", "Shamisen", "Koto", "Kalimba", "Bagpipe", "Fiddle", "Shanai",
        "Tinkle Bell", "Agogo", "Steel Drums", "Woodblock", "Taiko Drum", "Melodic Tom", "Synth Drum", "Reverse Cymbal", "Guitar Fret Noise",
        "Breath Noise", "Seashore", "Bird Tweet", "Telephone Ring", "Helicopter", "Applause", "Gunshot"
    };

    private MidiTrack ReadTrack(int end, int trackIndex)
    {
        var notes = new List<MidiNote>();
        var programs = new Dictionary<int, int>();
        var active = new Dictionary<(int Channel, int Pitch), Stack<long>>();
        string? name = null;
        byte runningStatus = 0;
        long tick = 0;

        while (_position < end)
        {
            _context.ThrowIfCancelled();
            tick = checked(tick + ReadVariableLength(end));
            if (_position >= end) throw _context.ParseError($"Missing MIDI event in track {trackIndex + 1}.");

            var first = ReadByte(end);
            byte status;
            byte firstData = 0;
            if ((first & 0x80) != 0)
            {
                status = first;
                if (status < 0xF0) runningStatus = status;
                else if (status is 0xF0 or 0xF7) runningStatus = 0;
            }
            else
            {
                if (runningStatus == 0) throw _context.ParseError($"Invalid running status in MIDI track {trackIndex + 1}.");
                status = runningStatus;
                firstData = first;
            }

            if (status == 0xFF)
            {
                var type = ReadByte(end);
                var size = ReadVariableLength(end);
                if (size > end - _position) throw _context.ParseError($"Truncated meta event in MIDI track {trackIndex + 1}.");
                var metaEnd = _position + (int)size;
                switch (type)
                {
                    case 0x03:
                        name = ReadText(metaEnd);
                        break;
                    case 0x58:
                        if (size < 2) throw _context.ParseError("Invalid MIDI time-signature event.");
                        var beats = _data[_position];
                        var exponent = _data[_position + 1];
                        if (beats == 0 || exponent > 7) throw _context.ParseError("Invalid MIDI time signature.");
                        _timeSignatures.Add(new TimeSignature(Scale(tick), beats, 1 << exponent));
                        break;
                    case 0x59:
                        if (size < 2) throw _context.ParseError("Invalid MIDI key-signature event.");
                        var fifths = unchecked((sbyte)_data[_position]);
                        if (fifths is < -7 or > 7) throw _context.ParseError("Invalid MIDI key signature.");
                        _keySignatures.Add(new KeySignature(Scale(tick), fifths));
                        break;
                    case 0x51:
                        if (size < 3) throw _context.ParseError("Invalid MIDI tempo event.");
                        var microsecondsPerQuarter = (_data[_position] << 16) | (_data[_position + 1] << 8) | _data[_position + 2];
                        if (microsecondsPerQuarter == 0) throw _context.ParseError("Invalid MIDI tempo value.");
                        _tempoEvents.Add(new TempoEvent(Scale(tick), 60_000_000.0 / microsecondsPerQuarter));
                        break;
                }
                _position = metaEnd;
                continue;
            }

            if (status is 0xF0 or 0xF7)
            {
                var size = ReadVariableLength(end);
                if (size > end - _position) throw _context.ParseError($"Truncated system-exclusive event in MIDI track {trackIndex + 1}.");
                _position += (int)size;
                continue;
            }

            if (status >= 0xF0) throw _context.ParseError($"Unsupported system event 0x{status:X2} in MIDI track {trackIndex + 1}.");
            var command = status & 0xF0;
            var channel = status & 0x0F;
            var data1 = (status == first ? ReadByte(end) : firstData);
            var data2 = command is 0xC0 or 0xD0 ? (byte)0 : ReadByte(end);
            if (data1 >= 0x80 || data2 >= 0x80) throw _context.ParseError($"Invalid MIDI channel event in track {trackIndex + 1}.");

            if (command == 0xC0)
            {
                programs.TryAdd(channel, data1);
            }
            else if (command == 0x90 && data2 > 0)
            {
                var key = (channel, (int)data1);
                if (!active.TryGetValue(key, out var starts)) active[key] = starts = new Stack<long>();
                starts.Push(tick);
            }
            else if (command == 0x80 || command == 0x90)
            {
                var key = (channel, (int)data1);
                if (active.TryGetValue(key, out var starts) && starts.Count > 0)
                {
                    var start = starts.Pop();
                    if (tick > start)
                    {
                        if (++_totalNotes > _context.Settings.MaxMidiNotes)
                            throw _context.LimitError($"MIDI file has more than {_context.Settings.MaxMidiNotes:N0} notes.");
                        notes.Add(new MidiNote(Scale(start), Scale(tick), data1, channel));
                    }
                    else _context.Warn("Zero-duration MIDI notes were ignored.");
                }
                else _context.Warn("MIDI note-off events without a matching note-on were ignored.");
            }
        }

        foreach (var pending in active.Values.Where(s => s.Count > 0))
            _context.Warn("MIDI note-on events without a matching note-off were ignored.");
        return new MidiTrack(name, notes, programs);
    }

    private NeutralPart BuildPart(
        string name, IReadOnlyCollection<MidiNote> notes, long maxTick, bool includeTempoEvents, (string Sign, int? Line) clef)
    {
        var part = new NeutralPart { Name = name };
        // The extra beat of margin leaves room for notes that snap past the raw end; empty trailing bars are trimmed below.
        var measures = CreateMeasures(maxTick + DurationHelper.TicksPerQuarter);
        var quantized = Quantize(notes, measures);
        var lastEnd = quantized.Max(n => n.End);
        measures.RemoveAll(m => m.Start >= lastEnd);
        var positionedNotes = new List<(long Tick, NeutralNote Note)>();
        var grouped = quantized.GroupBy(n => (n.Start, n.End))
            .Select(g => new TimedNote(g.Key.Start, g.Key.End, g.Select(n => n.Pitch).Distinct().Order().ToArray()))
            .OrderBy(n => n.Start).ThenBy(n => n.End).ToList();

        var voices = new List<List<TimedNote>>();
        var voiceEnds = new List<long>();
        var droppedNotes = 0;
        foreach (var note in grouped)
        {
            _context.ThrowIfCancelled();
            var voice = voiceEnds.FindIndex(end => end <= note.Start);
            if (voice < 0)
            {
                if (voices.Count >= MaxVoices)
                {
                    droppedNotes++;
                    continue;
                }
                voice = voices.Count;
                voices.Add(new List<TimedNote>());
                voiceEnds.Add(0);
            }
            voices[voice].Add(note);
            voiceEnds[voice] = note.End;
        }

        if (droppedNotes > 0)
            _context.Warn($"{droppedNotes} overlapping MIDI note(s) in '{name}' exceeded {MaxVoices} voices and were ignored.");

        var voiceStart = new int[voices.Count];
        for (var measureIndex = 0; measureIndex < measures.Count; measureIndex++)
        {
            _context.ThrowIfCancelled();
            var measureInfo = measures[measureIndex];
            var measure = new NeutralMeasure { Number = measureIndex + 1 };
            if (measureIndex == 0 || measureInfo.TimeChanged || measureInfo.KeyChanged)
            {
                measure.Attributes = new NeutralAttributes
                {
                    Beats = measureInfo.TimeChanged || measureIndex == 0 ? measureInfo.Beats : null,
                    BeatType = measureInfo.TimeChanged || measureIndex == 0 ? measureInfo.BeatType : null,
                    Fifths = measureInfo.KeyChanged || measureIndex == 0 ? measureInfo.Fifths : null,
                    ClefSign = measureIndex == 0 ? clef.Sign : null,
                    ClefLine = measureIndex == 0 ? clef.Line : null
                };
            }

            for (var voiceIndex = 0; voiceIndex < voices.Count; voiceIndex++)
                AddVoiceMeasure(measure, voices[voiceIndex], ref voiceStart[voiceIndex], measureInfo, measureIndex, voiceIndex + 1,
                    includeTempoEvents && voiceIndex == 0 ? positionedNotes : null);
            part.Measures.Add(measure);
        }

        if (includeTempoEvents)
        {
            foreach (var tempo in _tempoEvents.GroupBy(t => t.Tick).Select(g => g.Last()).OrderBy(t => t.Tick))
            {
                var index = positionedNotes.BinarySearch((tempo.Tick, null!), TickComparer.Instance);
                if (index < 0) index = ~index;
                var target = index < positionedNotes.Count ? positionedNotes[index].Note : null;
                if (target is null)
                    _context.Warn("MIDI tempo events after the final note were ignored.");
                else
                    target.Directions.Add(new NeutralDirection(
                        DirectionKinds.Tempo, tempo.BeatsPerMinute.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), "quarter"));
            }
        }

        return part;
    }

    private void AddVoiceMeasure(
        NeutralMeasure measure,
        IReadOnlyList<TimedNote> notes,
        ref int firstIndex,
        Measure current,
        int measureIndex,
        int voice,
        List<(long Tick, NeutralNote Note)>? positionedNotes)
    {
        var cursor = current.Start;
        // Voice notes are sorted and non-overlapping, so skip those that ended before this measure.
        while (firstIndex < notes.Count && notes[firstIndex].End <= current.Start) firstIndex++;
        for (var i = firstIndex; i < notes.Count && notes[i].Start < current.End; i++)
        {
            var source = notes[i];
            var start = Math.Max(source.Start, current.Start);
            var end = Math.Min(source.End, current.End);
            if (start > cursor)
                EmitSegment(measure, current, measureIndex, Array.Empty<int>(), cursor, start, voice, false, positionedNotes);
            if (end > start)
            {
                EmitSegment(measure, current, measureIndex, source.Pitches, start, end, voice, source.End > current.End, positionedNotes);
                cursor = end;
            }
        }

        if (cursor < current.End)
            EmitSegment(measure, current, measureIndex, Array.Empty<int>(), cursor, current.End, voice, false, positionedNotes);
    }

    private static readonly int[] StraightDurations = { 1920, 1440, 960, 720, 480, 360, 240, 180, 120, 90, 60, 30 };
    private static readonly HashSet<int> DottedDurations = new() { 1440, 720, 360, 180, 90 };

    /// <summary>Splits [start, end) into notatable pieces (tied for notes) that respect beat alignment and triplet beats.</summary>
    private void EmitSegment(
        NeutralMeasure measure, Measure current, int measureIndex, int[] pitches, long start, long end, int voice,
        bool tieAtEnd, List<(long Tick, NeutralNote Note)>? positionedNotes)
    {
        const int beat = DurationHelper.TicksPerQuarter;
        var p = start - current.Start;
        var e = end - current.Start;
        var measureLength = current.End - current.Start;

        while (p < e)
        {
            var beatIndex = (int)(p / beat);
            var beatEnd = Math.Min((beatIndex + 1L) * beat, measureLength);
            long length;
            var tuplet = false;

            if (_tripletBeats.Contains((measureIndex, beatIndex)) && (p % beat != 0 || e < beatEnd))
            {
                length = Math.Min(e, beatEnd) - p;
                tuplet = length % 160 == 0;
            }
            else length = 0;

            if (!tuplet) length = LargestStraightDuration(p, e, measureIndex);

            var note = new NeutralNote
            {
                Ticks = (int)length,
                Voice = voice,
                TieToNext = pitches.Length > 0 && (p + length < e || tieAtEnd),
                TupletActual = tuplet ? 3 : null,
                TupletNormal = tuplet ? 2 : null,
                Pitches = pitches.Select(ToPitch).ToList()
            };
            measure.Notes.Add(note);
            positionedNotes?.Add((current.Start + p, note));
            p += length;
        }
    }

    private long LargestStraightDuration(long p, long e, int measureIndex)
    {
        const int beat = DurationHelper.TicksPerQuarter;
        foreach (var d in StraightDurations)
        {
            if (d > e - p) continue;
            var baseValue = DottedDurations.Contains(d) ? d * 2 / 3 : d;
            if (p % baseValue != 0) continue;
            var q = p + d;
            // Off-beat pieces stop at the next beat so syncopations are notated as tied beat-sized pieces.
            if (p % beat != 0 && q > (p / beat + 1) * beat) continue;
            if (q % beat != 0 && _tripletBeats.Contains((measureIndex, (int)(q / beat)))) continue;
            return d;
        }
        return e - p;
    }

    /// <summary>
    /// Snaps note starts/ends to a 16th-note grid (32nd when the music needs it), or to a triplet grid
    /// in beats where that fits clearly better, so every duration becomes a notatable value.
    /// </summary>
    private List<MidiNote> Quantize(IReadOnlyCollection<MidiNote> notes, List<Measure> measures)
    {
        const int beat = DurationHelper.TicksPerQuarter;
        var starts = measures.Select(m => m.Start).ToArray();

        (int Measure, long Offset) Locate(long tick)
        {
            var index = Array.BinarySearch(starts, tick);
            if (index < 0) index = Math.Max(0, ~index - 1);
            return (index, tick - starts[index]);
        }

        var straightError = new Dictionary<(int, int), double>();
        var tripletError = new Dictionary<(int, int), double>();
        var offGrid = new List<((int, int) Beat, double Error)>();
        foreach (var tick in notes.SelectMany(n => new[] { n.Start, n.End }))
        {
            var (measure, offset) = Locate(tick);
            var key = (measure, (int)(offset / beat));
            var r = offset % beat;
            var straight = Math.Abs(r - Math.Round(r / 120.0) * 120);
            var triplet = Math.Abs(r - Math.Round(r / 160.0) * 160);
            straightError[key] = straightError.GetValueOrDefault(key) + straight;
            tripletError[key] = tripletError.GetValueOrDefault(key) + triplet;
            offGrid.Add((key, straight));
        }

        _tripletBeats = straightError.Keys
            .Where(k => tripletError[k] * 2 < straightError[k])
            .ToHashSet();

        var straightTimes = offGrid.Where(o => !_tripletBeats.Contains(o.Beat)).ToList();
        _straightGrid = straightTimes.Count > 0 && straightTimes.Count(o => o.Error > 20) >= straightTimes.Count * 0.15 ? 60 : 120;

        long Snap(long tick, out int grid)
        {
            var (measure, offset) = Locate(tick);
            var beatIndex = (int)(offset / beat);
            grid = _tripletBeats.Contains((measure, beatIndex)) ? 160 : _straightGrid;
            var r = offset % beat;
            var snapped = starts[measure] + offset - r + (long)Math.Round(r / (double)grid) * grid;
            return Math.Min(snapped, measures[measure].End);
        }

        var result = new List<MidiNote>(notes.Count);
        foreach (var note in notes)
        {
            var start = Snap(note.Start, out var grid);
            var end = Snap(note.End, out _);
            if (end <= start) end = start + grid;
            result.Add(note with { Start = start, End = end });
        }
        return result;
    }

    private List<Measure> CreateMeasures(long maxTick)
    {
        var timeChanges = _timeSignatures.OrderBy(t => t.Tick).ToList();
        var keyChanges = _keySignatures.OrderBy(k => k.Tick).ToList();
        var result = new List<Measure>();
        var beats = 4;
        var beatType = 4;
        var fifths = 0;
        var timeIndex = 0;
        var keyIndex = 0;
        var start = 0L;

        while (start < maxTick)
        {
            var timeChanged = false;
            var keyChanged = false;
            while (timeIndex < timeChanges.Count && timeChanges[timeIndex].Tick <= start)
            {
                beats = timeChanges[timeIndex].Beats;
                beatType = timeChanges[timeIndex].BeatType;
                timeIndex++;
                timeChanged = true;
            }
            while (keyIndex < keyChanges.Count && keyChanges[keyIndex].Tick <= start)
            {
                fifths = keyChanges[keyIndex].Fifths;
                keyIndex++;
                keyChanged = true;
            }

            var nominalEnd = checked(start + (long)beats * 4 * _ticksPerQuarter / beatType);
            var end = nominalEnd;
            if (timeIndex < timeChanges.Count && timeChanges[timeIndex].Tick > start &&
                timeChanges[timeIndex].Tick < end)
                end = timeChanges[timeIndex].Tick;
            if (end <= start) throw _context.ParseError("Invalid MIDI measure boundary.");
            if (result.Count >= MaxMeasures)
                throw _context.ParseError($"MIDI file is too long to convert (more than {MaxMeasures} measures).");
            _context.ThrowIfCancelled();
            result.Add(new Measure(start, end, beats, beatType, timeChanged, fifths, keyChanged));
            start = end;
        }
        return result;
    }

    private static NeutralPitch ToPitch(int midiPitch)
    {
        var steps = new[] { ('C', 0), ('C', 1), ('D', 0), ('D', 1), ('E', 0), ('F', 0), ('F', 1), ('G', 0), ('G', 1), ('A', 0), ('A', 1), ('B', 0) };
        var (step, alter) = steps[midiPitch % 12];
        return new NeutralPitch(step, alter, midiPitch / 12 - 1);
    }

    private string ReadText(int end)
    {
        var value = System.Text.Encoding.UTF8.GetString(_data, _position, end - _position).TrimEnd('\0');
        _position = end;
        return value;
    }

    private string ReadChunkId()
    {
        EnsureAvailable(4, _data.Length);
        var value = System.Text.Encoding.ASCII.GetString(_data, _position, 4);
        _position += 4;
        return value;
    }

    private byte ReadByte(int end)
    {
        EnsureAvailable(1, end);
        return _data[_position++];
    }

    private ushort ReadUInt16()
    {
        EnsureAvailable(2, _data.Length);
        var value = (ushort)((_data[_position] << 8) | _data[_position + 1]);
        _position += 2;
        return value;
    }

    private uint ReadUInt32()
    {
        EnsureAvailable(4, _data.Length);
        var value = ((uint)_data[_position] << 24) | ((uint)_data[_position + 1] << 16) |
                    ((uint)_data[_position + 2] << 8) | _data[_position + 3];
        _position += 4;
        return value;
    }

    private int ReadVariableLength(int end)
    {
        var value = 0;
        for (var index = 0; index < 4; index++)
        {
            var next = ReadByte(end);
            value = (value << 7) | (next & 0x7F);
            if ((next & 0x80) == 0) return value;
        }
        throw _context.ParseError("Invalid MIDI variable-length quantity.");
    }

    private void EnsureAvailable(int count, int end)
    {
        if (count < 0 || _position > end - count)
            throw _context.ParseError("Unexpected end of MIDI file.");
    }
}