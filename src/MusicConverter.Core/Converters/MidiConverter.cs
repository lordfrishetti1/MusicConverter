using MusicConverter.Abstractions;
using MusicConverter.Logging;
using MusicConverter.MusicXml;
using MusicConverter.Neutral;

namespace MusicConverter.Converters;

/// <summary>Imports Standard MIDI Files (.mid, .midi), preserving tracks and channels as parts.</summary>
public sealed class MidiConverter : IMusicFormatConverter, IConfigurableConverter
{
    private static readonly string[] Extensions = { ".mid", ".midi" };
    private readonly ILogger _logger;
    private ConversionSettings _settings = new();

    public void Configure(ConversionSettings settings) => _settings = settings.Clone();

    public MidiConverter() : this(null) { }

    public MidiConverter(ILogger? logger) => _logger = logger ?? NullLogger.Instance;

    public string FormatName => "MIDI";
    public IReadOnlyCollection<string> FileExtensions => Extensions;

    public bool CanRead(string filePath) =>
        !string.IsNullOrWhiteSpace(filePath) &&
        Extensions.Contains(Path.GetExtension(filePath), StringComparer.OrdinalIgnoreCase);

    public async Task<MusicXmlDocument> ConvertAsync(
        string filePath, IProgress<ConversionProgress>? progress, CancellationToken cancellationToken = default)
    {
        var context = new ConversionContext(filePath, FormatName, _logger, progress, cancellationToken) { Settings = _settings };
        _logger.Info($"Conversion started: converter '{FormatName}', file '{filePath}'.");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.ReportParsing(0, "Reading MIDI file");
            context.EnsureInputSize(filePath);
            byte[] data;
            try
            {
                data = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                throw new ConversionException($"Cannot read '{filePath}': {ex.Message}", ConversionErrorCategory.ParseError, ex)
                {
                    FilePath = filePath,
                    ConverterName = FormatName
                };
            }

            var score = await Task.Run(() => new MidiParser(data, context).Parse(), cancellationToken).ConfigureAwait(false);
            context.ReportParsing(1, "Parsing complete");
            context.Report(ConversionStage.Converting, 0, "Mapping to MusicXML");

            MusicXmlDocument document;
            try
            {
                document = await Task.Run(
                    () => new NeutralToMusicXmlMapper().Map(
                        score, (fraction, message) => context.Report(ConversionStage.Converting, fraction, message),
                        cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ConversionException($"Unexpected MIDI mapping failure: {ex.Message}",
                    ConversionErrorCategory.MappingError, ex);
            }

            document.SourceFile = filePath;
            document.ConverterName = FormatName;
            document.Warnings.AddRange(context.Warnings);
            context.Report(ConversionStage.Converting, 1, "Mapping complete");
            _logger.Info($"Conversion finished: converter '{FormatName}', file '{filePath}', " +
                         $"{document.Parts.Count} part(s), {context.Warnings.Count} warning(s).");
            return document;
        }
        catch (OperationCanceledException)
        {
            _logger.Info($"Conversion cancelled: converter '{FormatName}', file '{filePath}'.");
            throw;
        }
        catch (ConversionException ex)
        {
            ex.FilePath ??= filePath;
            ex.ConverterName ??= FormatName;
            _logger.LogConversionFailure(ex);
            throw;
        }
        catch (Exception ex)
        {
            var wrapped = new ConversionException($"Unexpected MIDI conversion failure: {ex.Message}",
                ConversionErrorCategory.ParseError, ex)
            {
                FilePath = filePath,
                ConverterName = FormatName
            };
            _logger.LogConversionFailure(wrapped);
            throw wrapped;
        }
    }
}