using System.Text.Json.Serialization;

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

    /// <summary>Most MIDI notes accepted per file; bounds memory use for huge sequences.</summary>
    public int MaxMidiNotes { get; set; } = 2_000_000;

    /// <summary>Files converted at the same time in a batch. 1 is sequential and the safest.</summary>
    public int MaxConcurrentFiles { get; set; } = 1;

    /// <summary>Runs the batch at below-normal process priority so the rest of the system stays responsive.</summary>
    public bool LowPriority { get; set; }

    /// <summary>Pause after every file, in milliseconds, to give the system breathing room.</summary>
    public int DelayBetweenFilesMs { get; set; }

    /// <summary>Waits before starting a file while system memory use is at or above this percentage. 0 disables.</summary>
    public int PauseWhenMemoryAbovePercent { get; set; } = 90;

    /// <summary>Converts each batch file in a separate worker process, so a crash or hang only fails that file.</summary>
    public bool IsolateProcesses { get; set; }

    /// <summary>Re-reads each written file and compares it with the converted model (and, for MIDI, the source note count).</summary>
    public bool VerifyOutput { get; set; }

    /// <summary>Folder for log files. Empty uses %LOCALAPPDATA%\MusicConverter\logs. Applied at next start.</summary>
    public string LogDirectory { get; set; } = "";

    /// <summary>Writes a log file. Applied at next start.</summary>
    public bool FileLoggingEnabled { get; set; } = true;

    /// <summary>Lowest level written to the log file. Applied at next start.</summary>
    public Logging.LogLevel FileLogLevel { get; set; } = Logging.LogLevel.Debug;

    /// <summary>Lowest level shown in the log panel. Applied at next start.</summary>
    public Logging.LogLevel UiLogLevel { get; set; } = Logging.LogLevel.Information;

    /// <summary>Log files older than this many days are deleted at start. 0 keeps them all.</summary>
    public int LogRetentionDays { get; set; } = 30;

    /// <summary>Shows a toast notification when a batch finishes.</summary>
    public bool NotifyOnBatchComplete { get; set; } = true;

    /// <summary>Plays a sound when a batch finishes.</summary>
    public bool PlaySoundOnBatchComplete { get; set; } = true;

    [JsonIgnore]
    public long MaxInputBytes => MaxInputMegabytes * 1024L * 1024L;

    [JsonIgnore]
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
        MaxMidiNotes = other.MaxMidiNotes;
        MaxConcurrentFiles = other.MaxConcurrentFiles;
        LowPriority = other.LowPriority;
        DelayBetweenFilesMs = other.DelayBetweenFilesMs;
        PauseWhenMemoryAbovePercent = other.PauseWhenMemoryAbovePercent;
        IsolateProcesses = other.IsolateProcesses;
        VerifyOutput = other.VerifyOutput;
        LogDirectory = other.LogDirectory;
        FileLoggingEnabled = other.FileLoggingEnabled;
        FileLogLevel = other.FileLogLevel;
        UiLogLevel = other.UiLogLevel;
        LogRetentionDays = other.LogRetentionDays;
        NotifyOnBatchComplete = other.NotifyOnBatchComplete;
        PlaySoundOnBatchComplete = other.PlaySoundOnBatchComplete;
        Clamp();
    }

    /// <summary>Forces every value into a sane range (e.g. after loading a hand-edited file).</summary>
    public void Clamp()
    {
        MaxMidiNotes = Math.Clamp(MaxMidiNotes, 1_000, 50_000_000);
        MaxConcurrentFiles = Math.Clamp(MaxConcurrentFiles, 1, 64);
        DelayBetweenFilesMs = Math.Clamp(DelayBetweenFilesMs, 0, 60_000);
        PauseWhenMemoryAbovePercent = PauseWhenMemoryAbovePercent <= 0 ? 0 : Math.Clamp(PauseWhenMemoryAbovePercent, 50, 99);
        MaxNestingDepth = Math.Clamp(MaxNestingDepth, MinNestingDepth, MaxNestingDepthLimit);
        MaxInputMegabytes = Math.Clamp(MaxInputMegabytes, 1, 2048);
        MaxRecoveries = Math.Clamp(MaxRecoveries, 0, 100_000);
        MaxWarnings = Math.Clamp(MaxWarnings, 1, 100_000);
        PerFileTimeoutSeconds = Math.Clamp(PerFileTimeoutSeconds, 0, 86_400);
        LogDirectory = (LogDirectory ?? "").Trim();
        LogRetentionDays = Math.Clamp(LogRetentionDays, 0, 3650);
        if (!Enum.IsDefined(FileLogLevel)) FileLogLevel = Logging.LogLevel.Debug;
        if (!Enum.IsDefined(UiLogLevel)) UiLogLevel = Logging.LogLevel.Information;
    }
}