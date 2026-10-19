using MusicConverter.Abstractions;
using MusicConverter.Logging;
using MusicConverter.MusicXml;
using MusicConverter.Neutral;

namespace MusicConverter.Converters;

/// <summary>
/// Template for converters: reads the file, parses to the neutral model, maps to MusicXML, and
/// handles progress, logging and exception wrapping. Subclasses only implement <see cref="Parse"/>.
/// </summary>
public abstract class MusicFormatConverterBase : IMusicFormatConverter
{
    protected MusicFormatConverterBase(ILogger? logger) => Logger = logger ?? NullLogger.Instance;

    protected ILogger Logger { get; }

    public abstract string FormatName { get; }

    public abstract IReadOnlyCollection<string> FileExtensions { get; }

    public virtual bool CanRead(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        var extension = Path.GetExtension(filePath);
        return FileExtensions.Any(e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Parses the whole file into the neutral model. Throw <see cref="ConversionException"/> for bad input.</summary>
    protected abstract NeutralScore Parse(string text, ConversionContext context);

    public async Task<MusicXmlDocument> ConvertAsync(
        string filePath, IProgress<ConversionProgress>? progress, CancellationToken cancellationToken = default)
    {
        var context = new ConversionContext(filePath, FormatName, Logger, progress, cancellationToken);
        Logger.Info($"Conversion started: converter '{FormatName}', file '{filePath}'.");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.ReportParsing(0, "Reading file");
            var text = await ReadAsync(filePath, context).ConfigureAwait(false);

            var score = await Task.Run(() => RunParse(text, context), cancellationToken).ConfigureAwait(false);
            context.ReportParsing(1, "Parsing complete");

            context.Report(ConversionStage.Converting, 0, "Mapping to MusicXML");
            var document = await Task.Run(() => RunMapping(score, context), cancellationToken).ConfigureAwait(false);
            document.SourceFile = filePath;
            document.ConverterName = FormatName;
            document.Warnings.AddRange(context.Warnings);
            context.Report(ConversionStage.Converting, 1, "Mapping complete");

            Logger.Info($"Conversion finished: converter '{FormatName}', file '{filePath}', " +
                        $"{document.Parts.Count} part(s), {context.Warnings.Count} warning(s).");
            return document;
        }
        catch (OperationCanceledException)
        {
            Logger.Info($"Conversion cancelled: converter '{FormatName}', file '{filePath}'.");
            throw;
        }
        catch (ConversionException ex)
        {
            ex.FilePath ??= filePath;
            ex.ConverterName ??= FormatName;
            Logger.LogConversionFailure(ex);
            throw;
        }
    }

    private static async Task<string> ReadAsync(string filePath, ConversionContext context)
    {
        try
        {
            return await File.ReadAllTextAsync(filePath, context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new ConversionException($"Cannot read '{filePath}': {ex.Message}", ConversionErrorCategory.ParseError, ex)
            {
                FilePath = filePath,
                ConverterName = context.ConverterName
            };
        }
    }

    private NeutralScore RunParse(string text, ConversionContext context)
    {
        try
        {
            var score = Parse(text, context);
            if (score.Parts.Count == 0 || score.Parts.All(p => p.Measures.Count == 0))
                throw context.ParseError($"No musical content found in {FormatName} file.");
            return score;
        }
        catch (Exception ex) when (ex is not (ConversionException or OperationCanceledException))
        {
            throw new ConversionException($"Unexpected {FormatName} parse failure: {ex.Message}",
                ConversionErrorCategory.ParseError, ex);
        }
    }

    private static MusicXmlDocument RunMapping(NeutralScore score, ConversionContext context)
    {
        try
        {
            return new NeutralToMusicXmlMapper().Map(
                score, (fraction, message) => context.Report(ConversionStage.Converting, fraction, message),
                context.CancellationToken);
        }
        catch (Exception ex) when (ex is not (ConversionException or OperationCanceledException))
        {
            throw new ConversionException($"Unexpected mapping failure: {ex.Message}",
                ConversionErrorCategory.MappingError, ex);
        }
    }
}
