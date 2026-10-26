using System.Diagnostics;

namespace MusicConverter.Logging;

/// <summary>Writes to the attached debugger / debug output window.</summary>
public sealed class DebugLogger : ILogger
{
    private readonly LogLevel _minimumLevel;

    public DebugLogger(LogLevel minimumLevel = LogLevel.Debug) => _minimumLevel = minimumLevel;

    public void Log(LogLevel level, string message, Exception? exception = null)
    {
        if (level < _minimumLevel) return;
        Trace.WriteLine(LogFormatter.Format(DateTimeOffset.Now, level, message, exception));
    }
}