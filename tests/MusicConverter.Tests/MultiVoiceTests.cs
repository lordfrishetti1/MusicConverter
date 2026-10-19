using MusicConverter.Converters;
using Xunit;

namespace MusicConverter.Tests;

public class MultiVoiceTests
{
    [Fact]
    public async Task MeiLayersBecomeVoices()
    {
        using var dir = new TempDir();
        var path = dir.Write("v.mei", @"<?xml version=""1.0""?>
<mei xmlns=""http://www.music-encoding.org/ns/mei"" meiversion=""5.0""><music><body><mdiv><score>
<scoreDef meter.count=""2"" meter.unit=""4"" key.sig=""0""><staffGrp><staffDef n=""1"" clef.shape=""G"" clef.line=""2""/></staffGrp></scoreDef>
<section><measure n=""1""><staff n=""1"">
<layer n=""1""><note pname=""c"" oct=""5"" dur=""2""/></layer>
<layer n=""2""><note pname=""e"" oct=""4"" dur=""4""/><note pname=""g"" oct=""4"" dur=""4""/></layer>
</staff></measure></section></score></mdiv></body></music></mei>");
        var doc = await new MeiConverter().ConvertAsync(path, null);
        var notes = doc.Parts.Single().Measures.Single().Notes;
        Assert.Equal(new[] { 1, 2, 2 }, notes.Select(n => n.Voice).ToArray());
        Assert.Equal(960, notes[1].BackupBefore);
        Assert.Equal(0, notes[2].BackupBefore);
    }

    [Fact]
    public async Task MupSecondVoiceIsKept()
    {
        using var dir = new TempDir();
        var path = dir.Write("v.mup", "score\n\ttime=2/4\n\tstaffs=1\n\nmusic\n1 1: 2c+;\n1 2: 4e; g;\nendbar\n");
        var doc = await new MupConverter().ConvertAsync(path, null);
        var notes = doc.Parts.Single().Measures.Single().Notes;
        Assert.Equal(new[] { 1, 2, 2 }, notes.Select(n => n.Voice).ToArray());
        Assert.Equal(960, notes[1].BackupBefore);
    }

    [Fact]
    public async Task NwcTxtSecondVoiceIsKept()
    {
        using var dir = new TempDir();
        var path = dir.Write("v.nwctxt",
            "!NoteWorthyComposer(2.75)\n|AddStaff|Name:\"S\"\n|Clef|Type:Treble\n|TimeSig|Signature:2/4\n" +
            "|Chord|Dur:Half|Pos:0|Dur2:Quarter|Pos2:-3\n|Chord|Dur:Half|Pos:1|Dur2:Quarter|Pos2:-2\n|Bar\n!NoteWorthyComposer-End\n");
        var doc = await new NwcTxtConverter().ConvertAsync(path, null);
        var measures = doc.Parts.Single().Measures;
        var notes = measures[0].Notes;
        Assert.Equal(new[] { 1, 2, 2 }, notes.Select(n => n.Voice).ToArray());
        Assert.Equal(960, notes[0].Duration);
        Assert.Equal(480, notes[1].Duration);
        Assert.Equal(960, notes[1].BackupBefore);
    }

    [Fact]
    public async Task NwcTxtLowerVoiceIsPaddedWithRestsAndKeyCIsAccepted()
    {
        using var dir = new TempDir();
        var path = dir.Write("p.nwctxt",
            "!NoteWorthyComposer(2.75)\n|SongInfo|Title:\"A \\\"q\\\"\"\n|AddStaff|Name:\"S\"\n|Clef|Type:Treble\n|Key|Signature:C|Tonic:C\n|TimeSig|Signature:4/4\n" +
            "|Note|Dur:Half|Pos:0\n|Chord|Dur:Half|Pos:1|Dur2:Whole|Pos2:-3\n|Bar\n!NoteWorthyComposer-End\n");
        var doc = await new NwcTxtConverter().ConvertAsync(path, null);
        var notes = doc.Parts.Single().Measures[0].Notes;
        Assert.Equal(1920, notes.Where(n => n.Voice == 1).Sum(n => n.Duration));
        var lower = notes.Where(n => n.Voice == 2).ToList();
        Assert.Equal(1920, lower.Sum(n => n.Duration));
        Assert.True(lower[0].IsRest);
    }

    [Fact]
    public async Task HumdrumSpineSplitBecomesSecondVoice()
    {
        using var dir = new TempDir();
        var path = dir.Write("v.krn",
            "**kern\n*clefG2\n*M2/4\n*^\n" +
            "2c\t4e\n.\t4g\n=1\t=1\n*v\t*v\n*-\n");
        var doc = await new HumdrumConverter().ConvertAsync(path, null);
        var notes = doc.Parts.Single().Measures.Single().Notes;
        Assert.Equal(new[] { 1, 2, 2 }, notes.Select(n => n.Voice).ToArray());
        Assert.Equal(960, notes[1].BackupBefore);
    }

    [Fact]
    public async Task LilyPondVoicesStartingMidMeasureAreKept()
    {
        using var dir = new TempDir();
        var path = dir.Write("v.ly", "\\relative c' { \\time 4/4 c4 << { d2. } \\\\ { b2. } >> c1 }");
        var doc = await new LilyPondConverter().ConvertAsync(path, null);
        var first = doc.Parts.Single().Measures[0].Notes;
        Assert.Equal(new[] { 1, 1, 2, 2 }, first.Select(n => n.Voice).ToArray());
        Assert.Equal(480, first[2].Duration);
        Assert.True(first[2].IsRest);
        Assert.Equal(1440, first[3].Duration);
    }
}
