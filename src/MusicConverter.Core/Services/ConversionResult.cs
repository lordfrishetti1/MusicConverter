namespace MusicConverter.Services;

public sealed record ConversionResult(
    string InputPath, string OutputPath, string FormatName, string MusicXmlVersion, int WarningCount,
    IReadOnlyList<string>? Warnings = null, bool Verified = false, IReadOnlyList<string>? VerificationIssues = null)
{
    /// <summary>True when output verification ran and found differences.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasMismatch => VerificationIssues is { Count: > 0 };
}