using System.Xml.Linq;
using MusicConverter.Abstractions;
using MusicConverter.Logging;
using MusicConverter.MusicXml;
using MusicConverter.MusicXml.V3_1;

namespace MusicConverter.Tests;

public class SerializerTests
{
    private static MusicXmlDocument SimpleDoc()
    {
        var doc = new MusicXmlDocument { Title = "T" };
        var part = new MusicXmlPart();
        var m = new MusicXmlMeasure { Number = 1, Attributes = new MusicXmlAttributes { Divisions = 480, Fifths = 0, Beats = 4, BeatType = 4, ClefSign = "G", ClefLine = 2 } };
        m.Notes.Add(new MusicXmlNote { Pitch = new MusicXmlPitch('C', 0, 4), Duration = 1920, Type = "whole" });
        part.Measures.Add(m);
        doc.Parts.Add(part);
        return doc;
    }

    private sealed class FakeV50Writer : IMusicXmlVersionWriter
    {
        public string Version => "5.0";
        public IReadOnlyDictionary<string, string> DeprecatedFeatures { get; } = new Dictionary<string, string>();
        public XDocument Write(MusicXmlDocument document) => new(new XElement("score-partwise", new XAttribute("version", "5.0")));
    }

    [Fact]
    public void Serialize_ProducesValidV31Xml()
    {
        var xml = new MusicXmlSerializer().Serialize(SimpleDoc(), "3.1");
        Assert.Contains("score-partwise", xml);
        Assert.Contains("DOCTYPE", xml);
        var root = XDocument.Parse(xml, LoadOptions.None).Root!;
        Assert.Equal("3.1", root.Attribute("version")?.Value);
        Assert.Equal("C", root.Descendants("step").Single().Value);
    }

    [Fact]
    public void DefaultVersion_Is40_AndOlderVersionsRemain()
    {
        var serializer = new MusicXmlSerializer();
        Assert.Contains("3.1", serializer.SupportedVersions);
        Assert.Contains("4.0", serializer.SupportedVersions);
        Assert.Equal("4.0", serializer.DefaultVersion);
    }

    [Fact]
    public void DefaultSerialize_ProducesV40Xml()
    {
        var xml = new MusicXmlSerializer().Serialize(SimpleDoc());
        Assert.Contains("MusicXML 4.0 Partwise", xml);
        Assert.Equal("4.0", XDocument.Parse(xml).Root!.Attribute("version")?.Value);
    }

    [Fact]
    public void DefaultVersion_StaysAt40_WhenNewerWriterRegistered()
    {
        var serializer = new MusicXmlSerializer();
        serializer.RegisterWriter(new FakeV50Writer());
        Assert.Equal("4.0", serializer.DefaultVersion);
    }

    [Fact]
    public void RegisteredWriter_IsSelectable_WithoutTouchingOthers()
    {
        var serializer = new MusicXmlSerializer();
        serializer.RegisterWriter(new FakeV50Writer());
        Assert.Contains("5.0", serializer.SupportedVersions);
        Assert.Contains("version=\"5.0\"", serializer.Serialize(SimpleDoc(), "5.0"));
        Assert.Contains("version=\"4.0\"", serializer.Serialize(SimpleDoc(), "4.0"));
        Assert.Contains("version=\"3.1\"", serializer.Serialize(SimpleDoc(), "3.1"));
    }

    [Fact]
    public void UnsupportedVersion_ThrowsSerializationError()
    {
        var ex = Assert.Throws<ConversionException>(() => new MusicXmlSerializer().Serialize(SimpleDoc(), "9.9"));
        Assert.Equal(ConversionErrorCategory.SerializationError, ex.Category);
    }

    [Fact]
    public void DeprecatedFeature_LogsWarning_AndRecordsOnDocument()
    {
        var log = new InMemoryLogger();
        var writer = new MusicXmlV31Writer();
        var feature = writer.DeprecatedFeatures.Keys.First();
        var doc = SimpleDoc();
        doc.UseFeature(feature);
        new MusicXmlSerializer(log).Serialize(doc, "3.1");
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(feature));
        Assert.NotEmpty(doc.Warnings);
    }

    [Fact]
    public async Task SerializeToFile_WritesFile_AndBadPathFails()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var path = Path.Combine(dir.Path, "out.musicxml");
        await new MusicXmlSerializer().SerializeToFileAsync(SimpleDoc(), path);
        Assert.True(File.Exists(path));

        var bad = Path.Combine(dir.Path, "missing", "deeper", "x\0.xml");
        var ex = await Assert.ThrowsAsync<ConversionException>(() =>
            new MusicXmlSerializer().SerializeToFileAsync(SimpleDoc(), bad));
        Assert.Equal(ConversionErrorCategory.SerializationError, ex.Category);
    }
}