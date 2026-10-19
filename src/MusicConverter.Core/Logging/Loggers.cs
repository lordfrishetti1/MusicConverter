using System.Diagnostics;
using System.Text;

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

/// <summary>Thread-safe append-only file logger. I/O failures never propagate to callers.</summary>
public sealed class FileLogger : ILogger
{
    private readonly object _gate = new();
    private readonly LogLevel _minimumLevel;

    public FileLogger(string path, LogLevel minimumLevel = LogLevel.Debug)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Log path is required.", nameof(path));
        Path = System.IO.Path.GetFullPath(path);
        _minimumLevel = minimumLevel;
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
    }

    public string Path { get; }

    public void Log(LogLevel level, string message, Exception? exception = null)
    {
        if (level < _minimumLevel) return;
        var line = LogFormatter.Format(DateTimeOffset.Now, level, message, exception) + Environment.NewLine;
        lock (_gate)
        {
            try
            {
                File.AppendAllText(Path, line, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.WriteLine($"FileLogger failed: {ex.Message}");
            }
        }
    }
}

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
