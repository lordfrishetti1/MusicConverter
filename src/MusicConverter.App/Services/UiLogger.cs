using System.Collections.ObjectModel;
using MusicConverter.Logging;

namespace MusicConverter.App.Services;

/// <summary>Logger feeding the UI log panel; safe to call from any thread.</summary>
public sealed class UiLogger : ILogger
{
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly int _maxEntries;

    public UiLogger(int maxEntries = 2000) => _maxEntries = maxEntries;

    public ObservableCollection<string> Entries { get; } = new();

    public void Log(LogLevel level, string message, Exception? exception = null)
    {
        // The stack trace goes to the file log; the panel shows the message only.
        var text = $"{DateTime.Now:HH:mm:ss} [{level}] {message}";
        if (_context != null) _context.Post(_ => Append(text), null);
        else Append(text);
    }

    private void Append(string text)
    {
        Entries.Add(text);
        while (Entries.Count > _maxEntries) Entries.RemoveAt(0);
    }
}
