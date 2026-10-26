namespace MusicConverter.Services;

public sealed record BatchItemResult(
    string InputPath, string OutputPath, BatchItemStatus Status, string? Message = null, ConversionResult? Result = null);