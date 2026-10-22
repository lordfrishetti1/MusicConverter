using MusicConverter.Logging;

namespace MusicConverter.Abstractions;

public enum ConversionErrorCategory
{
    ParseError,
    MappingError,
    SerializationError,
    UnsupportedFormat,
    LimitExceeded,
    WorkerFailure
}

public class ConversionException : Exception
{
    public ConversionException(string message, ConversionErrorCategory category, Exception? innerException = null)
        : base(message, innerException)
    {
        Category = category;
    }

    public ConversionErrorCategory Category { get; }

    public string? FilePath { get; set; }

    public string? ConverterName { get; set; }

    public int? Line { get; set; }
}

public static class ConversionExceptionLogging
{
    /// <summary>Logs file, converter, category and the full exception (including stack trace).</summary>
    public static void LogConversionFailure(this ILogger logger, ConversionException exception)
    {
        logger.Error(
            $"Conversion failed. File: '{exception.FilePath ?? "<unknown>"}', " +
            $"Converter: '{exception.ConverterName ?? "<unknown>"}', " +
            $"Category: {exception.Category}" +
            (exception.Line is int line ? $", Line: {line}" : string.Empty) +
            $", Message: {exception.Message}",
            exception);
    }
}
