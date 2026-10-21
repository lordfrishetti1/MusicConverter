using MusicConverter.Abstractions;
using MusicConverter.Logging;

namespace MusicConverter.Converters;

/// <summary>Per-conversion state handed to parsers: logging, warnings, progress and cancellation.</summary>
public sealed class ConversionContext
{
    private readonly IProgress<ConversionProgress>? _progress;
    private double _lastPercent = -1;
    private ConversionStage? _lastStage;

    public ConversionContext(
        string filePath, string converterName, ILogger logger,
        IProgress<ConversionProgress>? progress, CancellationToken cancellationToken)
    {
        FilePath = filePath;
        ConverterName = converterName;
        Logger = logger;
        _progress = progress;
        CancellationToken = cancellationToken;
    }

    public string FilePath { get; }
    public string ConverterName { get; }
    public ILogger Logger { get; }
    public CancellationToken CancellationToken { get; }
    public List<string> Warnings { get; } = new();

    /// <summary>When true, parsers skip malformed constructs (with a warning) instead of failing the file.</summary>
    public bool Lenient { get; init; } = true;

    private const int MaxRecoveries = 500;
    private int _recoveries;

    public void ThrowIfCancelled() => CancellationToken.ThrowIfCancellationRequested();

    /// <summary>
    /// Handles a recoverable parse error: in lenient mode logs it as a warning and returns true so the caller can
    /// resynchronise; otherwise (or once too many errors piled up) returns false and the caller should rethrow.
    /// </summary>
    public bool Recover(Exception error)
    {
        if (!Lenient || error is OperationCanceledException) return false;
        if (error is ConversionException { Category: not ConversionErrorCategory.ParseError }) return false;
        if (++_recoveries > MaxRecoveries) return false;
        Warn($"Skipped malformed input: {error.Message}");
        return true;
    }

    /// <summary>Records and logs a warning about an unsupported construct (once per distinct message).</summary>
    public void Warn(string message)
    {
        if (Warnings.Contains(message)) return;
        Warnings.Add(message);
        Logger.Warning($"[{ConverterName}] {Path.GetFileName(FilePath)}: {message}");
    }

    public void Report(ConversionStage stage, double fraction, string message)
    {
        var update = ConversionProgress.ForStage(stage, fraction, message);
        var milestone = fraction <= 0 || fraction >= 1 || _lastStage != stage;
        if (!milestone && update.Percent - _lastPercent < 1) return;

        _lastPercent = update.Percent;
        _lastStage = stage;
        if (milestone) Logger.Debug($"[{ConverterName}] {stage} {update.Percent:0}%: {message}");
        _progress?.Report(update);
    }

    public void ReportParsing(double fraction, string message = "Parsing") =>
        Report(ConversionStage.Parsing, fraction, message);

    public ConversionException ParseError(string message, int? line = null) => new(
        line is int l ? $"{message} (line {l})" : message, ConversionErrorCategory.ParseError)
    {
        FilePath = FilePath,
        ConverterName = ConverterName,
        Line = line
    };
}
