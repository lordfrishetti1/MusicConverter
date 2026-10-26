using System.Xml.Linq;
using MusicConverter.Abstractions;
using MusicConverter.MusicXml;
using MusicConverter.Services;

namespace MusicConverter.Tests;

public class ConversionServiceTests
{
    private static ConversionService Create(InMemoryLogger? log = null)
    {
        var registry = new ConverterRegistry(log);
        registry.DiscoverConverters();
        return new ConversionService(registry, new MusicXmlSerializer(log), log);
    }

    [Theory]
    [InlineData("twinkle.abc")]
    [InlineData("twinkle.ly")]
    [InlineData("twinkle.nwctxt")]
    [InlineData("twinkle.krn")]
    [InlineData("twinkle.mei")]
    [InlineData("twinkle.tex")]
    [InlineData("twinkle.mup")]
    public async Task Samples_ConvertEndToEnd(string sample)
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var output = Path.Combine(dir.Path, "out.musicxml");
        var progress = new ListProgress();
        var result = await Create().ConvertFileAsync(Samples.Path(sample), output, null, progress);
        Assert.True(File.Exists(output));
        Assert.NotNull(XDocument.Load(output).Root);
        Assert.Equal(100, progress.Reports.Last().Percent, 0.001);
        Assert.Contains(progress.Reports, r => r.Stage == ConversionStage.Parsing);
        Assert.Contains(progress.Reports, r => r.Stage == ConversionStage.Converting);
        Assert.Contains(progress.Reports, r => r.Stage == ConversionStage.Serializing);
        Assert.Equal(output, result.OutputPath);
    }

    [Fact]
    public async Task UnsupportedFormat_Throws()
    {
        using var dir = new TempDir();
        var file = dir.Write("a.xyz", "data");
        var ex = await Assert.ThrowsAsync<ConversionException>(() => Create().ConvertFileAsync(file, file + ".xml"));
        Assert.Equal(ConversionErrorCategory.UnsupportedFormat, ex.Category);
    }

    [Fact]
    public async Task MissingInput_IsParseError()
    {
        var ex = await Assert.ThrowsAsync<ConversionException>(() =>
            Create().ConvertFileAsync(Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid() + ".abc"), "o.xml"));
        Assert.Equal(ConversionErrorCategory.ParseError, ex.Category);
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Create().ConvertFileAsync(Samples.Path("twinkle.abc"), Path.Combine(Path.GetTempPath(), "c.xml"), null, null, cts.Token));
    }
}