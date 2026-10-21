using System.Text.RegularExpressions;
using MusicConverter.Logging;
using MusicConverter.MusicXml;
using MusicConverter.Neutral;

namespace MusicConverter.Converters;

/// <summary>Imports NoteWorthy Composer text files (.nwctxt).</summary>
public sealed class NwcTxtConverter : MusicFormatConverterBase
{
    private static readonly string[] Extensions = { ".nwctxt" };

    public NwcTxtConverter() : this(null) { }

    public NwcTxtConverter(ILogger? logger) : base(logger) { }

    public override string FormatName => "NWCTXT";

    public override IReadOnlyCollection<string> FileExtensions => Extensions;

    public override bool CanRead(string filePath)
    {
        if (base.CanRead(filePath)) return true;
        if (!string.Equals(Path.GetExtension(filePath), ".txt", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var reader = new StreamReader(filePath);
            return reader.ReadLine()?.StartsWith("!NoteWorthyComposer", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    protected override NeutralScore Parse(string text, ConversionContext context) =>
        new NwcTxtParser(text, context).Parse();
}

internal sealed class NwcTxtParser
{
    private static readonly Regex PositionRegex = new(@"^([#bnxv]?)(-?\d+)(\^?)$", RegexOptions.Compiled);

    private sealed class Staff
    {
        public Staff(PartBuilder builder) => Builder = builder;
        public PartBuilder Builder { get; }
        public int ClefBase = 34; // treble: middle line is B4 (4*7 + 6)
        public int Fifths;
        public Dictionary<char, int> KeyAlters { get; } = new();
        public Dictionary<int, int> MeasureAccidentals { get; } = new();
        public List<(int Verse, string Text)> Lyrics { get; } = new();

        private readonly SortedDictionary<int, PartBuilder> _lower = new();
        private int _barStart;

        // Elapsed ticks in the open bar: voice 1, and each lower voice. Lower voices only sound where an object
        // carries a second voice, so the gaps are filled with rests to keep every voice the length of the bar.
        private int _mainTicks;
        private readonly Dictionary<int, int> _lowerTicks = new();

        public List<NeutralDirection> PendingDirections { get; } = new();
        public bool PendingFermata;
        public bool OpenWedge;

        private bool _slurOpen;
        private NeutralNote? _slurStart, _lastPitched, _lastMain;

        public void AddDirection(string kind, string value, string? unit = null)
        {
            if (kind is DirectionKinds.Dynamics or DirectionKinds.Wedge) CloseWedge();
            if (kind == DirectionKinds.Wedge) OpenWedge = true;
            PendingDirections.Add(new NeutralDirection(kind, value, unit));
        }

        private void CloseWedge()
        {
            if (!OpenWedge) return;
            OpenWedge = false;
            PendingDirections.Add(new NeutralDirection(DirectionKinds.Wedge, "stop"));
        }

        /// <summary>NWC's Slur flag joins a note to the next one: the slur starts on the first flagged note and ends on the note after the last.</summary>
        public void AddMain(NeutralNote note, bool slurred = false)
        {
            if (PendingDirections.Count > 0)
            {
                note.Directions.AddRange(PendingDirections);
                PendingDirections.Clear();
            }
            if (PendingFermata) { note.Fermata = true; PendingFermata = false; }

            if (!note.IsGrace)
            {
                if (_slurOpen)
                {
                    if (note.IsRest) EndSlurAtLastNote();
                    else if (!slurred) { note.SlurStop = true; _slurOpen = false; }
                }
                if (slurred && !note.IsRest && !_slurOpen)
                {
                    note.SlurStart = true;
                    _slurStart = note;
                    _slurOpen = true;
                }
                if (!note.IsRest) _lastPitched = note;
                _mainTicks += note.Ticks;
            }

            _lastMain = note;
            Builder.Add(note);
        }

        private void EndSlurAtLastNote()
        {
            _slurOpen = false;
            if (_lastPitched == null) return;
            if (ReferenceEquals(_lastPitched, _slurStart)) _lastPitched.SlurStart = false;
            else _lastPitched.SlurStop = true;
        }

        /// <summary>Closes marks still open at the end of the staff.</summary>
        public void Finish()
        {
            if (_slurOpen) EndSlurAtLastNote();
            if (OpenWedge && _lastMain != null)
            {
                OpenWedge = false;
                _lastMain.Directions.Add(new NeutralDirection(DirectionKinds.Wedge, "stop"));
            }
        }

        public void AddLower(NeutralNote note, bool align = true)
        {
            if (!_lower.TryGetValue(note.Voice, out var voice))
            {
                voice = Builder.Fork();
                voice.AutoFlush = false;
                _lower[note.Voice] = voice;
            }
            if (align && !note.IsGrace)
            {
                PadLower(note.Voice, _mainTicks);
                // A held lower note may be written longer than the bar; keep it within the bar.
                var room = Builder.CapacityTicks - _lowerTicks.GetValueOrDefault(note.Voice);
                if (room <= 0) return;
                if (note.Ticks > room) note.Ticks = room;
            }
            voice.Add(note);
            if (!note.IsGrace) _lowerTicks[note.Voice] = _lowerTicks.GetValueOrDefault(note.Voice) + note.Ticks;
        }

        private void PadLower(int voiceNumber, int target)
        {
            var gap = target - _lowerTicks.GetValueOrDefault(voiceNumber);
            if (gap <= 0) return;
            _lower[voiceNumber].Add(new NeutralNote { Ticks = gap, Voice = voiceNumber });
            _lowerTicks[voiceNumber] = target;
        }

        /// <summary>Lays lower-voice notes over the measure voice 1 has just closed; does nothing while the bar is open.</summary>
        public void MergeVoices()
        {
            if (Builder.MeasureCount <= _barStart) return;
            foreach (var number in _lower.Keys.ToList())
                if (_lowerTicks.GetValueOrDefault(number) > 0) PadLower(number, _mainTicks);
            _mainTicks = 0;
            _lowerTicks.Clear();
            foreach (var voice in _lower.Values)
            {
                voice.Flush();
                var notes = voice.Part.Measures.SelectMany(m => m.Notes).ToList();
                voice.Part.Measures.Clear();
                if (notes.Count > 0) Builder.Part.Measures[^1].Notes.AddRange(notes);
            }
            _barStart = Builder.MeasureCount;
        }

        /// <summary>Closes the bar (or the file); voices left over with no voice 1 notes become their own measure.</summary>
        public void CloseBar()
        {
            Builder.Flush();
            MergeVoices();
            foreach (var voice in _lower.Values)
            {
                voice.Flush();
                var notes = voice.Part.Measures.SelectMany(m => m.Notes).ToList();
                voice.Part.Measures.Clear();
                if (notes.Count == 0) continue;
                foreach (var note in notes) { note.Voice = 1; Builder.Add(note); }
                Builder.Flush();
                _barStart = Builder.MeasureCount;
            }
        }
    }

    private readonly string[] _lines;
    private readonly ConversionContext _ctx;
    private readonly NeutralScore _score = new();
    private readonly List<Staff> _staves = new();
    private Staff? _staff;
    private int _lineNo;

    public NwcTxtParser(string text, ConversionContext ctx)
    {
        _lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        _ctx = ctx;
    }

    public NeutralScore Parse()
    {
        var firstContent = _lines.FirstOrDefault(l => l.Trim().Length > 0)?.Trim();
        if (firstContent == null || !firstContent.StartsWith("!NoteWorthyComposer", StringComparison.OrdinalIgnoreCase))
            throw _ctx.ParseError("Missing '!NoteWorthyComposer' header; not an NWCTXT file", 1);

        for (var i = 0; i < _lines.Length; i++)
        {
            _ctx.ThrowIfCancelled();
            _ctx.ReportParsing((i + 1) / (double)_lines.Length, "Parsing NWCTXT");
            _lineNo = i + 1;

            var line = _lines[i].Trim();
            if (line.Length == 0 || line[0] == '!') continue;
            if (line[0] != '|') throw _ctx.ParseError($"Unexpected text '{line}'", _lineNo);

            var fields = SplitFields(line);
            HandleObject(fields.Type, fields.Values);
        }

        foreach (var staff in _staves)
        {
            staff.Finish();
            staff.CloseBar();
            if (staff.Builder.MeasureCount == 0) continue;
            var singable = LyricAligner.Singable(staff.Builder.Part.Measures.SelectMany(m => m.Notes).Where(n => n.Voice == 1));
            foreach (var (verse, text) in staff.Lyrics)
                LyricAligner.Apply(singable, LyricAligner.Tokenize(text, underscoreIsSpace: true), verse);
            _score.Parts.Add(staff.Builder.Part);
        }
        return _score;
    }

    private (string Type, Dictionary<string, string> Values) SplitFields(string line)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        var escaped = false;
        foreach (var c in line)
        {
            if (escaped) { escaped = false; current.Append(c); continue; }
            if (c == '\\' && inQuotes) { escaped = true; current.Append(c); continue; }
            if (c == '"') inQuotes = !inQuotes;
            if (c == '|' && !inQuotes)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        tokens.Add(current.ToString());
        if (inQuotes) throw _ctx.ParseError("Unterminated quoted string", _lineNo);

        var type = tokens.Count > 1 ? tokens[1].Trim() : string.Empty;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in tokens.Skip(2))
        {
            var colon = token.IndexOf(':');
            if (colon <= 0) continue;
            values[token[..colon].Trim()] = token[(colon + 1)..].Trim().Trim('"');
        }
        return (type, values);
    }

    private void HandleObject(string type, Dictionary<string, string> v)
    {
        switch (type.ToLowerInvariant())
        {
            case "songinfo":
                if (v.TryGetValue("Title", out var title)) _score.Title ??= title;
                if (v.TryGetValue("Author", out var author)) _score.Composer ??= author;
                break;
            case "addstaff":
                AddStaff(v.TryGetValue("Name", out var name) && name.Length > 0 ? name : null);
                break;
            case "clef":
                HandleClef(v);
                break;
            case "key":
                HandleKey(v);
                break;
            case "timesig":
                HandleTime(v);
                break;
            case "bar":
                var staff = CurrentStaff();
                staff.CloseBar();
                staff.MeasureAccidentals.Clear();
                if (v.TryGetValue("Style", out var barStyle))
                {
                    if (barStyle.Contains("RepeatClose", StringComparison.OrdinalIgnoreCase))
                        staff.Builder.MarkRepeatEnd(v.TryGetValue("Repeat", out var times) && int.TryParse(times, out var n) ? Math.Clamp(n, 2, 64) : 2);
                    if (barStyle.Contains("RepeatOpen", StringComparison.OrdinalIgnoreCase)) staff.Builder.MarkRepeatStart();
                }
                break;
            case "note":
                HandleNote(v, isChord: false);
                break;
            case "chord":
                HandleNote(v, isChord: true);
                break;
            case "rest":
            case "restchord":
                HandleRest(v);
                break;
            case "dynamic":
                if (v.TryGetValue("Style", out var dynStyle) && dynStyle.Length > 0)
                    CurrentStaff().AddDirection(DirectionKinds.Dynamics, dynStyle.ToLowerInvariant());
                break;
            case "dynamicvariance":
                var variance = v.GetValueOrDefault("Style", "").ToLowerInvariant();
                if (variance.StartsWith("cresc")) CurrentStaff().AddDirection(DirectionKinds.Wedge, "crescendo");
                else if (variance.StartsWith("decresc") || variance.StartsWith("dimin"))
                    CurrentStaff().AddDirection(DirectionKinds.Wedge, "diminuendo");
                else if (variance.Length > 0) CurrentStaff().AddDirection(DirectionKinds.Words, v["Style"]);
                break;
            case "tempo":
                HandleTempo(v);
                break;
            case "tempovariance":
                var tv = v.GetValueOrDefault("Style", "");
                if (tv.Equals("Fermata", StringComparison.OrdinalIgnoreCase)) CurrentStaff().PendingFermata = true;
                else if (tv.Length > 0) CurrentStaff().AddDirection(DirectionKinds.Words, tv);
                break;
            case "text":
                if (v.TryGetValue("Text", out var words) && words.Length > 0)
                    CurrentStaff().AddDirection(DirectionKinds.Words, words);
                break;
            case "performancestyle":
                if (v.TryGetValue("Style", out var perf) && perf.Length > 0)
                    CurrentStaff().AddDirection(DirectionKinds.Words, perf);
                break;
            case "sustainpedal":
                var released = v.GetValueOrDefault("Status", "").Equals("Released", StringComparison.OrdinalIgnoreCase);
                CurrentStaff().AddDirection(DirectionKinds.Pedal, released ? "stop" : "start");
                break;
            case "lyrics":
                break;
            case var t when t.Length > 5 && t.StartsWith("lyric", StringComparison.Ordinal) && int.TryParse(t[5..], out var verse):
                if (v.TryGetValue("Text", out var lyric))
                    CurrentStaff().Lyrics.Add((verse, System.Text.RegularExpressions.Regex.Replace(
                        lyric.Replace("\\n", " ").Replace("\\r", " "), @"^\s*\d+[.:)]_*\s*", "")));
                break;
            default:
                if (_warnedTypes.Add(type)) _ctx.Warn($"Unsupported NWCTXT object '{type}' ignored.");
                break;
        }
    }

    private readonly HashSet<string> _warnedTypes = new(StringComparer.OrdinalIgnoreCase);

    private void HandleTempo(Dictionary<string, string> v)
    {
        if (!v.TryGetValue("Tempo", out var text) || !double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var bpm) || bpm <= 0) return;
        var unit = "quarter";
        if (v.TryGetValue("Base", out var b))
        {
            var lower = b.ToLowerInvariant();
            if (lower.Contains("dotted")) { bpm *= 1.5; }
            else if (lower.Contains("eighth")) unit = "eighth";
            else if (lower.Contains("half")) unit = "half";
        }
        CurrentStaff().AddDirection(DirectionKinds.Tempo, Math.Round(bpm).ToString(System.Globalization.CultureInfo.InvariantCulture), unit);
    }

    private void AddStaff(string? name)
    {
        var builder = new PartBuilder(name ?? $"Staff {_staves.Count + 1}");
        builder.SetTime(4, 4);
        builder.SetKey(0);
        builder.SetClef("G", 2);
        var staff = new Staff(builder);
        builder.MeasureCompleted += staff.MeasureAccidentals.Clear;
        _staves.Add(staff);
        _staff = staff;
    }

    private Staff CurrentStaff()
    {
        if (_staff == null) AddStaff(null);
        return _staff!;
    }

    private void HandleClef(Dictionary<string, string> v)
    {
        var staff = CurrentStaff();
        var type = v.GetValueOrDefault("Type", "Treble");
        var octaveShift = 0;
        if (v.TryGetValue("OctaveShift", out var shift) && !shift.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            if (shift.Contains("Up", StringComparison.OrdinalIgnoreCase)) octaveShift = 1;
            else if (shift.Contains("Down", StringComparison.OrdinalIgnoreCase)) octaveShift = -1;
            else _ctx.Warn($"Unsupported clef octave shift '{shift}' ignored.");
        }

        switch (type.ToLowerInvariant())
        {
            case "treble": staff.Builder.SetClef("G", 2); staff.ClefBase = 4 * 7 + 6; break;
            case "bass": staff.Builder.SetClef("F", 4); staff.ClefBase = 3 * 7 + 1; break;
            case "alto": staff.Builder.SetClef("C", 3); staff.ClefBase = 4 * 7; break;
            case "tenor": staff.Builder.SetClef("C", 4); staff.ClefBase = 3 * 7 + 5; break;
            default:
                _ctx.Warn($"Unsupported clef '{type}'; treble clef assumed.");
                staff.Builder.SetClef("G", 2);
                staff.ClefBase = 4 * 7 + 6;
                break;
        }

        // Octave clefs (e.g. guitar) sound an octave away from where the notes are written.
        staff.ClefBase += 7 * octaveShift;
    }

    private void HandleKey(Dictionary<string, string> v)
    {
        var staff = CurrentStaff();
        staff.KeyAlters.Clear();
        var sharps = 0;
        var flats = 0;
        foreach (var token in v.GetValueOrDefault("Signature", string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = token.Trim();
            if (t.Equals("C", StringComparison.OrdinalIgnoreCase) || t.Equals("None", StringComparison.OrdinalIgnoreCase)) continue;
            if (t.Length < 2 || "ABCDEFG".IndexOf(char.ToUpperInvariant(t[0])) < 0 || t[1] is not ('#' or 'b'))
                throw _ctx.ParseError($"Invalid key signature entry '{t}'", _lineNo);
            var alter = t[1] == '#' ? 1 : -1;
            staff.KeyAlters[char.ToUpperInvariant(t[0])] = alter;
            if (alter > 0) sharps++; else flats++;
        }
        staff.Fifths = sharps - flats;
        staff.Builder.SetKey(staff.Fifths);
    }

    private void HandleTime(Dictionary<string, string> v)
    {
        var staff = CurrentStaff();
        var signature = v.GetValueOrDefault("Signature", "4/4");
        if (signature.Equals("Common", StringComparison.OrdinalIgnoreCase)) { staff.Builder.SetTime(4, 4); return; }
        if (signature.Equals("AllaBreve", StringComparison.OrdinalIgnoreCase)) { staff.Builder.SetTime(2, 2); return; }

        var parts = signature.Split('/');
        if (parts.Length == 2 && int.TryParse(parts[0], out var beats) && int.TryParse(parts[1], out var beatType)
            && beats > 0 && beatType > 0)
        {
            staff.Builder.SetTime(beats, beatType);
            return;
        }
        throw _ctx.ParseError($"Invalid time signature '{signature}'", _lineNo);
    }

    private bool TryDuration(Dictionary<string, string> v, out int ticks, out bool triplet)
    {
        ticks = 0;
        triplet = false;
        if (!v.TryGetValue("Dur", out var dur)) throw _ctx.ParseError("Missing 'Dur' field", _lineNo);

        var tokens = dur.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) throw _ctx.ParseError("Empty 'Dur' field", _lineNo);

        var value = tokens[0].ToLowerInvariant() switch
        {
            "whole" or "1st" => 1,
            "half" or "2nd" => 2,
            "quarter" or "4th" => 4,
            "8th" => 8,
            "16th" => 16,
            "32nd" => 32,
            "64th" => 64,
            _ => throw _ctx.ParseError($"Unknown duration '{tokens[0]}'", _lineNo)
        };

        var dots = 0;
        foreach (var modifier in tokens.Skip(1))
        {
            if (modifier.Equals("Dotted", StringComparison.OrdinalIgnoreCase)) dots = 1;
            else if (modifier.Equals("DblDotted", StringComparison.OrdinalIgnoreCase)) dots = 2;
            else if (modifier.StartsWith("Triplet", StringComparison.OrdinalIgnoreCase)) triplet = true;
        }

        ticks = DurationHelper.TicksFor(value, dots);
        return true;
    }

    /// <summary>Applies the articulation and grace modifiers of a <c>Dur</c> field; returns true when the note is slurred to the next.</summary>
    private static bool ApplyDurFlags(NeutralNote note, string? dur)
    {
        var slurred = false;
        if (dur == null) return false;
        foreach (var token in dur.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (token.ToLowerInvariant())
            {
                case "staccato": note.Articulations.Add("staccato"); break;
                case "accent": note.Articulations.Add("accent"); break;
                case "tenuto": note.Articulations.Add("tenuto"); break;
                case "grace": note.IsGrace = true; break;
                case "slur": slurred = true; break;
            }
        }
        return slurred;
    }

    private void HandleRest(Dictionary<string, string> v)
    {
        var staff = CurrentStaff();
        if (!TryDuration(v, out var ticks, out var triplet)) return;
        var note = new NeutralNote { Ticks = ticks };
        AddWithSecondVoice(staff, note, triplet, v, null);
    }

    private void HandleNote(Dictionary<string, string> v, bool isChord)
    {
        var staff = CurrentStaff();
        if (!v.TryGetValue("Pos", out var pos) || pos.Length == 0)
            throw _ctx.ParseError("Missing 'Pos' field", _lineNo);
        if (!TryDuration(v, out var ticks, out var triplet)) return;

        var note = new NeutralNote { Ticks = ticks };
        ReadPositions(staff, note, pos, isChord);
        AddWithSecondVoice(staff, note, triplet, v, isChord);
    }

    private void ReadPositions(Staff staff, NeutralNote note, string pos, bool isChord)
    {
        var entries = isChord ? pos.Split(',', StringSplitOptions.RemoveEmptyEntries) : new[] { pos };
        foreach (var entry in entries)
        {
            var match = PositionRegex.Match(entry.Trim());
            if (!match.Success) throw _ctx.ParseError($"Invalid note position '{entry}'", _lineNo);

            var index = staff.ClefBase + int.Parse(match.Groups[2].Value);
            if (index < 0) throw _ctx.ParseError($"Note position '{entry}' is out of range", _lineNo);
            var step = "CDEFGAB"[index % 7];

            int alter;
            if (match.Groups[1].Length > 0)
            {
                alter = match.Groups[1].Value switch { "#" => 1, "b" => -1, "x" => 2, "v" => -2, _ => 0 };
                staff.MeasureAccidentals[index] = alter;
            }
            else if (!staff.MeasureAccidentals.TryGetValue(index, out alter))
            {
                alter = staff.KeyAlters.GetValueOrDefault(step);
            }

            note.Pitches.Add(new NeutralPitch(step, alter, index / 7));
            if (match.Groups[3].Length > 0) note.TieToNext = true;
        }
    }

    /// <summary>
    /// Adds the object, plus its lower voice when the object carries <c>Dur2</c>/<c>Pos2</c> (NWC's second voice on the
    /// same staff), or places the whole object in the voice named by <c>Opts</c> <c>VoiceNum</c>.
    /// </summary>
    private void AddWithSecondVoice(Staff staff, NeutralNote note, bool triplet, Dictionary<string, string> v, bool? isChord)
    {
        var voice = 1;
        if (v.TryGetValue("Opts", out var opts))
        {
            var m = Regex.Match(opts, @"Voice(?:Num)?\s*=\s*(\d+)", RegexOptions.IgnoreCase);
            if (m.Success && int.Parse(m.Groups[1].Value) is > 1 and var n) voice = n;
        }

        if (v.ContainsKey("Dur2") || v.ContainsKey("Pos2"))
        {
            var lowerValues = new Dictionary<string, string>(v, StringComparer.OrdinalIgnoreCase);
            if (v.TryGetValue("Dur2", out var dur2)) lowerValues["Dur"] = dur2;
            if (TryDuration(lowerValues, out var lowerTicks, out var lowerTriplet))
            {
                var lower = new NeutralNote { Ticks = lowerTicks, Voice = 2 };
                if (v.TryGetValue("Pos2", out var pos2) && pos2.Length > 0) ReadPositions(staff, lower, pos2, true);
                ApplyDurFlags(lower, dur2);
                staff.AddLower(Finish(lower, lowerTriplet));
            }
        }

        note.Voice = voice;
        var slurred = ApplyDurFlags(note, v.GetValueOrDefault("Dur"));
        if (voice > 1) staff.AddLower(Finish(note, triplet), align: false);
        else staff.AddMain(Finish(note, triplet), slurred);
        staff.MergeVoices();
    }

    private static NeutralNote Finish(NeutralNote note, bool triplet)
    {
        if (triplet)
        {
            note.Ticks = note.Ticks * 2 / 3;
            note.TupletActual = 3;
            note.TupletNormal = 2;
        }
        return note;
    }
}
