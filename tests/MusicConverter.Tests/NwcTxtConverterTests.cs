using MusicConverter.Abstractions;
using MusicConverter.Converters;
using MusicConverter.MusicXml;
using Xunit;

namespace MusicConverter.Tests;

public class NwcTxtConverterTests
{
    private const string Header = "!NoteWorthyComposer(2.75)\n";

    private static async Task<MusicXml.MusicXmlDocument> ConvertText(string body, string fileName = "t.nwctxt", InMemoryLogger? logger = null)
    {
        using var dir = new TempDir();
        var path = dir.Write(fileName, Header + body);
        return await new NwcTxtConverter(logger).ConvertAsync(path, null);
    }

    [Fact]
    public async Task ConvertsSampleFile()
    {
        var progress = new ListProgress();
        var doc = await new NwcTxtConverter().ConvertAsync(Samples.Path("twinkle.nwctxt"), progress);

        Assert.Equal("Twinkle, Twinkle, Little Star", doc.Title);
        Assert.Equal("Traditional", doc.Composer);
        var part = Assert.Single(doc.Parts);
        Assert.Equal("Melody", part.Name);
        Assert.Equal(4, part.Measures.Count);
        Assert.Equal(new[] { "C4:480", "C4:480", "G4:480", "G4:480" },
            part.Measures[0].Notes.Select(n => n.Describe()).ToArray());
        Assert.Equal(new[] { "C4:960", "E4:960", "G4:960" },
            part.Measures[2].Notes.Take(3).Select(n => n.Describe()).ToArray());
        Assert.Equal(2, part.Measures[2].Notes.Count(n => n.IsChord));
        Assert.True(part.Measures[2].Notes[^1].TieStart);
        Assert.True(part.Measures[3].Notes[0].TieStop);
        Assert.Equal(1920, part.Measures[3].Notes[0].Duration);
        Assert.Contains(progress.Reports, r => r.Stage == ConversionStage.Parsing);
    }

    [Fact]
    public async Task AppliesKeySignatureAccidentalsAndBassClef()
    {
        var doc = await ConvertText(
            "|AddStaff|Name:\"Low\"\n|Clef|Type:Bass\n|Key|Signature:F#,C#\n|TimeSig|Signature:2/4\n" +
            "|Note|Dur:4th|Pos:0\n|Note|Dur:4th|Pos:-2\n|Note|Dur:4th|Pos:n-2\n|Note|Dur:4th|Pos:-2\n");
        var part = doc.Parts[0];
        Assert.Equal(2, part.Measures.Count);
        Assert.Equal(2, part.Measures[0].Attributes!.Fifths);
        Assert.Equal("F", part.Measures[0].Attributes!.ClefSign);
        Assert.Equal(new[] { "D3:480", "B2:480", "B2:480", "B2:480" }, doc.AllNotes().Select(n => n.Describe()).ToArray());

        var sharp = await ConvertText("|Key|Signature:F#\n|Note|Dur:4th|Pos:-3\n|Note|Dur:4th|Pos:n-3\n|Note|Dur:4th|Pos:-3\n|Note|Dur:4th|Pos:-3\n");
        Assert.Equal(new[] { "F+14:480", "F4:480", "F4:480", "F4:480" }, sharp.AllNotes().Select(n => n.Describe()).ToArray());
    }

    [Fact]
    public async Task TripletsAndMultipleStaves()
    {
        var doc = await ConvertText(
            "|AddStaff|Name:\"A\"\n|Note|Dur:8th,Triplet=First|Pos:-6\n|Note|Dur:8th,Triplet=Mid|Pos:-5\n|Note|Dur:8th,Triplet=End|Pos:-4\n|Note|Dur:Half,Dotted|Pos:-6\n" +
            "|AddStaff|Name:\"B\"\n|Rest|Dur:Whole\n");
        Assert.Equal(2, doc.Parts.Count);
        var first = doc.Parts[0].Measures[0].Notes;
        Assert.All(first.Take(3), n => Assert.Equal((3, 2, 160), (n.TupletActual, n.TupletNormal, n.Duration)));
        Assert.Equal(1, first[3].Dots);
        Assert.True(doc.Parts[1].Measures[0].Notes[0].IsRest);
    }

    [Fact]
    public async Task WarnsOncePerUnsupportedObjectAndKeepsGraceNotesOutOfTiming()
    {
        var doc = await ConvertText("|AddStaff|Name:\"A\"\n|Flow|Style:Fine\n|Flow|Style:Coda\n|Note|Dur:8th,Grace|Pos:0\n|Note|Dur:Whole|Pos:0\n");
        Assert.Single(doc.Warnings, w => w.Contains("Flow"));
        var notes = doc.AllNotes().ToList();
        Assert.Equal(2, notes.Count);
        Assert.True(notes[0].IsGrace);
        Assert.Single(doc.Parts[0].Measures);
    }

    [Fact]
    public async Task DynamicsTempoArticulationsFermataAndSlurs()
    {
        var doc = await ConvertText(
            "|AddStaff|Name:\"A\"\n|Tempo|Tempo:90|Base:Eighth\n|Dynamic|Style:mf\n|DynamicVariance|Style:Crescendo\n" +
            "|Note|Dur:8th,Staccato,Slur|Pos:0\n|Note|Dur:8th,Accent|Pos:1\n|Dynamic|Style:f\n|TempoVariance|Style:Fermata\n|Note|Dur:Half|Pos:2\n");
        var notes = doc.AllNotes().ToList();
        Assert.Contains(notes[0].Directions, d => d.Kind == DirectionKinds.Tempo && d.Value == "90" && d.Unit == "eighth");
        Assert.Contains(notes[0].Directions, d => d.Kind == DirectionKinds.Dynamics && d.Value == "mf");
        Assert.Contains(notes[0].Directions, d => d.Kind == DirectionKinds.Wedge && d.Value == "crescendo");
        Assert.Contains("staccato", notes[0].Articulations);
        Assert.True(notes[0].SlurStart);
        Assert.True(notes[1].SlurStop);
        Assert.Contains("accent", notes[1].Articulations);
        Assert.Contains(notes[2].Directions, d => d.Kind == DirectionKinds.Wedge && d.Value == "stop");
        Assert.Contains(notes[2].Directions, d => d.Kind == DirectionKinds.Dynamics && d.Value == "f");
        Assert.True(notes[2].Fermata);
    }

    [Fact]
    public async Task MissingHeaderIsParseError()
    {
        using var dir = new TempDir();
        var path = dir.Write("bad.nwctxt", "|AddStaff|Name:\"A\"\n");
        var ex = await Assert.ThrowsAsync<ConversionException>(() => new NwcTxtConverter().ConvertAsync(path, null));
        Assert.Equal(ConversionErrorCategory.ParseError, ex.Category);
        Assert.Equal("NWCTXT", ex.ConverterName);
    }

    [Theory]
    [InlineData("|Note|Dur:4th|Pos:abc")]
    [InlineData("|Note|Dur:7th|Pos:0")]
    [InlineData("|Note|Dur:4th")]
    [InlineData("|TimeSig|Signature:x/y")]
    [InlineData("|SongInfo|Title:\"unterminated")]
    public async Task MalformedObjectsAreParseErrors(string line)
    {
        using var dir = new TempDir();
        var path = dir.Write("bad.nwctxt", Header + line + "\n");
        var ex = await Assert.ThrowsAsync<ConversionException>(() => new NwcTxtConverter().ConvertAsync(path, null));
        Assert.Equal(ConversionErrorCategory.ParseError, ex.Category);
        Assert.Equal(2, ex.Line);
    }

    [Fact]
    public void CanReadSniffsTxtFiles()
    {
        using var dir = new TempDir();
        var nwc = dir.Write("a.txt", Header);
        var other = dir.Write("b.txt", "hello");
        var converter = new NwcTxtConverter();
        Assert.True(converter.CanRead(nwc));
        Assert.False(converter.CanRead(other));
        Assert.True(converter.CanRead("x.NWCTXT"));
        Assert.False(converter.CanRead("x.abc"));
    }
}
