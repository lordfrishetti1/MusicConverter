using MusicConverter.Logging;

namespace MusicConverter.Abstractions;

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