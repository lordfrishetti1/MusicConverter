namespace MusicConverter.Abstractions;

/// <summary>User-tunable safeguards and behaviour for conversions. Defaults are safe for large batches.</summary>
public sealed class ConversionSettings
{
    public const int MinNestingDepth = 10, MaxNestingDepthLimit = 1000;

    /// <summary>Skip malformed constructs with a warning instead of failing the file.</summary>
    public bool Lenient { get; set; } = true;

    /// <summary>Deepest nesting recursive parsers accept (LilyPond, MEI). Guards against stack overflow.</summary>
    public int MaxNestingDepth { get; set; } = 100;

    /// <summary>Largest input file accepted, in megabytes.</summary>
    public int MaxInputMegabytes { get; set; } = 64;

    /// <summary>Most malformed constructs skipped per file in lenient mode before the file fails.</summary>
    public int MaxRecoveries { get; set; } = 500;

    /// <summary>Most distinct warnings kept per file.</summary>
    public int MaxWarnings { get; set; } = 1000;

    /// <summary>Seconds a single file may take in a batch before it is failed. 0 disables the limit.</summary>
    public int PerFileTimeoutSeconds { get; set; } = 300;

    public long MaxInputBytes => MaxInputMegabytes * 1024L * 1024L;

    public TimeSpan? PerFileTimeout => PerFileTimeoutSeconds > 0 ? TimeSpan.FromSeconds(PerFileTimeoutSeconds) : null;

    public ConversionSettings Clone() => (ConversionSettings)MemberwiseClone();

    public void CopyFrom(ConversionSettings other)
    {
        Lenient = other.Lenient;
        MaxNestingDepth = other.MaxNestingDepth;
        MaxInputMegabytes = other.MaxInputMegabytes;
        MaxRecoveries = other.MaxRecoveries;
        MaxWarnings = other.MaxWarnings;
        PerFileTimeoutSeconds = other.PerFileTimeoutSeconds;
        Clamp();
    }

    /// <summary>Forces every value into a sane range (e.g. after loading a hand-edited file).</summary>
    public void Clamp()
    {
        MaxNestingDepth = Math.Clamp(MaxNestingDepth, MinNestingDepth, MaxNestingDepthLimit);
        MaxInputMegabytes = Math.Clamp(MaxInputMegabytes, 1, 2048);
        MaxRecoveries = Math.Clamp(MaxRecoveries, 0, 100_000);
        MaxWarnings = Math.Clamp(MaxWarnings, 1, 100_000);
        PerFileTimeoutSeconds = Math.Clamp(PerFileTimeoutSeconds, 0, 86_400);
    }
}

/// <summary>Implemented by converters that honour <see cref="ConversionSettings"/>; applied by the conversion service.</summary>
public interface IConfigurableConverter
{
    void Configure(ConversionSettings settings);
}
