using System.Xml.Linq;

namespace MusicConverter.MusicXml;

/// <summary>
/// Writes a <see cref="MusicXmlDocument"/> as one specific MusicXML version. Implementations are
/// discovered by reflection; to add a version create a new namespace (e.g. <c>V4_0</c>) with one.
/// </summary>
public interface IMusicXmlVersionWriter
{
    /// <summary>Version string such as "3.1".</summary>
    string Version { get; }

    /// <summary>Feature id → explanation for features deprecated in this version.</summary>
    IReadOnlyDictionary<string, string> DeprecatedFeatures { get; }

    XDocument Write(MusicXmlDocument document);
}
