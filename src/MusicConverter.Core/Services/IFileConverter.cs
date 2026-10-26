using MusicConverter.Abstractions;

namespace MusicConverter.Services;

/// <summary>Converts one file to MusicXML. Implemented in-process by <see cref="ConversionService"/> and out-of-process by the worker runner.</summary>
public interface IFileConverter
{
    Task<ConversionResult> ConvertFileAsync(
        string inputPath, string outputPath, string? musicXmlVersion = null,
        IProgress<ConversionProgress>? progress = null, CancellationToken cancellationToken = default);
}