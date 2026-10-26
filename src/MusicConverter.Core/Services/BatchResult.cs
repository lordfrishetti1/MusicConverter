using MusicConverter.Abstractions;

namespace MusicConverter.Services;

public sealed record BatchResult(IReadOnlyList<BatchItemResult> Items)
{
    public int Succeeded => Items.Count(i => i.Status == BatchItemStatus.Succeeded);
    public int Failed => Items.Count(i => i.Status == BatchItemStatus.Failed);
    public int Skipped => Items.Count(i => i.Status == BatchItemStatus.Skipped);

    /// <summary>Converted files whose conversion dropped or approximated content.</summary>
    public IReadOnlyList<BatchItemResult> WithWarnings =>
        Items.Where(i => i.Result is { WarningCount: > 0 }).ToList();

    /// <summary>Converted files whose written output differs from the converted score or the MIDI source.</summary>
    public IReadOnlyList<BatchItemResult> WithMismatches =>
        Items.Where(i => i.Result is { HasMismatch: true }).ToList();

    /// <summary>Failures caused by a safety limit, timeout or worker crash rather than by bad content.</summary>
    public int StoppedBySafeguards => Items.Count(i => i.Status == BatchItemStatus.Failed && i.Message is { } m &&
                                                       (m.StartsWith(nameof(ConversionErrorCategory.LimitExceeded)) || m.StartsWith(nameof(ConversionErrorCategory.WorkerFailure)) ||
                                                        m.StartsWith("Timed out")));
}