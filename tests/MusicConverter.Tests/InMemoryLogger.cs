using MusicConverter.Logging;

namespace MusicConverter.Tests;

public sealed class InMemoryLogger : ILogger
{
    private readonly List<(LogLevel Level, string Message, Exception? Exception)> _entries = new();

    public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries
    {
        get { lock (_entries) return _entries.ToArray(); }
    }

    public void Log(LogLevel level, string message, Exception? exception = null)
    {
        lock (_entries) _entries.Add((level, message, exception));
    }

    public bool Contains(LogLevel level, string text) =>
        Entries.Any(e => e.Level == level && e.Message.Contains(text, StringComparison.OrdinalIgnoreCase));
}