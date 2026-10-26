namespace MusicConverter.Services;

public sealed record BatchOptions
{
    public bool Recursive { get; init; } = true;

    /// <summary>When null, each output is written next to its input; otherwise the relative folder layout is mirrored here.</summary>
    public string? OutputDirectory { get; init; }

    public bool Overwrite { get; init; }
    public string? MusicXmlVersion { get; init; }
    public string OutputExtension { get; init; } = ".musicxml";

    /// <summary>Input extensions (e.g. ".mid") to process. Null or empty means every supported extension.</summary>
    public IReadOnlyCollection<string>? Extensions { get; init; }

    /// <summary>Skips files an earlier, interrupted run already finished (even when <see cref="Overwrite"/> is set).</summary>
    public bool Resume { get; init; }

    /// <summary>When set, only these input files are processed (used to retry failures).</summary>
    public IReadOnlyCollection<string>? OnlyFiles { get; init; }
}