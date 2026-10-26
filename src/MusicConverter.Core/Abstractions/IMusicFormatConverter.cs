using MusicConverter.MusicXml;

namespace MusicConverter.Abstractions;

/// <summary>
/// An importer for one notation format. Implement this interface in any loaded assembly and the
/// <see cref="ConverterRegistry"/> discovers it automatically.
/// </summary>
public interface IMusicFormatConverter
{
    string FormatName { get; }

    /// <summary>Lower-case extensions including the dot, e.g. ".abc". Used for file dialogs.</summary>
    IReadOnlyCollection<string> FileExtensions { get; }

    bool CanRead(string filePath);

    /// <exception cref="ConversionException">Any failure, categorised.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    Task<MusicXmlDocument> ConvertAsync(
        string filePath,
        IProgress<ConversionProgress> progress,
        CancellationToken cancellationToken = default);
}