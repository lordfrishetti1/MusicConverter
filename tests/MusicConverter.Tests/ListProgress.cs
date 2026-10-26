using MusicConverter.Abstractions;

namespace MusicConverter.Tests;

/// <summary>Synchronous progress sink (Progress&lt;T&gt; posts to the thread pool and is racy in tests).</summary>
public sealed class ListProgress : IProgress<ConversionProgress>
{
    public List<ConversionProgress> Reports { get; } = new();

    public void Report(ConversionProgress value)
    {
        lock (Reports) Reports.Add(value);
    }
}