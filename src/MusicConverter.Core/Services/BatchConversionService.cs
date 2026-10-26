using System.Diagnostics;
using MusicConverter.Abstractions;
using MusicConverter.Logging;

namespace MusicConverter.Services;

/// <summary>Converts every supported file in a directory. A failing file never stops the batch; cancellation does.</summary>
public sealed class BatchConversionService
{
    private const int KeptWarningsPerFile = 20;

    private readonly ConverterRegistry _registry;
    private readonly ConversionService _service;
    private readonly IFileConverter? _isolated;
    private readonly ILogger _logger;

    public BatchConversionService(
        ConverterRegistry registry, ConversionService service, ILogger? logger = null, IFileConverter? isolatedRunner = null)
    {
        _registry = registry;
        _service = service;
        _isolated = isolatedRunner;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>How long past the timeout a file that ignores cancellation is waited for before being abandoned.</summary>
    public TimeSpan HardTimeoutGrace { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How often memory use is re-checked while the batch is paused for memory pressure.</summary>
    public TimeSpan MemoryPollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Longest a file waits for memory pressure to ease before it is started anyway.</summary>
    public TimeSpan MaxMemoryWait { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Pause after a file hits a safety limit, letting memory settle before the next one.</summary>
    public TimeSpan CoolDownAfterLimit { get; set; } = TimeSpan.FromSeconds(1);

    public Func<int?> MemoryLoadProvider { get; set; } = ResourceMonitor.GetMemoryLoadPercent;

    /// <summary>Every file extension handled by a registered converter, sorted.</summary>
    public IReadOnlyList<string> SupportedExtensions => _registry.Converters
        .SelectMany(c => c.FileExtensions)
        .Select(NormalizeExtension)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static string NormalizeExtension(string e) => e.StartsWith('.') ? e : "." + e;

    public IReadOnlyList<string> FindConvertibleFiles(string directory, bool recursive, IReadOnlyCollection<string>? extensions = null)
    {
        var filter = extensions is { Count: > 0 }
            ? new HashSet<string>(extensions.Select(NormalizeExtension), StringComparer.OrdinalIgnoreCase)
            : null;
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Input directory '{directory}' does not exist.");

        var fileOptions = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
        // Junctions and symlinks are not followed, so a link loop cannot make the scan run forever.
        var directoryOptions = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint
        };

        var found = new List<string>();
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            try
            {
                found.AddRange(Directory.EnumerateFiles(current, "*", fileOptions)
                    .Where(f => filter == null || filter.Contains(Path.GetExtension(f)))
                    .Where(f => _registry.FindConverter(f) != null));
                if (recursive)
                    foreach (var sub in Directory.EnumerateDirectories(current, "*", directoryOptions)) pending.Push(sub);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.Warning($"Folder '{current}' could not be scanned: {ex.Message}");
            }
        }
        return found.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<BatchResult> ConvertDirectoryAsync(
        string inputDirectory,
        BatchOptions? options = null,
        IProgress<BatchProgress>? progress = null,
        IProgress<ConversionProgress>? fileProgress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new BatchOptions();
        var settings = _service.Settings.Clone();
        // Scanning a large folder tree (and sniffing file contents) must not block the caller's UI thread.
        var files = await Task.Run(() => FindConvertibleFiles(inputDirectory, options.Recursive, options.Extensions), cancellationToken)
            .ConfigureAwait(false);
        if (options.OnlyFiles is { Count: > 0 } only)
        {
            var wanted = new HashSet<string>(only.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
            files = files.Where(f => wanted.Contains(Path.GetFullPath(f))).ToList();
        }

        var root = Path.GetFullPath(inputDirectory);
        var manifest = new BatchManifest(string.IsNullOrWhiteSpace(options.OutputDirectory) ? root : Path.GetFullPath(options.OutputDirectory));
        if (options.Resume) manifest.Load();
        else if (options.OnlyFiles is null) manifest.Reset();

        var plan = new List<(string Input, string Output)>(files.Count);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in files)
        {
            var output = ResolveOutputPath(root, input, options);
            // Same base name in different formats (a.abc, a.ly) would collide; keep the source extension then.
            if (!used.Add(output))
            {
                output = ResolveOutputPath(root, input, options, keepSourceExtension: true);
                used.Add(output);
            }
            plan.Add((input, output));
        }

        var isolated = settings.IsolateProcesses && _isolated != null;
        _logger.Info($"Batch started: {plan.Count} file(s) in '{inputDirectory}'. Limits: depth {settings.MaxNestingDepth}, " +
                     $"{settings.MaxInputMegabytes} MB/file, timeout {(settings.PerFileTimeout is { } t ? $"{t.TotalSeconds:0}s" : "off")}, " +
                     $"{settings.MaxConcurrentFiles} at a time, memory pause {(settings.PauseWhenMemoryAbovePercent > 0 ? settings.PauseWhenMemoryAbovePercent + "%" : "off")}, " +
                     $"{(isolated ? "worker processes" : "in-process")}.");
        if (settings.IsolateProcesses && _isolated == null)
            _logger.Warning("Process isolation is enabled but no worker runner is available; converting in-process.");

        var total = plan.Count;
        var results = new BatchItemResult?[total];
        var started = 0;
        var completed = 0;
        using var gate = new SemaphoreSlim(Math.Max(1, settings.MaxConcurrentFiles));
        // Interleaved per-file progress from several files at once would make the bars jump around.
        var perFileProgress = settings.MaxConcurrentFiles == 1 ? fileProgress : null;
        using var priority = settings.LowPriority ? ProcessPriorityScope.Lower() : null;

        async Task RunAsync(int i)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await WaitForMemoryAsync(settings, cancellationToken).ConfigureAwait(false);
                var (input, output) = plan[i];
                var n = Interlocked.Increment(ref started);
                progress?.Report(new BatchProgress(n, total, input, 100.0 * Volatile.Read(ref completed) / total,
                    $"Converting {Path.GetFileName(input)} ({n}/{total})"));

                results[i] = await ConvertOneAsync(input, output, options, settings, isolated, manifest, perFileProgress, cancellationToken)
                    .ConfigureAwait(false);
                Interlocked.Increment(ref completed);
                if (settings.DelayBetweenFilesMs > 0)
                    await Task.Delay(settings.DelayBetweenFilesMs, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        try
        {
            await Task.WhenAll(Enumerable.Range(0, total).Select(RunAsync)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.Info($"Batch cancelled after {Volatile.Read(ref completed)} of {total} file(s).");
            throw;
        }

        progress?.Report(new BatchProgress(total, total, string.Empty, 100, "Batch complete"));
        var batch = new BatchResult(results.Select(r => r!).ToList());
        _logger.Info($"Batch finished: {batch.Succeeded} succeeded, {batch.Failed} failed, {batch.Skipped} skipped, " +
                     $"{batch.WithWarnings.Count} with warnings, {batch.WithMismatches.Count} with output mismatches, {batch.StoppedBySafeguards} stopped by safeguards.");
        if (batch.Failed == 0) manifest.Delete();
        return batch;
    }

    private async Task<BatchItemResult> ConvertOneAsync(
        string input, string output, BatchOptions options, ConversionSettings settings, bool isolated, BatchManifest manifest,
        IProgress<ConversionProgress>? fileProgress, CancellationToken cancellationToken)
    {
        if (File.Exists(output) && !options.Overwrite)
        {
            _logger.Warning($"Skipped '{input}': '{output}' already exists.");
            return new BatchItemResult(input, output, BatchItemStatus.Skipped, "Output already exists");
        }
        if (options.Resume && File.Exists(output) && manifest.IsDone(input))
        {
            _logger.Info($"Skipped '{input}': already converted by an earlier run.");
            return new BatchItemResult(input, output, BatchItemStatus.Skipped, "Already converted in an earlier run");
        }

        using var fileCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeout = settings.PerFileTimeout;
        if (timeout is { } limit) fileCts.CancelAfter(limit);
        IFileConverter runner = isolated ? _isolated! : _service;

        try
        {
            var dir = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var task = runner.ConvertFileAsync(input, output, options.MusicXmlVersion, fileProgress, fileCts.Token);
            if (timeout is { } t)
            {
                var abandon = Task.Delay(t + HardTimeoutGrace, cancellationToken);
                if (await Task.WhenAny(task, abandon).ConfigureAwait(false) != task)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _ = task.ContinueWith(x => _ = x.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    throw new TimeoutException();
                }
            }

            var result = await task.ConfigureAwait(false);
            manifest.MarkDone(input);
            if (result.WarningCount > 0)
                _logger.Warning($"'{input}' converted with {result.WarningCount} warning(s): {string.Join(" | ", result.Warnings ?? Array.Empty<string>())}");
            if (result.HasMismatch)
                _logger.Warning($"'{input}' output differs from its source: {string.Join(" | ", result.VerificationIssues!)}");
            // Only a sample of the warning text is kept so a huge batch does not hold every message in memory.
            result = result with { Warnings = result.Warnings?.Take(KeptWarningsPerFile).ToArray() };
            return new BatchItemResult(input, output, BatchItemStatus.Succeeded, Result: result);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.Error($"Batch item timed out after {timeout}: '{input}'.");
            await CoolDownAsync(cancellationToken).ConfigureAwait(false);
            return new BatchItemResult(input, output, BatchItemStatus.Failed, $"Timed out after {timeout}");
        }
        catch (TimeoutException)
        {
            _logger.Error($"Batch item did not respond to cancellation after {timeout} and was abandoned: '{input}'.");
            await CoolDownAsync(cancellationToken).ConfigureAwait(false);
            return new BatchItemResult(input, output, BatchItemStatus.Failed,
                $"Timed out after {timeout} and did not stop; it was abandoned. Enable worker-process isolation to terminate such files.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error($"Batch item failed: '{input}'.", ex);
            var message = ex is ConversionException ce ? $"{ce.Category}: {ce.Message}" : ex.Message;
            if (ex is ConversionException { Category: ConversionErrorCategory.LimitExceeded or ConversionErrorCategory.WorkerFailure })
                await CoolDownAsync(cancellationToken).ConfigureAwait(false);
            return new BatchItemResult(input, output, BatchItemStatus.Failed, message);
        }
    }

    private async Task CoolDownAsync(CancellationToken cancellationToken)
    {
        GC.Collect();
        if (CoolDownAfterLimit > TimeSpan.Zero) await Task.Delay(CoolDownAfterLimit, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Holds the next file back while system memory use is high, so the machine is not pushed into thrashing.</summary>
    private async Task WaitForMemoryAsync(ConversionSettings settings, CancellationToken cancellationToken)
    {
        var limit = settings.PauseWhenMemoryAbovePercent;
        if (limit <= 0) return;

        var waited = TimeSpan.Zero;
        var announced = false;
        while (MemoryLoadProvider() is { } load && load >= limit)
        {
            if (waited >= MaxMemoryWait)
            {
                _logger.Warning($"Memory use is still {load}% after waiting {waited.TotalSeconds:0}s; continuing anyway.");
                return;
            }
            if (!announced)
            {
                _logger.Warning($"Memory use is {load}% (limit {limit}%); pausing the batch until it drops.");
                announced = true;
            }
            GC.Collect();
            await Task.Delay(MemoryPollInterval, cancellationToken).ConfigureAwait(false);
            waited += MemoryPollInterval;
        }
        if (announced) _logger.Info("Memory use dropped; resuming the batch.");
    }

    private static string ResolveOutputPath(string root, string input, BatchOptions options, bool keepSourceExtension = false)
    {
        var ext = options.OutputExtension.StartsWith('.') ? options.OutputExtension : "." + options.OutputExtension;
        var relative = string.IsNullOrWhiteSpace(options.OutputDirectory) ? input : Path.Combine(Path.GetFullPath(options.OutputDirectory), Path.GetRelativePath(root, input));
        return keepSourceExtension ? relative + ext : Path.ChangeExtension(relative, ext);
    }

    /// <summary>Lowers this process's priority for the duration of a batch and restores it afterwards.</summary>
    private sealed class ProcessPriorityScope : IDisposable
    {
        private readonly Process _process;
        private readonly ProcessPriorityClass _original;

        private ProcessPriorityScope(Process process, ProcessPriorityClass original)
        {
            _process = process;
            _original = original;
        }

        public static ProcessPriorityScope? Lower()
        {
            try
            {
                var process = Process.GetCurrentProcess();
                var original = process.PriorityClass;
                if (original is ProcessPriorityClass.Idle or ProcessPriorityClass.BelowNormal) return null;
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
                return new ProcessPriorityScope(process, original);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return null;
            }
        }

        public void Dispose()
        {
            try { _process.PriorityClass = _original; }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            _process.Dispose();
        }
    }
}
