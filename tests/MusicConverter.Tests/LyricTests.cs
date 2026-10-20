using MusicConverter.Converters;
using MusicConverter.MusicXml;
using Xunit;

namespace MusicConverter.Tests;

public class LyricTests
{
    private static string Lyrics(MusicXmlDocument doc) =>
        string.Join(" ", doc.Parts.SelectMany(p => p.Measures).SelectMany(m => m.Notes)
            .Where(n => n.Voice == 1).SelectMany(n => n.Lyrics).Select(l => $"{l.Text}:{l.Syllabic}"));

    [Fact]
    public async Task AbcWLinesBecomeVerses()
    {
        using var dir = new TempDir();
        var path = dir.Write("l.abc", "X:1\nT:t\nM:4/4\nL:1/4\nK:C\nCDEF|G z A B|\nw:Hel-lo to you * now\nw:one two three four five six\n");
        var doc = await new AbcConverter().ConvertAsync(path, null);
        var notes = doc.Parts.Single().Measures.SelectMany(m => m.Notes).Where(n => !n.IsRest).ToList();
        Assert.Equal(new[] { "Hel", "lo", "to", "you" }, notes.Take(4).Select(n => n.Lyrics[0].Text).ToArray());
        Assert.Equal(new[] { "begin", "end", "single", "single" }, notes.Take(4).Select(n => n.Lyrics[0].Syllabic).ToArray());
        Assert.Empty(notes[4].Lyrics.Where(l => l.Number == 1));
        Assert.Equal("one", notes[0].Lyrics.Single(l => l.Number == 2).Text);
    }

    [Fact]
    public async Task NwcTxtLyricObjectsAreAligned()
    {
        using var dir = new TempDir();
        var path = dir.Write("l.nwctxt",
            "!NoteWorthyComposer(2.75)\n|AddStaff|Name:\"S\"\n|Clef|Type:Treble\n|TimeSig|Signature:4/4\n" +
            "|Note|Dur:4th|Pos:0\n|Note|Dur:4th|Pos:1\n|Rest|Dur:4th\n|Note|Dur:4th|Pos:2\n|Bar\n" +
            "|Lyrics|Placement:Bottom\n|Lyric1|Text:\"1._A a-men\\n\"\n!NoteWorthyComposer-End\n");
        var doc = await new NwcTxtConverter().ConvertAsync(path, null);
        Assert.Equal("A:single a:begin men:end", Lyrics(doc));
    }

    [Fact]
    public async Task MeiVersesAreRead()
    {
        using var dir = new TempDir();
        var path = dir.Write("l.mei", @"<?xml version=""1.0""?>
<mei xmlns=""http://www.music-encoding.org/ns/mei"" meiversion=""5.0""><music><body><mdiv><score>
<scoreDef meter.count=""2"" meter.unit=""4"" key.sig=""0""><staffGrp><staffDef n=""1"" clef.shape=""G"" clef.line=""2""/></staffGrp></scoreDef>
<section><measure n=""1""><staff n=""1""><layer n=""1"">
<note pname=""c"" oct=""4"" dur=""4""><verse n=""1""><syl wordpos=""i"" con=""d"">Hel</syl></verse></note>
<note pname=""d"" oct=""4"" dur=""4""><verse n=""1""><syl wordpos=""t"">lo</syl></verse></note>
</layer></staff></measure></section></score></mdiv></body></music></mei>");
        var doc = await new MeiConverter().ConvertAsync(path, null);
        Assert.Equal("Hel:begin lo:end", Lyrics(doc));
    }

    [Fact]
    public async Task HumdrumTextSpineIsAligned()
    {
        using var dir = new TempDir();
        var path = dir.Write("l.krn",
            "**kern\t**text\n*clefG2\t*\n*M2/4\t*\n=1\t=1\n4c\tHel-\n4d\t-lo\n=2\t=2\n4e\t.\n4r\t.\n*-\t*-\n");
        var doc = await new HumdrumConverter().ConvertAsync(path, null);
        Assert.Equal("Hel:begin lo:end", Lyrics(doc));
    }

    [Fact]
    public async Task MupLyricsStatementIsAligned()
    {
        using var dir = new TempDir();
        var path = dir.Write("l.mup", "score\n\ttime=2/4\n\tstaffs=1\n\nmusic\n1: 4c; d;\nbar\n1: 4e; f;\nlyrics 1: \"Hel-\" \"lo\" \"to\" \"you\";\nendbar\n");
        var doc = await new MupConverter().ConvertAsync(path, null);
        Assert.Equal("Hel:begin lo:end to:single you:single", Lyrics(doc));
    }
}
