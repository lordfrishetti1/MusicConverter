using MusicConverter.Abstractions;
using MusicConverter.Converters;
using MusicConverter.MusicXml;
using Xunit;

namespace MusicConverter.Tests;

public class LenientRecoveryTests
{
    private static async Task<MusicXmlDocument> Convert(MusicFormatConverterBase converter, string name, string text)
    {
        using var dir = new TempDir();
        return await converter.ConvertAsync(dir.Write(name, text), null);
    }

    private static int NoteCount(MusicXmlDocument doc) => doc.Parts.Sum(p => p.Measures.Sum(m => m.Notes.Count));

    [Fact]
    public async Task AbcSkipsBadChordAndKeepsRest()
    {
        var doc = await Convert(new AbcConverter(), "a.abc", "M:4/4\nL:1/4\nK:C\n[CE G D E F|G A B c|\n");
        Assert.NotEmpty(doc.Warnings);
        Assert.True(NoteCount(doc) >= 4);
    }

    [Fact]
    public async Task AbcBadMeterFallsBackToDefault()
    {
        var doc = await Convert(new AbcConverter(), "a.abc", "M:x/y\nL:1/4\nK:C\nCDEF|GABc|\n");
        Assert.NotEmpty(doc.Warnings);
        Assert.Equal(8, NoteCount(doc));
    }

    [Fact]
    public async Task LilyPondUnclosedBraceIsClosedAtEof()
    {
        var doc = await Convert(new LilyPondConverter(), "a.ly", "\\relative c' {\n c4 d e f\n");
        Assert.Contains(doc.Warnings, w => w.Contains("never closed"));
        Assert.Equal(4, NoteCount(doc));
    }

    [Fact]
    public async Task LilyPondBadDurationSkipsOnlyThatNote()
    {
        var doc = await Convert(new LilyPondConverter(), "a.ly", "{ c'4 d'3 e'4 f'4 }");
        Assert.NotEmpty(doc.Warnings);
        Assert.Equal(3, NoteCount(doc));
    }

    [Fact]
    public async Task NwcTxtSkipsMalformedLines()
    {
        const string text = "!NoteWorthyComposer(2.0)\n|AddStaff|Name:\"A\"\n|Clef|Type:Treble\n|Note|Dur:4th|Pos:abc\n|Note|Dur:4th|Pos:0\n|Note|Dur:4th|Pos:1\n";
        var doc = await Convert(new NwcTxtConverter(), "a.nwctxt", text);
        Assert.NotEmpty(doc.Warnings);
        Assert.Equal(2, NoteCount(doc));
    }

    [Fact]
    public async Task HumdrumPadsShortDataLines()
    {
        const string text = "**kern\t**kern\n*clefG2\t*clefF4\n=1\t=1\n4c\t4C\n4d\n4e\t4E\n";
        var doc = await Convert(new HumdrumConverter(), "a.krn", text);
        Assert.NotEmpty(doc.Warnings);
        Assert.Equal(2, doc.Parts.Count);
    }

    [Fact]
    public async Task MeiTruncatedFileIsRepaired()
    {
        const string text = "<?xml version=\"1.0\"?><mei xmlns=\"http://www.music-encoding.org/ns/mei\"><music><body><mdiv><score>" +
                            "<scoreDef><staffGrp><staffDef n=\"1\" lines=\"5\" meter.count=\"4\" meter.unit=\"4\"/></staffGrp></scoreDef>" +
                            "<section><measure n=\"1\"><staff n=\"1\"><layer><note pname=\"c\" oct=\"4\" dur=\"4\"/><note pname=\"d\" oct=\"4\" dur=\"4\"/>";
        var doc = await Convert(new MeiConverter(), "a.mei", text);
        Assert.Contains(doc.Warnings, w => w.Contains("repaired"));
        Assert.Equal(2, NoteCount(doc));
    }

    [Fact]
    public async Task StrictModeStillFails()
    {
        using var dir = new TempDir();
        var path = dir.Write("a.ly", "{ c'4 d'3 }");
        await Assert.ThrowsAsync<ConversionException>(() => new LilyPondConverter { Lenient = false }.ConvertAsync(path, null));
    }

    [Fact]
    public async Task DeeplyNestedInputFailsCleanlyInsteadOfOverflowingTheStack()
    {
        using var dir = new TempDir();
        var path = dir.Write("deep.ly", new string('{', 200000) + " c4 " + new string('}', 200000));
        var ex = await Assert.ThrowsAsync<ConversionException>(() => new LilyPondConverter().ConvertAsync(path, null));
        Assert.Equal(ConversionErrorCategory.LimitExceeded, ex.Category);
    }

    [Theory]
    [InlineData("twinkle.abc")]
    public async Task TruncatedSamplesNeverThrowUnexpectedly(string sample)
    {
        var text = await File.ReadAllTextAsync(Samples.Path(sample));
        for (var cut = text.Length / 4; cut < text.Length; cut += Math.Max(1, text.Length / 12))
        {
            using var dir = new TempDir();
            var path = dir.Write(sample, text[..cut]);
            try { await new AbcConverter().ConvertAsync(path, null); }
            catch (ConversionException) { }
        }
    }
}
