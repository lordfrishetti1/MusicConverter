using System.Diagnostics;
using System.Text;

namespace MusicConverter.Logging;

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