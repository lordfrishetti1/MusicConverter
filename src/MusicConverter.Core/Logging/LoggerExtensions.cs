namespace MusicConverter.Logging;

public static class LoggerExtensions
{
    public static void Debug(this ILogger logger, string message) => logger.Log(LogLevel.Debug, message);

    public static void Info(this ILogger logger, string message) => logger.Log(LogLevel.Information, message);

    public static void Warning(this ILogger logger, string message) => logger.Log(LogLevel.Warning, message);

    public static void Error(this ILogger logger, string message, Exception? exception = null) =>
        logger.Log(LogLevel.Error, message, exception);
}