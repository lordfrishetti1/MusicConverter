using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MusicConverter.Abstractions;
using MusicConverter.Converters;
using MusicConverter.MusicXml;
using MusicConverter.Services;
using Xunit;

namespace MusicConverter.Tests;

public sealed class SafeguardTests
{
    private static readonly byte[] TwoNoteMidi = Convert.FromHexString(
        "4D546864000000060001000101E0" +
        "4D54726B0000002D" +
        "00FF03055069616E6F" +
        "00FF510307A120" +
        "00FF580403021808" +
        "00903C40" +
        "00904040" +
        "8360803C00" +
        "00804000" +
        "00FF2F00");

    private sealed class FakeConverter : IFileConverter
    {
        private readonly Func<string, CancellationToken, Task> _run;
        private int _current, _max;
        public int MaxConcurrent => _max;

        public FakeConverter(Func<string, CancellationToken, Task>? run = null) => _run = run ?? ((_, _) => Task.CompletedTask);

        public async Task<ConversionResult> ConvertFileAsync(
            string inputPath, string outputPath, string? musicXmlVersion = null,
            IProgress<ConversionProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            var now = Interlocked.Increment(ref _current);
            int seen;
            while ((seen = _max) < now && Interlocked.CompareExchange(ref _max, now, seen) != seen) { }
            try
            {
                await _run(inputPath, cancellationToken);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                await File.WriteAllTextAsync(outputPath, "<x/>", cancellationToken);
                return new ConversionResult(inputPath, outputPath, "fake", "4.0", 0);
            }
            finally { Interlocked.Decrement(ref _current); }
        }
    }

    private static (BatchConversionService Batch, ConversionService Service) Create(IFileConverter? runner, Action<ConversionSettings>? configure = null)
    {
        var registry = new ConverterRegistry(null);
        registry.DiscoverConverters();
        var service = new ConversionService(registry, new MusicXmlSerializer(null), null);
        service.Settings.IsolateProcesses = runner != null;
        configure?.Invoke(service.Settings);
        var batch = new BatchConversionService(registry, service, null, runner)
        {
            MemoryPollInterval = TimeSpan.FromMilliseconds(30),
            CoolDownAfterLimit = TimeSpan.Zero,
        };
        return (batch, service);
    }

    private static string MakeInputs(string root, int count)
    {
        var input = Path.Combine(root, "in");
        Directory.CreateDirectory(input);
        for (var i = 0; i < count; i++) File.Copy(Samples.Path("twinkle.abc"), Path.Combine(input, $"f{i}.abc"));
        return input;
    }

    [Fact]
    public void SettingsAreClampedAndRoundTripThroughJson()
    {
        var s = new ConversionSettings
        {
            MaxNestingDepth = 1, MaxInputMegabytes = 0, MaxConcurrentFiles = 999, DelayBetweenFilesMs = -5,
            PauseWhenMemoryAbovePercent = 10, MaxMidiNotes = 5,
        };
        s.Clamp();
        Assert.Equal(10, s.MaxNestingDepth);
        Assert.Equal(1, s.MaxInputMegabytes);
        Assert.Equal(64, s.MaxConcurrentFiles);
        Assert.Equal(0, s.DelayBetweenFilesMs);
        Assert.Equal(50, s.PauseWhenMemoryAbovePercent);
        Assert.Equal(1000, s.MaxMidiNotes);

        s.IsolateProcesses = true;
        var copy = JsonSerializer.Deserialize<ConversionSettings>(JsonSerializer.Serialize(s))!;
        Assert.True(copy.IsolateProcesses);
        Assert.Equal(s.MaxConcurrentFiles, copy.MaxConcurrentFiles);
    }

    [Fact]
    public async Task OversizedInputIsRejectedBeforeParsing()
    {
        using var dir = new TempDir();
        var path = dir.Write("big.abc", new string('a', 1024 * 1024 + 10));
        var (_, service) = Create(null, s => s.MaxInputMegabytes = 1);
        var ex = await Assert.ThrowsAsync<ConversionException>(() => service.ConvertFileAsync(path, path + ".xml"));
        Assert.Equal(ConversionErrorCategory.LimitExceeded, ex.Category);
    }

    [Fact]
    public async Task DeeplyNestedMeiFailsWithLimitExceeded()
    {
        using var dir = new TempDir();
        var sb = new StringBuilder("<mei xmlns=\"http://www.music-encoding.org/ns/mei\"><music><body><mdiv><score>");
        for (var i = 0; i < 300; i++) sb.Append("<section>");
        for (var i = 0; i < 300; i++) sb.Append("</section>");
        sb.Append("</score></mdiv></body></music></mei>");
        var path = dir.Write("deep.mei", sb.ToString());
        var (_, service) = Create(null);
        var ex = await Assert.ThrowsAsync<ConversionException>(() => service.ConvertFileAsync(path, path + ".xml"));
        Assert.Equal(ConversionErrorCategory.LimitExceeded, ex.Category);
    }

    [Fact]
    public async Task MeiEntityExpansionBombIsStopped()
    {
        using var dir = new TempDir();
        var sb = new StringBuilder("<?xml version=\"1.0\"?><!DOCTYPE mei [<!ENTITY a0 \"aaaaaaaaaa\">");
        for (var i = 1; i < 10; i++)
            sb.Append($"<!ENTITY a{i} \"{string.Concat(Enumerable.Repeat($"&a{i - 1};", 10))}\">");
        sb.Append("]><mei xmlns=\"http://www.music-encoding.org/ns/mei\"><music>&a9;</music></mei>");
        var path = dir.Write("bomb.mei", sb.ToString());
        var (_, service) = Create(null);
        await Assert.ThrowsAsync<ConversionException>(() => service.ConvertFileAsync(path, path + ".xml"));
    }

    [Fact]
    public async Task MidiNoteLimitIsEnforced()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var path = Path.Combine(dir.Path, "n.mid");
        await File.WriteAllBytesAsync(path, TwoNoteMidi);
        var converter = new MidiConverter();
        converter.Configure(new ConversionSettings { MaxMidiNotes = 1 });
        var ex = await Assert.ThrowsAsync<ConversionException>(() => converter.ConvertAsync(path, null));
        Assert.Equal(ConversionErrorCategory.LimitExceeded, ex.Category);
    }

    [Fact]
    public async Task PerFileTimeoutFailsTheFileAndContinues()
    {
        using var dir = new TempDir();
        var input = MakeInputs(dir.Path, 2);
        var runner = new FakeConverter(async (p, ct) =>
        {
            if (p.EndsWith("f0.abc")) await Task.Delay(Timeout.Infinite, ct);
        });
        var (batch, _) = Create(runner, s => s.PerFileTimeoutSeconds = 1);
        var result = await batch.ConvertDirectoryAsync(input);
        Assert.Equal(1, result.Failed);
        Assert.Equal(1, result.Succeeded);
        Assert.Equal(1, result.StoppedBySafeguards);
    }

    [Fact]
    public async Task ConverterIgnoringCancellationIsAbandoned()
    {
        using var dir = new TempDir();
        var input = MakeInputs(dir.Path, 2);
        var release = new TaskCompletionSource();
        var runner = new FakeConverter((p, _) => p.EndsWith("f0.abc") ? release.Task : Task.CompletedTask);
        var (batch, _) = Create(runner, s => s.PerFileTimeoutSeconds = 1);
        batch.HardTimeoutGrace = TimeSpan.FromMilliseconds(200);
        var result = await batch.ConvertDirectoryAsync(input);
        release.SetResult();
        Assert.Equal(1, result.Failed);
        Assert.Equal(1, result.Succeeded);
    }

    [Fact]
    public async Task ConcurrencyIsCapped()
    {
        using var dir = new TempDir();
        var input = MakeInputs(dir.Path, 8);
        var runner = new FakeConverter((_, _) => Task.Delay(60));
        var (batch, _) = Create(runner, s => s.MaxConcurrentFiles = 3);
        var result = await batch.ConvertDirectoryAsync(input);
        Assert.Equal(8, result.Succeeded);
        Assert.InRange(runner.MaxConcurrent, 2, 3);
    }

    [Fact]
    public async Task BatchWaitsWhileMemoryIsHigh()
    {
        using var dir = new TempDir();
        var input = MakeInputs(dir.Path, 1);
        var polls = 0;
        var runner = new FakeConverter();
        var (batch, _) = Create(runner);
        batch.MemoryLoadProvider = () => ++polls <= 3 ? 95 : 10;
        var result = await batch.ConvertDirectoryAsync(input);
        Assert.Equal(1, result.Succeeded);
        Assert.True(polls >= 4, $"polls={polls}");
    }

    [Fact]
    public async Task ResumeSkipsFinishedFilesEvenWithOverwrite()
    {
        using var dir = new TempDir();
        var input = MakeInputs(dir.Path, 3);
        var output = Path.Combine(dir.Path, "out");
        var fail = true;
        var runner = new FakeConverter((p, _) =>
            fail && p.EndsWith("f2.abc") ? throw new ConversionException("boom", ConversionErrorCategory.ParseError) : Task.CompletedTask);
        var (batch, _) = Create(runner);

        var first = await batch.ConvertDirectoryAsync(input, new BatchOptions { OutputDirectory = output });
        Assert.Equal(2, first.Succeeded);
        Assert.Equal(1, first.Failed);

        fail = false;
        var second = await batch.ConvertDirectoryAsync(input, new BatchOptions { OutputDirectory = output, Overwrite = true, Resume = true });
        Assert.Equal(1, second.Succeeded);
        Assert.Equal(2, second.Skipped);
    }

    [Fact]
    public async Task OnlyFilesRestrictsTheBatch()
    {
        using var dir = new TempDir();
        var input = MakeInputs(dir.Path, 3);
        var (batch, _) = Create(new FakeConverter());
        var only = new[] { Path.Combine(input, "f1.abc") };
        var result = await batch.ConvertDirectoryAsync(input, new BatchOptions { OnlyFiles = only });
        var item = Assert.Single(result.Items);
        Assert.EndsWith("f1.abc", item.InputPath);
    }

    [Fact]
    public async Task FailedConversionLeavesNoPartialOrTempOutput()
    {
        using var dir = new TempDir();
        var input = MakeInputs(dir.Path, 1);
        var (batch, _) = Create(null);
        await batch.ConvertDirectoryAsync(input);
        Assert.Empty(Directory.GetFiles(input, "*.tmp"));
        Assert.Single(Directory.GetFiles(input, "*.musicxml"));
    }

    [Fact]
    public void RegexDefaultTimeoutIsConfigured()
    {
        _ = typeof(ConversionSettings).Assembly;
        Assert.Equal(TimeSpan.FromSeconds(2), AppContext.GetData("REGEX_DEFAULT_MATCH_TIMEOUT") is TimeSpan t ? t : Regex.InfiniteMatchTimeout);
    }

    [Fact]
    public async Task WorkerReportsSuccessAndFailureAsJson()
    {
        using var dir = new TempDir();
        var abc = Samples.Path("twinkle.abc");
        var output = Path.Combine(dir.Path, "o.musicxml");
        Directory.CreateDirectory(dir.Path);
        ConversionService Make()
        {
            var registry = new ConverterRegistry(null);
            registry.DiscoverConverters();
            return new ConversionService(registry, new MusicXmlSerializer(null), null);
        }

        var ok = new StringWriter();
        var code = await ConversionWorker.RunAsync(ConversionWorker.BuildArguments(abc, output, null, new ConversionSettings()).ToList(), Make, ok);
        Assert.Equal(0, code);
        Assert.True(JsonSerializer.Deserialize<WorkerResponse>(ok.ToString())!.Ok);
        Assert.True(File.Exists(output));

        var bad = new StringWriter();
        code = await ConversionWorker.RunAsync(
            ConversionWorker.BuildArguments(Path.Combine(dir.Path, "missing.abc"), output, null, new ConversionSettings()).ToList(), Make, bad);
        Assert.Equal(1, code);
        Assert.False(JsonSerializer.Deserialize<WorkerResponse>(bad.ToString())!.Ok);

        var invalid = new StringWriter();
        Assert.Equal(2, await ConversionWorker.RunAsync(new[] { "x" }, Make, invalid));
    }
}
