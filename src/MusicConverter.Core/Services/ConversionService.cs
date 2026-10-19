using MusicConverter.Abstractions;
using MusicConverter.Logging;
using MusicConverter.MusicXml;

namespace MusicConverter.Services;

public sealed record ConversionResult(string InputPath, string OutputPath, string FormatName, string MusicXmlVersion, int WarningCount);

/// <summary>End-to-end pipeline: pick converter → parse → map → serialize, with progress and cancellation.</summary>
public sealed class ConversionService
{
    private readonly ConverterRegistry _registry;
    private readonly MusicXmlSerializer _serializer;
    private readonly ILogger _logger;

    public ConversionService(ConverterRegistry registry, MusicXmlSerializer serializer, ILogger? logger = null)
    {
        _registry = registry;
        _serializer = serializer;
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<ConversionResult> ConvertFileAsync(
        string inputPath,
        string outputPath,
        string? musicXmlVersion = null,
        IProgress<ConversionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(inputPath))
        {
            throw Log(new ConversionException($"Input file '{inputPath}' does not exist.", ConversionErrorCategory.ParseError)
            {
                FilePath = inputPath
            });
        }

        var converter = _registry.FindConverter(inputPath)
            ?? throw Log(new ConversionException(
                $"No converter can read '{Path.GetFileName(inputPath)}'. Supported formats: " +
                string.Join(", ", _registry.Converters.Select(c => c.FormatName)) + ".",
                ConversionErrorCategory.UnsupportedFormat)
            { FilePath = inputPath });

        var version = musicXmlVersion ?? _serializer.DefaultVersion;
        _logger.Info($"Pipeline started: '{inputPath}' -> '{outputPath}' via {converter.FormatName}, MusicXML {version}.");

        var document = await converter.ConvertAsync(inputPath, progress ?? new Progress<ConversionProgress>(), cancellationToken)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(ConversionProgress.ForStage(ConversionStage.Serializing, 0, $"Serializing MusicXML {version}"));
        await _serializer.SerializeToFileAsync(document, outputPath, version, cancellationToken).ConfigureAwait(false);
        progress?.Report(ConversionProgress.ForStage(ConversionStage.Completed, 1, "Conversion complete"));

        _logger.Info($"Pipeline finished: '{outputPath}' ({document.Warnings.Count} warning(s)).");
        return new ConversionResult(inputPath, outputPath, converter.FormatName, version, document.Warnings.Count);
    }

    private ConversionException Log(ConversionException exception)
    {
        _logger.LogConversionFailure(exception);
        return exception;
    }
}
