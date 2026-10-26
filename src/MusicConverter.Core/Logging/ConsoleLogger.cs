namespace MusicConverter.Logging;

public sealed class ConsoleLogger : ILogger
{
    private static readonly object Gate = new();
    private readonly LogLevel _minimumLevel;

    public ConsoleLogger(LogLevel minimumLevel = LogLevel.Information) => _minimumLevel = minimumLevel;

    public void Log(LogLevel level, string message, Exception? exception = null)
    {
        if (level < _minimumLevel) return;
        var text = LogFormatter.Format(DateTimeOffset.Now, level, message, exception);
        lock (Gate)
        {
            (level >= LogLevel.Error ? Console.Error : Console.Out).WriteLine(text);
        }
    }
}