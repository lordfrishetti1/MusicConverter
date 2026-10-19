using MusicConverter.Abstractions;
using MusicConverter.Logging;

namespace MusicConverter.Services;

public enum BatchItemStatus
{
    Succeeded,
    Failed,
    Skipped
}

public sealed record BatchItemResult(
    string InputPath, string OutputPath, BatchItemStatus Status, string? Message = null, ConversionResult? Result = null);

public sealed record BatchOptions
{
    public bool Recursive { get; init; } = true;

    /// <summary>When null, each output is written next to its input; otherwise the relative folder layout is mirrored here.</summary>
    public string? OutputDirectory { get; init; }

    public bool Overwrite { get; init; }
    public string? MusicXmlVersion { get; init; }
    public string OutputExtension { get; init; } = ".musicxml";
}

/// <summary>Progress for a batch: which file is running and how far along the whole batch is.</summary>
public sealed record BatchProgress(int Index, int Total, string CurrentFile, double OverallPercent, string Message);

public sealed record BatchResult(IReadOnlyList<BatchItemResult> Items)
{
    public int Succeeded => Items.Count(i => i.Status == BatchItemStatus.Succeeded);
    public int Failed => Items.Count(i => i.Status == BatchItemStatus.Failed);
    public int Skipped => Items.Count(i => i.Status == BatchItemStatus.Skipped);
}

/// <summary>Converts every supported file in a directory. A failing file never stops the batch; cancellation does.</summary>
public sealed class BatchConversionService
{
    private readonly ConverterRegistry _registry;
    private readonly ConversionService _service;
    private readonly ILogger _logger;

    public BatchConversionService(ConverterRegistry registry, ConversionService service, ILogger? logger = null)
    {
        _registry = registry;
        _service = service;
        _logger = logger ?? NullLogger.Instance;
    }

    public IReadOnlyList<string> FindConvertibleFiles(string directory, bool recursive)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Input directory '{directory}' does not exist.");

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System
        };
        return Directory.EnumerateFiles(directory, "*", options)
            .Where(f => _registry.FindConverter(f) != null)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<BatchResult> ConvertDirectoryAsync(
        string inputDirectory,
        BatchOptions? options = null,
        IProgress<BatchProgress>? progress = null,
        IProgress<ConversionProgress>? fileProgress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new BatchOptions();
        var files = FindConvertibleFiles(inputDirectory, options.Recursive);
        var root = Path.GetFullPath(inputDirectory);
        var results = new List<BatchItemResult>();
        _logger.Info($"Batch started: {files.Count} file(s) in '{inputDirectory}'.");

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = files[i];
            var output = ResolveOutputPath(root, input, options);
            // Same base name in different formats (a.abc, a.ly) would collide; keep the source extension then.
            if (!used.Add(output))
            {
                output = ResolveOutputPath(root, input, options, keepSourceExtension: true);
                used.Add(output);
            }
            var name = Path.GetFileName(input);
            progress?.Report(new BatchProgress(i + 1, files.Count, input, 100.0 * i / files.Count, $"Converting {name} ({i + 1}/{files.Count})"));

            if (File.Exists(output) && !options.Overwrite)
            {
                _logger.Warning($"Skipped '{input}': '{output}' already exists.");
                results.Add(new BatchItemResult(input, output, BatchItemStatus.Skipped, "Output already exists"));
                continue;
            }

            try
            {
                var dir = Path.GetDirectoryName(output);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var result = await _service.ConvertFileAsync(input, output, options.MusicXmlVersion, fileProgress, cancellationToken)
                    .ConfigureAwait(false);
                results.Add(new BatchItemResult(input, output, BatchItemStatus.Succeeded, Result: result));
            }
            catch (OperationCanceledException)
            {
                _logger.Info($"Batch cancelled after {results.Count} of {files.Count} file(s).");
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error($"Batch item failed: '{input}'.", ex);
                var message = ex is ConversionException ce ? $"{ce.Category}: {ce.Message}" : ex.Message;
                results.Add(new BatchItemResult(input, output, BatchItemStatus.Failed, message));
            }
        }

        progress?.Report(new BatchProgress(files.Count, files.Count, string.Empty, 100, "Batch complete"));
        var batch = new BatchResult(results);
        _logger.Info($"Batch finished: {batch.Succeeded} succeeded, {batch.Failed} failed, {batch.Skipped} skipped.");
        return batch;
    }

    private static string ResolveOutputPath(string root, string input, BatchOptions options, bool keepSourceExtension = false)
    {
        var ext = options.OutputExtension.StartsWith('.') ? options.OutputExtension : "." + options.OutputExtension;
        var relative = string.IsNullOrWhiteSpace(options.OutputDirectory) ? input : Path.Combine(Path.GetFullPath(options.OutputDirectory), Path.GetRelativePath(root, input));
        return keepSourceExtension ? relative + ext : Path.ChangeExtension(relative, ext);
    }
}
