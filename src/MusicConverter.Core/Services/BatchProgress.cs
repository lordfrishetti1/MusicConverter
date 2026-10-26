namespace MusicConverter.Services;

/// <summary>Progress for a batch: which file is running and how far along the whole batch is.</summary>
public sealed record BatchProgress(int Index, int Total, string CurrentFile, double OverallPercent, string Message);