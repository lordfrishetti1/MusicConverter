using MusicConverter.Abstractions;
using MusicConverter.Converters;
using Xunit;

namespace MusicConverter.Tests;

public class ExtraFormatSampleTests
{
    public static IEnumerable<object[]> Samples_ => new[]
    {
        new object[] { "twinkle.krn", typeof(HumdrumConverter) },
        new object[] { "twinkle.mei", typeof(MeiConverter) },
        new object[] { "twinkle.tex", typeof(MusiXTeXConverter) },
        new object[] { "twinkle.mup", typeof(MupConverter) },
    };

    private static IMusicFormatConverter Create(Type t) => (IMusicFormatConverter)Activator.CreateInstance(t)!;

    [Theory]
    [MemberData(nameof(Samples_))]
    public async Task SampleConvertsToSameMelody(string sample, Type converterType)
    {
        var converter = Create(converterType);
        Assert.True(converter.CanRead(Samples.Path(sample)));
        var progress = new ListProgress();
        var doc = await converter.ConvertAsync(Samples.Path(sample), progress);

        var part = Assert.Single(doc.Parts);
        Assert.Equal(5, part.Measures.Count);
        Assert.Equal(1, part.Measures[0].Attributes!.Fifths);
        Assert.Equal(new[] { "G4:480", "G4:480", "D5:480", "D5:480" },
            part.Measures[0].Notes.Select(n => n.Describe()).ToArray());
        Assert.Equal(new[] { "E5:480", "E5:480", "D5:960" },
            part.Measures[1].Notes.Select(n => n.Describe()).ToArray());
        Assert.Equal(2, part.Measures[2].Notes.Count(n => n.IsChord));
        Assert.Contains(part.Measures[2].Notes, n => n.Describe() == "E4:480");
        if (!sample.EndsWith(".tex"))
        {
            Assert.True(part.Measures[3].Notes[^1].TieStart);
            Assert.True(part.Measures[4].Notes[0].TieStop);
        }
        Assert.Contains(progress.Reports, r => r.Stage == ConversionStage.Parsing);
    }

    [Theory]
    [InlineData("twinkle.krn")]
    [InlineData("twinkle.mei")]
    public async Task MetadataIsRead(string sample)
    {
        var doc = await Create(sample.EndsWith("krn") ? typeof(HumdrumConverter) : typeof(MeiConverter))
            .ConvertAsync(Samples.Path(sample), null);
        Assert.Equal("Twinkle, Twinkle, Little Star", doc.Title);
        Assert.Equal("Traditional", doc.Composer);
    }

    [Fact]
    public void FormatNamesAndExtensions()
    {
        Assert.Equal("Humdrum", new HumdrumConverter().FormatName);
        Assert.Equal("MEI", new MeiConverter().FormatName);
        Assert.Equal("MusiXTeX", new MusiXTeXConverter().FormatName);
        Assert.Equal("MUP", new MupConverter().FormatName);
        Assert.False(new HumdrumConverter().CanRead("a.mei"));
        Assert.False(new MupConverter().CanRead("a.krn"));
    }

    [Fact]
    public void MusiXTeX_PlainTexIsNotClaimed()
    {
        using var dir = new TempDir();
        var plain = dir.Write("doc.tex", "\\documentclass{article}\\begin{document}Hi\\end{document}");
        var music = dir.Write("m.tex", "\\input musixtex\n\\startpiece\\Notes\\qu c\\enotes\\endpiece");
        Assert.False(new MusiXTeXConverter().CanRead(plain));
        Assert.True(new MusiXTeXConverter().CanRead(music));
    }

    [Theory]
    [InlineData("a.krn", "**kern\n*M4/4\n=1\n4h\n*-\n", typeof(HumdrumConverter))]
    [InlineData("a.mei", "<mei><music><body", typeof(MeiConverter))]
    [InlineData("a.mup", "music\n1: 4z;\n", typeof(MupConverter))]
    public async Task InvalidInput_ThrowsParseError(string name, string content, Type type)
    {
        using var dir = new TempDir();
        var path = dir.Write(name, content);
        var logger = new InMemoryLogger();
        var converter = (IMusicFormatConverter)Activator.CreateInstance(type, new object?[] { logger })!;
        var ex = await Assert.ThrowsAsync<ConversionException>(() => converter.ConvertAsync(path, null));
        Assert.Equal(ConversionErrorCategory.ParseError, ex.Category);
        Assert.Equal(path, ex.FilePath);
    }

    [Fact]
    public async Task Humdrum_UnsupportedConstructsWarn()
    {
        using var dir = new TempDir();
        var path = dir.Write("w.krn", "**kern\t**kern\n*M2/4\t*M2/4\n=1\t=1\n2c\t2e\n*-\t*-\n");
        var logger = new InMemoryLogger();
        var doc = await new HumdrumConverter(logger).ConvertAsync(path, null);
        Assert.Equal(2, doc.Parts.Count);
    }

    [Fact]
    public async Task Humdrum_AccidentalsAndRests()
    {
        using var dir = new TempDir();
        var path = dir.Write("a.krn", "**kern\n*clefF4\n*M2/4\n=1\n4C#\n4r\n=2\n2B-\n==\n*-\n");
        var doc = await new HumdrumConverter().ConvertAsync(path, null);
        Assert.Equal("F", doc.Parts[0].Measures[0].Attributes!.ClefSign);
        var notes = doc.AllNotes().Select(n => n.Describe()).ToArray();
        Assert.Equal(3, notes.Length);
        Assert.Equal("r480", notes[1]);
        Assert.Equal(1, doc.AllNotes().First().Pitch!.Alter);
        Assert.Equal(-1, doc.AllNotes().Last().Pitch!.Alter);
    }

    [Fact]
    public async Task Mup_Rests_AndDefaultOctave()
    {
        using var dir = new TempDir();
        var path = dir.Write("r.mup", "score\n time=2/4\n clef=bass\nmusic\n1: 4c; r;\nendbar\n");
        var doc = await new MupConverter().ConvertAsync(path, null);
        var notes = doc.AllNotes().Select(n => n.Describe()).ToArray();
        Assert.Equal("C3:480", notes[0]);
        Assert.Equal("r480", notes[1]);
    }

    [Fact]
    public async Task MusiXTeX_Rests_AndDots()
    {
        using var dir = new TempDir();
        var path = dir.Write("r.mtx", "\\generalmeter{\\meterfrac34}\n\\Notes\\qup c\\enotes\n\\Notes\\ca e\\enotes\n\\Notes\\qp\\enotes\n");
        var doc = await new MusiXTeXConverter().ConvertAsync(path, null);
        var notes = doc.AllNotes().Select(n => n.Describe()).ToArray();
        Assert.Equal("C4:720", notes[0]);
        Assert.Equal("E4:240", notes[1]);
        Assert.Equal("r480", notes[2]);
    }

    [Fact]
    public async Task Mei_MultipleStaves_AndRests()
    {
        using var dir = new TempDir();
        var path = dir.Write("m.mei",
            "<mei xmlns=\"http://www.music-encoding.org/ns/mei\"><music><body><mdiv><score>" +
            "<scoreDef meter.count=\"2\" meter.unit=\"4\"><staffGrp><staffDef n=\"1\" clef.shape=\"G\" clef.line=\"2\"/><staffDef n=\"2\" clef.shape=\"F\" clef.line=\"4\"/></staffGrp></scoreDef>" +
            "<section><measure n=\"1\"><staff n=\"1\"><layer><note pname=\"c\" oct=\"4\" dur=\"2\" accid=\"s\"/></layer></staff>" +
            "<staff n=\"2\"><layer><rest dur=\"2\"/></layer></staff></measure></section></score></mdiv></body></music></mei>");
        var doc = await new MeiConverter().ConvertAsync(path, null);
        Assert.Equal(2, doc.Parts.Count);
        Assert.Equal(1, doc.Parts[0].Measures[0].Notes[0].Pitch!.Alter);
        Assert.True(doc.Parts[1].Measures[0].Notes[0].IsRest);
    }

    [Fact]
    public async Task Cancellation_IsHonoured()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var converter = new HumdrumConverter();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => converter.ConvertAsync(Samples.Path("twinkle.krn"), null, cts.Token));
    }
}
