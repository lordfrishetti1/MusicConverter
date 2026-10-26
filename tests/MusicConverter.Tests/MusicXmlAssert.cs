using MusicConverter.MusicXml;

namespace MusicConverter.Tests;

public static class MusicXmlAssert
{
    public static IEnumerable<MusicXmlNote> AllNotes(this MusicXmlDocument doc) =>
        doc.Parts.SelectMany(p => p.Measures).SelectMany(m => m.Notes);

    public static string Describe(this MusicXmlNote n) =>
        n.IsRest ? $"r{n.Duration}" : $"{n.Pitch!.Step}{(n.Pitch.Alter == 0 ? "" : n.Pitch.Alter.ToString("+0;-0"))}{n.Pitch.Octave}:{n.Duration}";
}