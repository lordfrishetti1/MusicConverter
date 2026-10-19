using MusicConverter.Abstractions;
using MusicConverter.Converters;
using MusicConverter.Logging;
using MusicConverter.MusicXml;
using Xunit;

namespace MusicConverter.Tests;

public class AbcConverterTests
{
    private static async Task<MusicXml.MusicXmlDocument> ConvertText(string abc, InMemoryLogger? logger = null, ListProgress? progress = null)
    {
        using var dir = new TempDir();
        var path = dir.Write("t.abc", abc);
        return await new AbcConverter(logger).ConvertAsync(path, progress, CancellationToken.None);
    }

    [Fact]
    public async Task ConvertsSampleFile()
    {
        var progress = new ListProgress();
        var doc = await new AbcConverter().ConvertAsync(Samples.Path("twinkle.abc"), progress);

        Assert.Equal("Twinkle, Twinkle, Little Star", doc.Title);
        Assert.Equal("Traditional", doc.Composer);
        var part = Assert.Single(doc.Parts);
        Assert.Equal(6, part.Measures.Count);
        var attrs = part.Measures[0].Attributes!;
        Assert.Equal(480, attrs.Divisions);
        Assert.Equal(1, attrs.Fifths);
        Assert.Equal((4, 4), (attrs.Beats, attrs.BeatType));
        Assert.Equal("G4:480", part.Measures[0].Notes[0].Describe());
        Assert.Equal("D5:480", part.Measures[0].Notes[2].Describe());

        var m5 = part.Measures[4].Notes;
        Assert.Equal(2, m5.Count(n => n.IsChord));
        Assert.Equal(new[] { "G4:960", "B4:960", "D5:960" }, m5.Take(3).Select(n => n.Describe()).ToArray());
        Assert.True(m5[3].IsRest);
        var triplets = part.Measures[5].Notes.Where(n => n.TupletActual == 3).ToList();
        Assert.Equal(3, triplets.Count);
        Assert.All(triplets, n => Assert.Equal(320, n.Duration));

        Assert.Contains(progress.Reports, r => r.Stage == ConversionStage.Parsing);
        Assert.Contains(progress.Reports, r => r.Stage == ConversionStage.Converting);
        Assert.True(progress.Reports.Select(r => r.Percent).SequenceEqual(progress.Reports.Select(r => r.Percent).OrderBy(p => p)));
        Assert.True(progress.Reports.Max(r => r.Percent) <= 70);
    }

    [Fact]
    public async Task AppliesKeySignatureAndMeasureAccidentals()
    {
        var doc = await ConvertText("M:4/4\nL:1/4\nK:D\nF C =F ^G|^F F _B B|\n");
        var notes = doc.AllNotes().Select(n => n.Describe()).ToArray();
        Assert.Equal("F+14:480", notes[0]);
        Assert.Equal("C+14:480", notes[1]);
        Assert.Equal("F4:480", notes[2]);
        Assert.Equal("G+14:480", notes[3]);
        Assert.Equal("F+14:480", notes[4]);
        Assert.Equal("F+14:480", notes[5]);
        Assert.Equal("B-14:480", notes[6]);
        Assert.Equal("B-14:480", notes[7]);
    }

    [Fact]
    public async Task HandlesTiesBrokenRhythmAndRests()
    {
        var doc = await ConvertText("M:3/4\nL:1/8\nK:C\nC4-C2 |C>D z2 E2|\n");
        var m1 = doc.Parts[0].Measures[0].Notes;
        Assert.True(m1[0].TieStart);
        Assert.True(m1[1].TieStop);
        var m2 = doc.Parts[0].Measures[1].Notes;
        Assert.Equal(new[] { 360, 120, 480, 480 }, m2.Select(n => n.Duration).ToArray());
        Assert.True(m2[2].IsRest);
    }

    [Fact]
    public async Task VoicesBecomeParts()
    {
        var doc = await ConvertText("X:1\nM:4/4\nL:1/4\nK:C\nV:1 name=\"Soprano\"\nV:2 name=\"Bass\" clef=bass\nV:1\nCDEF|\nV:2\nC,D,E,F,|\n");
        Assert.Equal(2, doc.Parts.Count);
        Assert.Equal("Soprano", doc.Parts[0].Name);
        Assert.Equal("Bass", doc.Parts[1].Name);
        Assert.Equal("C3:480", doc.Parts[1].Measures[0].Notes[0].Describe());
    }

    [Fact]
    public async Task WarnsAboutUnsupportedConstructs()
    {
        var logger = new InMemoryLogger();
        var doc = await ConvertText("L:1/4\nM:4/4\nK:C\n\"Am\"!fermata!{g}C D E F|\nw: la la la la\n", logger);
        Assert.Contains(doc.Warnings, w => w.Contains("Chord symbols"));
    }

    [Fact]
    public async Task ConvertsDecorationsGraceNotesAndTempo()
    {
        var doc = await ConvertText("L:1/4\nM:4/4\nQ:1/4=100\nK:C\n!mf!!fermata!{/g}C .D !trill!E !crescendo(!F|\n");
        var notes = doc.AllNotes().ToList();
        Assert.True(notes[0].IsGrace);
        Assert.True(notes[0].GraceSlash);
        Assert.Contains(notes[1].Directions, d => d.Kind == DirectionKinds.Tempo && d.Value == "100");
        Assert.Contains(notes[1].Directions, d => d.Kind == DirectionKinds.Dynamics && d.Value == "mf");
        Assert.True(notes[1].Fermata);
        Assert.Contains("staccato", notes[2].Articulations);
        Assert.Contains("trill-mark", notes[3].Ornaments);
        Assert.Contains(notes[4].Directions, d => d.Kind == DirectionKinds.Wedge && d.Value == "crescendo");
        Assert.Single(doc.Parts[0].Measures);
    }

    [Fact]
    public async Task UnterminatedChordIsParseError()
    {
        using var dir = new TempDir();
        var path = dir.Write("bad.abc", "M:4/4\nL:1/4\nK:C\n[CE G|\n");
        var logger = new InMemoryLogger();
        var ex = await Assert.ThrowsAsync<ConversionException>(() => new AbcConverter(logger).ConvertAsync(path, null));
        Assert.Equal(ConversionErrorCategory.ParseError, ex.Category);
        Assert.Equal(path, ex.FilePath);
        Assert.Equal("ABC", ex.ConverterName);
        Assert.Equal(4, ex.Line);
    }

    [Fact]
    public async Task FileWithoutMusicIsParseError()
    {
        using var dir = new TempDir();
        var path = dir.Write("empty.abc", "X:1\nT:Nothing\nK:C\n");
        var ex = await Assert.ThrowsAsync<ConversionException>(() => new AbcConverter().ConvertAsync(path, null));
        Assert.Equal(ConversionErrorCategory.ParseError, ex.Category);
    }

    [Theory]
    [InlineData("a.abc", true)]
    [InlineData("A.ABC", true)]
    [InlineData("a.ly", false)]
    [InlineData("", false)]
    public void CanReadChecksExtension(string path, bool expected) =>
        Assert.Equal(expected, new AbcConverter().CanRead(path));
}
