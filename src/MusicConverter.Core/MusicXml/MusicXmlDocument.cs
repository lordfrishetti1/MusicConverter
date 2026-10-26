namespace MusicConverter.MusicXml;

/// <summary>
/// Version-neutral MusicXML document model produced by converters. Each supported MusicXML version
/// has its own namespace (e.g. <c>MusicConverter.MusicXml.V3_1</c>) containing an
/// <see cref="IMusicXmlVersionWriter"/>; adding a version never touches converters.
/// </summary>
public sealed class MusicXmlDocument
{
    private readonly HashSet<string> _usedFeatures = new(StringComparer.Ordinal);

    public string? Title { get; set; }
    public string? Composer { get; set; }
    public string Software { get; set; } = "MusicConverter";
    public string? SourceFile { get; set; }
    public string? ConverterName { get; set; }
    public List<MusicXmlPart> Parts { get; } = new();

    /// <summary>Non-fatal issues found during conversion (unsupported constructs, etc.).</summary>
    public List<string> Warnings { get; } = new();

    public IReadOnlyCollection<string> UsedFeatures => _usedFeatures;

    /// <summary>
    /// Records use of a versioned feature (e.g. "legacy.encoder-element"). Version writers declare which
    /// features they consider deprecated, and the serializer warns about them.
    /// </summary>
    public void UseFeature(string featureId) => _usedFeatures.Add(featureId);
}