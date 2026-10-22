using System.Reflection;
using System.Text;
using System.Xml;
using MusicConverter.Abstractions;
using MusicConverter.Logging;

namespace MusicConverter.MusicXml;

/// <summary>Serializes <see cref="MusicXmlDocument"/> with a selectable MusicXML version.</summary>
public sealed class MusicXmlSerializer
{
    private readonly Dictionary<string, IMusicXmlVersionWriter> _writers = new(StringComparer.Ordinal);
    private readonly ILogger _logger;

    /// <param name="writers">Explicit writers; when null all writers in this assembly are discovered.</param>
    public MusicXmlSerializer(ILogger? logger = null, IEnumerable<IMusicXmlVersionWriter>? writers = null)
    {
        _logger = logger ?? NullLogger.Instance;
        foreach (var writer in writers ?? DiscoverWriters(typeof(MusicXmlSerializer).Assembly))
            RegisterWriter(writer);
    }

    public IReadOnlyCollection<string> SupportedVersions =>
        _writers.Keys.OrderBy(ParseVersion).ToArray();

    /// <summary>Version used when a caller does not pick one.</summary>
    public const string PreferredVersion = "4.0";

    /// <summary>MusicXML 4.0 when available, otherwise the newest registered version.</summary>
    public string DefaultVersion =>
        _writers.ContainsKey(PreferredVersion)
            ? PreferredVersion
            : SupportedVersions.LastOrDefault() ?? throw new InvalidOperationException("No MusicXML writers registered.");

    public void RegisterWriter(IMusicXmlVersionWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writers[writer.Version] = writer;
    }

    public static IEnumerable<IMusicXmlVersionWriter> DiscoverWriters(Assembly assembly) =>
        assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IMusicXmlVersionWriter).IsAssignableFrom(t))
            .Where(t => t.GetConstructor(Type.EmptyTypes) != null)
            .Select(t => (IMusicXmlVersionWriter)Activator.CreateInstance(t)!);

    public string Serialize(MusicXmlDocument document, string? version = null)
    {
        var bytes = SerializeToBytes(document, version);
        return new UTF8Encoding(false).GetString(bytes);
    }

    public async Task SerializeToFileAsync(
        MusicXmlDocument document, string outputPath, string? version = null, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = await Task.Run(() => SerializeToBytes(document, version), cancellationToken).ConfigureAwait(false);
            var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            // Write beside the target and rename, so an interrupted run never leaves a truncated output file.
            var temp = $"{outputPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temp, outputPath, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            _logger.Info($"Wrote MusicXML to '{outputPath}'.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ConversionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Fail(document, $"Could not write '{outputPath}': {ex.Message}", ex);
        }
    }

    private byte[] SerializeToBytes(MusicXmlDocument document, string? version)
    {
        ArgumentNullException.ThrowIfNull(document);
        var selected = version ?? DefaultVersion;
        if (!_writers.TryGetValue(selected, out var writer))
        {
            throw Fail(document,
                $"MusicXML version '{selected}' is not supported. Supported: {string.Join(", ", SupportedVersions)}.", null);
        }

        _logger.Info($"Serializing to MusicXML {selected}.");
        WarnAboutDeprecatedFields(document, writer);

        try
        {
            var xml = writer.Write(document);
            using var stream = new MemoryStream();
            var settings = new XmlWriterSettings
            {
                Indent = true,
                Encoding = new UTF8Encoding(false),
                CloseOutput = false
            };
            using (var xmlWriter = XmlWriter.Create(stream, settings))
            {
                xml.Save(xmlWriter);
            }
            _logger.Info($"Serialization to MusicXML {selected} finished ({stream.Length} bytes).");
            return stream.ToArray();
        }
        catch (Exception ex) when (ex is not ConversionException)
        {
            throw Fail(document, $"Failed to serialize MusicXML {selected}: {ex.Message}", ex);
        }
    }

    private void WarnAboutDeprecatedFields(MusicXmlDocument document, IMusicXmlVersionWriter writer)
    {
        foreach (var feature in document.UsedFeatures)
        {
            if (!writer.DeprecatedFeatures.TryGetValue(feature, out var explanation)) continue;
            var message = $"Deprecated field '{feature}' used by converter '{document.ConverterName ?? "<unknown>"}' " +
                          $"(MusicXML {writer.Version}): {explanation}";
            _logger.Warning(message);
            document.Warnings.Add(message);
        }
    }

    private ConversionException Fail(MusicXmlDocument document, string message, Exception? inner)
    {
        var exception = new ConversionException(message, ConversionErrorCategory.SerializationError, inner)
        {
            FilePath = document.SourceFile,
            ConverterName = document.ConverterName
        };
        _logger.LogConversionFailure(exception);
        return exception;
    }

    private static Version ParseVersion(string value) =>
        Version.TryParse(value, out var v) ? v : new Version(0, 0);
}
