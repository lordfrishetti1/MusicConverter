using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using MusicConverter.Logging;

namespace MusicConverter.App.Services;

/// <summary>
/// Logger feeding the UI log panel; safe to call from any thread. Entries are queued and appended in
/// batches so a burst of messages (e.g. a folder conversion) cannot flood the UI thread.
/// </summary>
public sealed class UiLogger : ILogger
{
    private const int MaxPerFlush = 500;

    private readonly ConcurrentQueue<string> _pending = new();
    private readonly DispatcherTimer _timer;
    private readonly LogLevel _minimumLevel;
    private readonly int _maxEntries;

    public UiLogger(int maxEntries = 2000, LogLevel minimumLevel = LogLevel.Information)
    {
        _maxEntries = maxEntries;
        _minimumLevel = minimumLevel;
        _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher.CurrentDispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _timer.Tick += (_, _) => Flush();
        _timer.Start();
    }

    public ObservableCollection<string> Entries { get; } = new();

    public void Log(LogLevel level, string message, Exception? exception = null)
    {
        if (level < _minimumLevel) return;
        // The stack trace goes to the file log; the panel shows the message only.
        _pending.Enqueue($"{DateTime.Now:HH:mm:ss} [{level}] {message}");
    }

    private void Flush()
    {
        var batch = new List<string>();
        while (batch.Count < MaxPerFlush && _pending.TryDequeue(out var text)) batch.Add(text);
        if (batch.Count == 0) return;

        var overflow = Entries.Count + batch.Count - _maxEntries;
        for (var i = 0; i < overflow && Entries.Count > 0; i++) Entries.RemoveAt(0);
        foreach (var text in batch) Entries.Add(text);
    }
}
