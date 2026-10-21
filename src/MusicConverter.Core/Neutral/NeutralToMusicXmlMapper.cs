using MusicConverter.Abstractions;
using MusicConverter.MusicXml;

namespace MusicConverter.Neutral;

/// <summary>Maps the neutral representation to the version-neutral MusicXML model.</summary>
public sealed class NeutralToMusicXmlMapper
{
    public MusicXmlDocument Map(
        NeutralScore score, Action<double, string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(score);
        if (score.Parts.Count == 0 || score.Parts.All(p => p.Measures.Count == 0))
            throw new ConversionException("The score contains no music to map.", ConversionErrorCategory.MappingError);

        var document = new MusicXmlDocument { Title = score.Title, Composer = score.Composer };
        var total = score.Parts.Sum(p => p.Measures.Count);
        var done = 0;

        for (var partIndex = 0; partIndex < score.Parts.Count; partIndex++)
        {
            var source = score.Parts[partIndex];
            var part = new MusicXmlPart { Id = $"P{partIndex + 1}", Name = source.Name };
            var previousByVoice = new Dictionary<int, NeutralNote?>();

            foreach (var measure in source.Measures)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = new MusicXmlMeasure
                {
                    Number = measure.Number,
                    Attributes = MapAttributes(measure, isFirst: part.Measures.Count == 0),
                    RepeatStart = measure.RepeatStart,
                    RepeatEnd = measure.RepeatEnd,
                    RepeatTimes = measure.RepeatTimes,
                    EndingNumbers = measure.EndingNumbers,
                    EndingStart = measure.EndingStart,
                    EndingStop = measure.EndingStop
                };

                var backup = 0;
                foreach (var voice in measure.Notes.GroupBy(n => n.Voice).OrderBy(g => g.Key))
                {
                    var firstIndex = target.Notes.Count;
                    var length = 0;
                    foreach (var note in voice)
                    {
                        previousByVoice.TryGetValue(voice.Key, out var previous);
                        MapNote(note, previous, target, part.Id, measure.Number);
                        if (note.IsGrace) continue;
                        previousByVoice[voice.Key] = note;
                        length += note.Ticks;
                    }

                    if (backup > 0 && target.Notes.Count > firstIndex) target.Notes[firstIndex].BackupBefore = backup;
                    backup = length;
                }
                MarkTuplets(target);
                part.Measures.Add(target);
                progress?.Invoke(++done / (double)total, $"Mapped measure {measure.Number} of {part.Name}");
            }

            document.Parts.Add(part);
        }

        return document;
    }

    /// <summary>
    /// Sets tuplet start/stop per voice. A group ends when the ratio changes, a non-tuplet note follows, or its
    /// sounding length equals <c>normal</c> notes of a plain note value (e.g. 480 = 2 eighths for 3:2).
    /// </summary>
    private static void MarkTuplets(MusicXmlMeasure measure)
    {
        foreach (var voice in measure.Notes.Where(n => !n.IsGrace && !n.IsChord).GroupBy(n => n.Voice))
        {
            MusicXmlNote? first = null, last = null;
            var duration = 0;
            var count = 0;

            void Close()
            {
                if (first != null && last != null && first != last)
                {
                    first.TupletStart = true;
                    last.TupletStop = true;
                }
                first = last = null;
                duration = count = 0;
            }

            foreach (var note in voice)
            {
                if (note.TupletActual is not int actual || note.TupletNormal is not int normal || normal <= 0)
                {
                    Close();
                    continue;
                }

                if (first != null && (first.TupletActual != actual || first.TupletNormal != normal)) Close();
                first ??= note;
                last = note;
                duration += note.Duration;
                count++;

                var unit = duration % normal == 0 ? duration / normal : 0;
                if (count >= actual || IsPlainNoteValue(unit)) Close();
            }
            Close();
        }
    }

    private static bool IsPlainNoteValue(int ticks) =>
        ticks > 0 && DurationHelper.WholeTicks % ticks == 0 && DurationHelper.IsValidNoteValue(DurationHelper.WholeTicks / ticks);

    private static MusicXmlAttributes? MapAttributes(NeutralMeasure measure, bool isFirst)
    {
        var a = measure.Attributes;
        if (isFirst)
        {
            return new MusicXmlAttributes
            {
                Divisions = DurationHelper.TicksPerQuarter,
                Fifths = a?.Fifths ?? 0,
                Beats = a?.Beats ?? 4,
                BeatType = a?.BeatType ?? 4,
                ClefSign = a?.ClefSign ?? "G",
                ClefLine = a?.ClefLine ?? (a?.ClefSign == "percussion" ? null : 2)
            };
        }

        if (a is null) return null;
        return new MusicXmlAttributes
        {
            Fifths = a.Fifths,
            Beats = a.Beats,
            BeatType = a.BeatType,
            ClefSign = a.ClefSign,
            ClefLine = a.ClefLine
        };
    }

    private static void MapNote(NeutralNote note, NeutralNote? previous, MusicXmlMeasure target, string partId, int measureNumber)
    {
        if (note.Ticks <= 0)
            throw Fail($"Part {partId}, measure {measureNumber}: note has non-positive duration {note.Ticks}.");

        var written = note.Ticks;
        if (note.TupletActual is int actual && note.TupletNormal is int normal && normal > 0)
            written = (int)Math.Round(note.Ticks * (double)actual / normal);
        var (type, dots) = DurationHelper.Describe(written);

        if (note.IsRest)
        {
            if (note.IsGrace) return;
            target.Notes.Add(NewNote(note, type, dots, null, false, false, false, first: true));
            return;
        }

        var tiedIn = previous is { TieToNext: true, IsRest: false }
            ? previous.Pitches.Select(Key).ToHashSet()
            : new HashSet<string>();

        for (var i = 0; i < note.Pitches.Count; i++)
        {
            var p = note.Pitches[i];
            if ("ABCDEFG".IndexOf(p.Step) < 0 || p.Octave is < 0 or > 9 || p.Alter is < -2 or > 2)
                throw Fail($"Part {partId}, measure {measureNumber}: invalid pitch {p.Step}{p.Alter:+0;-0;0}/{p.Octave}.");

            target.Notes.Add(NewNote(note, type, dots, new MusicXmlPitch(p.Step, p.Alter, p.Octave),
                i > 0, note.TieToNext, tiedIn.Contains(Key(p)), first: i == 0));
        }
    }

    private static MusicXmlNote NewNote(
        NeutralNote note, string type, int dots, MusicXmlPitch? pitch, bool chord, bool tieStart, bool tieStop, bool first)
    {
        var result = new MusicXmlNote
        {
            IsRest = pitch is null,
            Pitch = pitch,
            IsChord = chord,
            Duration = note.IsGrace ? 0 : note.Ticks,
            Type = type,
            Dots = dots,
            TieStart = tieStart,
            TieStop = tieStop,
            TupletActual = note.IsGrace ? null : note.TupletActual,
            TupletNormal = note.IsGrace ? null : note.TupletNormal,
            Voice = note.Voice,
            IsGrace = note.IsGrace,
            GraceSlash = note.GraceSlash
        };
        if (first)
        {
            result.SlurStart = note.SlurStart;
            result.SlurStop = note.SlurStop;
            result.Fermata = note.Fermata;
            result.Articulations.AddRange(note.Articulations.Distinct());
            result.Ornaments.AddRange(note.Ornaments.Distinct());
            foreach (var d in note.Directions) result.Directions.Add(new MusicXmlDirection(d.Kind, d.Value, d.Unit));
            foreach (var lyric in note.Lyrics)
                result.Lyrics.Add(new MusicXmlLyric(lyric.Number, lyric.Text, lyric.Syllabic));
        }
        return result;
    }

    private static string Key(NeutralPitch p) => $"{p.Step}{p.Alter}/{p.Octave}";

    private static ConversionException Fail(string message) =>
        new(message, ConversionErrorCategory.MappingError);
}
