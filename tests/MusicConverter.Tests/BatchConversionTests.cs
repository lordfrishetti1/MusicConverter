using MusicConverter.Abstractions;
using MusicConverter.MusicXml;
using MusicConverter.Services;
using Xunit;

namespace MusicConverter.Tests;

public class BatchConversionTests
{
    private static BatchConversionService Create(InMemoryLogger? log = null)
    {
        var registry = new ConverterRegistry(log);
        registry.DiscoverConverters();
        return new BatchConversionService(registry, new ConversionService(registry, new MusicXmlSerializer(log), log), log);
    }

    private static void CopySamples(string dir, params string[] names)
    {
        Directory.CreateDirectory(dir);
        foreach (var n in names) File.Copy(Samples.Path(n), Path.Combine(dir, n));
    }

    [Fact]
    public async Task ConvertsAllSupportedFiles_SkippingUnknown_AndMirrorsOutput()
    {
        using var dir = new TempDir();
        var input = Path.Combine(dir.Path, "in");
        CopySamples(input, "twinkle.abc", "twinkle.mei");
        CopySamples(Path.Combine(input, "sub"), "twinkle.krn");
        File.WriteAllText(Path.Combine(input, "notes.txt"), "ignore me");
        var output = Path.Combine(dir.Path, "out");

        var reports = new ListBatchProgress();
        var result = await Create().ConvertDirectoryAsync(input, new BatchOptions { OutputDirectory = output }, reports);

        Assert.Equal(3, result.Succeeded);
        Assert.Equal(0, result.Failed);
        Assert.True(File.Exists(Path.Combine(output, "twinkle.musicxml")) || File.Exists(Path.Combine(output, "twinkle.mei.musicxml")));
        Assert.True(File.Exists(Path.Combine(output, "sub", "twinkle.musicxml")));
        Assert.Equal(100, reports.Items.Last().OverallPercent, 0.001);
    }

    [Fact]
    public async Task SameBaseName_DifferentFormats_DoNotCollide()
    {
        using var dir = new TempDir();
        CopySamples(dir.Path + "\\in", "twinkle.abc", "twinkle.ly");
        var result = await Create().ConvertDirectoryAsync(dir.Path + "\\in", new BatchOptions());
        Assert.Equal(2, result.Succeeded);
        Assert.Equal(2, result.Items.Select(i => i.OutputPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task NonRecursive_IgnoresSubfolders()
    {
        using var dir = new TempDir();
        var input = dir.Path + "\\in";
        CopySamples(input, "twinkle.abc");
        CopySamples(input + "\\sub", "twinkle.ly");
        var result = await Create().ConvertDirectoryAsync(input, new BatchOptions { Recursive = false });
        Assert.Single(result.Items);
    }

    [Fact]
    public async Task FailingFile_DoesNotStopBatch_AndExistingOutputIsSkipped()
    {
        using var dir = new TempDir();
        var input = dir.Path + "\\in";
        CopySamples(input, "twinkle.abc");
        File.WriteAllText(Path.Combine(input, "bad.krn"), "**kern\n=1\n4h\n*-\n");
        var service = Create();

        var first = await service.ConvertDirectoryAsync(input);
        Assert.Equal(1, first.Succeeded);
        Assert.Equal(1, first.Failed);
        Assert.Contains("ParseError", first.Items.Single(i => i.Status == BatchItemStatus.Failed).Message);

        var second = await service.ConvertDirectoryAsync(input);
        Assert.Equal(1, second.Skipped);
        var third = await service.ConvertDirectoryAsync(input, new BatchOptions { Overwrite = true });
        Assert.Equal(1, third.Succeeded);
    }

    [Fact]
    public async Task Cancellation_StopsBatch()
    {
        using var dir = new TempDir();
        CopySamples(dir.Path + "\\in", "twinkle.abc");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Create().ConvertDirectoryAsync(dir.Path + "\\in", null, null, null, cts.Token));
    }

    [Fact]
    public async Task MissingDirectory_Throws() =>
        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => Create().ConvertDirectoryAsync(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid())));

    private sealed class ListBatchProgress : IProgress<BatchProgress>
    {
        public List<BatchProgress> Items { get; } = new();
        public void Report(BatchProgress value) => Items.Add(value);
    }
}
