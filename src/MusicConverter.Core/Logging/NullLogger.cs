namespace MusicConverter.Logging;

public sealed class NullLogger : ILogger
{
    public static NullLogger Instance { get; } = new();

    private NullLogger() { }

    public void Log(LogLevel level, string message, Exception? exception = null) { }
}