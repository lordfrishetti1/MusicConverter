using MusicConverter.Abstractions;
using MusicConverter.Logging;
using MusicConverter.MusicXml;

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

/// <summary>Synchronous progress sink (Progress&lt;T&gt; posts to the thread pool and is racy in tests).</summary>
public sealed class ListProgress : IProgress<ConversionProgress>
{
    public List<ConversionProgress> Reports { get; } = new();

    public void Report(ConversionProgress value)
    {
        lock (Reports) Reports.Add(value);
    }
}

public sealed class TempDir : IDisposable
{
    public TempDir() => Path = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "musicconverter-tests-" + Guid.NewGuid().ToString("N"));

    public string Path { get; }

    public string Write(string name, string content)
    {
        Directory.CreateDirectory(Path);
        var full = System.IO.Path.Combine(Path, name);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(Path)) Directory.Delete(Path, true); } catch (IOException) { }
    }
}

public static class Samples
{
    public static string Path(string name) =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "samples", name);
}

public static class MusicXmlAssert
{
    public static IEnumerable<MusicXmlNote> AllNotes(this MusicXmlDocument doc) =>
        doc.Parts.SelectMany(p => p.Measures).SelectMany(m => m.Notes);

    public static string Describe(this MusicXmlNote n) =>
        n.IsRest ? $"r{n.Duration}" : $"{n.Pitch!.Step}{(n.Pitch.Alter == 0 ? "" : n.Pitch.Alter.ToString("+0;-0"))}{n.Pitch.Octave}:{n.Duration}";
}
