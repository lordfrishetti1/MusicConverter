namespace MusicConverter.Logging;

/// <summary>Pluggable logging sink. Implement this to add e.g. cloud logging.</summary>
public interface ILogger
{
    void Log(LogLevel level, string message, Exception? exception = null);
}