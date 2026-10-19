using System.Text;

namespace MusicConverter.Logging;

public enum LogLevel
{
    Debug = 0,
    Information = 1,
    Warning = 2,
    Error = 3
}

/// <summary>Pluggable logging sink. Implement this to add e.g. cloud logging.</summary>
public interface ILogger
{
    void Log(LogLevel level, string message, Exception? exception = null);
}

public static class LoggerExtensions
{
    public static void Debug(this ILogger logger, string message) => logger.Log(LogLevel.Debug, message);

    public static void Info(this ILogger logger, string message) => logger.Log(LogLevel.Information, message);

    public static void Warning(this ILogger logger, string message) => logger.Log(LogLevel.Warning, message);

    public static void Error(this ILogger logger, string message, Exception? exception = null) =>
        logger.Log(LogLevel.Error, message, exception);
}

public sealed class NullLogger : ILogger
{
    public static NullLogger Instance { get; } = new();

    private NullLogger() { }

    public void Log(LogLevel level, string message, Exception? exception = null) { }
}

public static class LogFormatter
{
    public static string Format(DateTimeOffset timestamp, LogLevel level, string message, Exception? exception)
    {
        var sb = new StringBuilder();
        sb.Append(timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"))
          .Append(" [").Append(level.ToString().ToUpperInvariant()).Append("] ")
          .Append(message);
        if (exception != null)
        {
            sb.AppendLine().Append(exception);
        }
        return sb.ToString();
    }
}
