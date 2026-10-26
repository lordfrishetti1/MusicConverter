using System.Text;

namespace MusicConverter.Logging;

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