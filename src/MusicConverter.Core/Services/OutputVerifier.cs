using System.Xml;
using System.Xml.Linq;
using MusicConverter.MusicXml;

namespace MusicConverter.Services;

/// <summary>
/// Compares a converted document with the MusicXML file written for it and, where a cheap independent count
/// exists (MIDI), with the source file. It reports differences; it never changes the output.
/// </summary>
public static class OutputVerifier
{
    private const int MaxIssues = 25;

    /// <returns>Human-readable differences; empty when everything matches.</returns>
    public static IReadOnlyList<string> Verify(MusicXmlDocument document, string inputPath, string outputPath)
    {
        var issues = new List<string>();
        try
        {
            VerifyOutputFile(document, outputPath, issues);
        }
        catch (Exception ex) when (ex is IOException or XmlException or UnauthorizedAccessException or FormatException)
        {
            issues.Add($"The written file could not be read back: {ex.Message}");
        }

        try
        {
            VerifySource(document, inputPath, issues);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            issues.Add($"The source file could not be re-read for comparison: {ex.Message}");
        }

        return issues.Take(MaxIssues).ToArray();
    }

    private static void VerifyOutputFile(MusicXmlDocument document, string outputPath, List<string> issues)
    {
        XDocument xml;
        using (var reader = XmlReader.Create(outputPath, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null }))
            xml = XDocument.Load(reader);

        var root = xml.Root;
        if (root is not { Name.LocalName: "score-partwise" })
        {
            issues.Add($"Output root element is '{root?.Name.LocalName}', expected 'score-partwise'.");
            return;
        }

        var parts = root.Elements("part").ToList();
        if (parts.Count != document.Parts.Count)
        {
            issues.Add($"Output has {parts.Count} part(s), the converted score has {document.Parts.Count}.");
            return;
        }

        for (var p = 0; p < parts.Count; p++)
        {
            var model = document.Parts[p];
            var file = parts[p];
            var label = $"Part {p + 1} ('{model.Name}')";

            var fileMeasures = file.Elements("measure").ToList();
            if (fileMeasures.Count != model.Measures.Count)
                issues.Add($"{label}: {fileMeasures.Count} measure(s) written, {model.Measures.Count} expected.");

            var modelNotes = model.Measures.SelectMany(m => m.Notes).ToList();
            var fileNotes = fileMeasures.SelectMany(m => m.Elements("note")).ToList();

            Compare(issues, label, "notes", modelNotes.Count(n => !n.IsRest), fileNotes.Count(n => n.Element("rest") == null));
            Compare(issues, label, "rests", modelNotes.Count(n => n.IsRest), fileNotes.Count(n => n.Element("rest") != null));
            Compare(issues, label, "chord notes", modelNotes.Count(n => n.IsChord), fileNotes.Count(n => n.Element("chord") != null));
            Compare(issues, label, "grace notes", modelNotes.Count(n => n.IsGrace), fileNotes.Count(n => n.Element("grace") != null));
            Compare(issues, label, "ties", modelNotes.Count(n => n.TieStart), fileNotes.Count(n => n.Elements("tie").Any(t => (string?)t.Attribute("type") == "start")));

            var modelDuration = modelNotes.Where(n => !n.IsChord && !n.IsGrace).Sum(n => (long)n.Duration);
            var fileDuration = fileNotes.Where(n => n.Element("chord") == null && n.Element("grace") == null)
                .Sum(n => (long?)int.Parse((string?)n.Element("duration") ?? "0", System.Globalization.CultureInfo.InvariantCulture) ?? 0);
            Compare(issues, label, "total duration (divisions)", modelDuration, fileDuration);

            var modelPitches = modelNotes.Where(n => n.Pitch != null).Select(n => $"{n.Pitch!.Step}{n.Pitch.Alter}/{n.Pitch.Octave}");
            var filePitches = fileNotes.Select(n => n.Element("pitch")).Where(p2 => p2 != null)
                .Select(p2 => $"{(string?)p2!.Element("step")}{(string?)p2.Element("alter") ?? "0"}/{(string?)p2.Element("octave")}");
            var pitchIndex = FirstDifference(modelPitches, filePitches);
            if (pitchIndex >= 0) issues.Add($"{label}: pitch sequence differs from the converted score at note {pitchIndex + 1}.");
        }
    }

    private static void VerifySource(MusicXmlDocument document, string inputPath, List<string> issues)
    {
        var ext = Path.GetExtension(inputPath);
        if (!ext.Equals(".mid", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".midi", StringComparison.OrdinalIgnoreCase)) return;

        var sourceNotes = MidiNoteCounter.Count(File.ReadAllBytes(inputPath));
        if (sourceNotes is null) return;

        // Notes split across barlines are written as tied pieces; only the first piece is a new note.
        var written = document.Parts.SelectMany(p => p.Measures).SelectMany(m => m.Notes).Count(n => !n.IsRest && !n.TieStop);
        if (written != sourceNotes)
            issues.Add($"The MIDI source has {sourceNotes} note(s), the output has {written}.");
    }

    private static void Compare(List<string> issues, string label, string what, long expected, long actual)
    {
        if (expected != actual) issues.Add($"{label}: {actual} {what} written, {expected} expected.");
    }

    private static int FirstDifference(IEnumerable<string> expected, IEnumerable<string> actual)
    {
        using var a = expected.GetEnumerator();
        using var b = actual.GetEnumerator();
        for (var i = 0; ; i++)
        {
            var hasA = a.MoveNext();
            var hasB = b.MoveNext();
            if (!hasA && !hasB) return -1;
            if (hasA != hasB || a.Current != b.Current) return i;
        }
    }
}

/// <summary>Counts note-on events in a Standard MIDI File without sharing any code with the converter's parser.</summary>
internal static class MidiNoteCounter
{
    /// <returns>The number of note-ons with non-zero velocity, or null when the file cannot be walked reliably.</returns>
    public static int? Count(byte[] data)
    {
        if (data.Length < 14 || data[0] != 'M' || data[1] != 'T' || data[2] != 'h' || data[3] != 'd') return null;
        var position = 8 + ReadInt32(data, 4);
        var count = 0;

        while (position + 8 <= data.Length)
        {
            var isTrack = data[position] == 'M' && data[position + 1] == 'T' && data[position + 2] == 'r' && data[position + 3] == 'k';
            var length = ReadInt32(data, position + 4);
            var start = position + 8;
            var end = (int)Math.Min((long)start + length, data.Length);
            position = end;
            if (!isTrack) continue;

            var i = start;
            byte running = 0;
            while (i < end)
            {
                if (!SkipVariableLength(data, ref i, end)) return null;
                if (i >= end) return null;
                var b = data[i];
                byte status;
                if ((b & 0x80) != 0)
                {
                    status = b;
                    i++;
                    if (status < 0xF0) running = status;
                    else if (status is 0xF0 or 0xF7) running = 0;
                }
                else
                {
                    if (running == 0) return null;
                    status = running;
                }

                if (status == 0xFF)
                {
                    if (i >= end) return null;
                    i++;
                    var size = ReadVariableLength(data, ref i, end);
                    if (size < 0) return null;
                    i += size;
                }
                else if (status is 0xF0 or 0xF7)
                {
                    var size = ReadVariableLength(data, ref i, end);
                    if (size < 0) return null;
                    i += size;
                }
                else if (status >= 0xF0)
                {
                    return null;
                }
                else
                {
                    var command = status & 0xF0;
                    var dataBytes = command is 0xC0 or 0xD0 ? 1 : 2;
                    if (i + dataBytes > end) return null;
                    if (command == 0x90 && data[i + 1] > 0) count++;
                    i += dataBytes;
                }
            }
        }

        return count;
    }

    private static int ReadInt32(byte[] d, int p) => (d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3];

    private static bool SkipVariableLength(byte[] d, ref int i, int end) => ReadVariableLength(d, ref i, end) >= 0;

    private static int ReadVariableLength(byte[] d, ref int i, int end)
    {
        long value = 0;
        for (var n = 0; n < 4; n++)
        {
            if (i >= end) return -1;
            var b = d[i++];
            value = (value << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) == 0) return value > int.MaxValue ? -1 : (int)value;
        }

        return -1;
    }
}
