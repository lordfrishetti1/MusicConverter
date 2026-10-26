using System.Diagnostics;

namespace MusicConverter.Logging;

/// <summary>Fans a log entry out to several loggers; a failing logger never affects the others.</summary>
public sealed class CompositeLogger : ILogger
{
    private readonly List<ILogger> _loggers;
    private readonly object _gate = new();

    public CompositeLogger(params ILogger[] loggers) : this((IEnumerable<ILogger>)loggers) { }

    public CompositeLogger(IEnumerable<ILogger> loggers) => _loggers = loggers.ToList();

    public void Add(ILogger logger)
    {
        lock (_gate) _loggers.Add(logger);
    }

    public void Log(LogLevel level, string message, Exception? exception = null)
    {
        ILogger[] snapshot;
        lock (_gate) snapshot = _loggers.ToArray();
        foreach (var logger in snapshot)
        {
            try
            {
                logger.Log(level, message, exception);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Logger {logger.GetType().Name} failed: {ex.Message}");
            }
        }
    }
}