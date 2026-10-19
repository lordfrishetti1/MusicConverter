using System.Xml.Linq;
using MusicConverter.Abstractions;
using MusicConverter.Converters;
using MusicConverter.Logging;
using MusicConverter.MusicXml;
using MusicConverter.MusicXml.V3_1;
using MusicConverter.Services;

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

public class ErrorHandlingTests
{
    [Fact]
    public async Task MappingFailure_IsWrapped_AndLoggedWithDiagnostics()
    {
        using var dir = new TempDir();
        var file = dir.Write("a.scr", "x");
        var log = new InMemoryLogger();
        var ex = await Assert.ThrowsAsync<ConversionException>(() =>
            new MappingFailureConverter(log).ConvertAsync(file, null!));
        Assert.Equal(ConversionErrorCategory.MappingError, ex.Category);
        Assert.Equal(file, ex.FilePath);
        Assert.Equal("Scripted", ex.ConverterName);
        Assert.True(log.Contains(LogLevel.Error, file));
        Assert.True(log.Contains(LogLevel.Error, "Scripted"));
        Assert.True(log.Contains(LogLevel.Error, "MappingError"));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && !string.IsNullOrEmpty(e.Exception?.StackTrace));
    }

    [Fact]
    public async Task UnexpectedParseException_IsWrappedAsParseError()
    {
        using var dir = new TempDir();
        var file = dir.Write("a.scr", "x");
        var ex = await Assert.ThrowsAsync<ConversionException>(() =>
            new ParseCrashConverter().ConvertAsync(file, null!));
        Assert.Equal(ConversionErrorCategory.ParseError, ex.Category);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public async Task PreCancelledToken_ThrowsOperationCanceled()
    {
        using var dir = new TempDir();
        var file = dir.Write("a.abc", "X:1\nK:C\nCDEF|\n");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new AbcConverter().ConvertAsync(file, null!, cts.Token));
    }

    [Fact]
    public async Task MissingFile_IsParseError()
    {
        var ex = await Assert.ThrowsAsync<ConversionException>(() =>
            new AbcConverter().ConvertAsync(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid() + ".abc"), null!));
        Assert.Equal(ConversionErrorCategory.ParseError, ex.Category);
    }
}

public class LoggingTests
{
    [Fact]
    public void FileLogger_WritesEntriesAndExceptions_AndHonoursMinimumLevel()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var path = Path.Combine(dir.Path, "log", "app.log");
        var logger = new FileLogger(path, LogLevel.Information);
        logger.Debug("hidden-debug");
        logger.Info("visible-info");
        logger.Error("failed", new InvalidOperationException("kaboom"));
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("hidden-debug", text);
        Assert.Contains("visible-info", text);
        Assert.Contains("kaboom", text);
    }

    [Fact]
    public void FileLogger_IsThreadSafe()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var path = Path.Combine(dir.Path, "c.log");
        var logger = new FileLogger(path);
        Parallel.For(0, 200, i => logger.Info($"entry-{i}"));
        Assert.Equal(200, File.ReadAllLines(path).Count(l => l.Contains("entry-")));
    }

    private sealed class ThrowingLogger : ILogger
    {
        public void Log(LogLevel level, string message, Exception? exception = null) => throw new IOException("down");
    }

    [Fact]
    public void CompositeLogger_FansOut_AndIsolatesFaults()
    {
        var a = new InMemoryLogger();
        var b = new InMemoryLogger();
        var composite = new CompositeLogger(a, new ThrowingLogger(), b);
        composite.Warning("hello");
        Assert.True(a.Contains(LogLevel.Warning, "hello"));
        Assert.True(b.Contains(LogLevel.Warning, "hello"));
    }

    [Fact]
    public void DebugLogger_DoesNotThrow()
    {
        new DebugLogger().Info("x");
        new DebugLogger().Error("y", new Exception("z"));
    }
}

public class RegistryTests
{
    [Fact]
    public void BuiltInConverters_AreDiscovered()
    {
        var registry = new ConverterRegistry();
        Assert.True(registry.DiscoverConverters() >= 3);
        var names = registry.Converters.Select(c => c.FormatName).ToList();
        Assert.Contains(names, n => n.Contains("ABC", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(names, n => n.Contains("LilyPond", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(names, n => n.Contains("NWC", StringComparison.OrdinalIgnoreCase) || n.Contains("NoteWorthy", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("x.abc", "ABC")]
    [InlineData("x.ly", "LilyPond")]
    [InlineData("x.nwctxt", "NWC")]
    [InlineData("x.krn", "Humdrum")]
    [InlineData("x.mei", "MEI")]
    [InlineData("x.mtx", "MusiXTeX")]
    [InlineData("x.mup", "MUP")]
    public void FindConverter_ByExtension(string file, string expected)
    {
        var registry = new ConverterRegistry();
        registry.DiscoverConverters();
        var found = registry.FindConverter(file);
        Assert.NotNull(found);
        Assert.Contains(expected, found!.FormatName, StringComparison.OrdinalIgnoreCase);
        Assert.Null(registry.FindConverter("x.unknown"));
    }

    [Fact]
    public void DiscoveringTestAssembly_HonoursIgnoreAndConstructors()
    {
        var registry = new ConverterRegistry();
        registry.DiscoverConverters(typeof(RegistryTests).Assembly);
        var names = registry.Converters.Select(c => c.FormatName).ToList();
        Assert.Contains("Discoverable", names);
        Assert.DoesNotContain("Scripted", names);
    }

    [Fact]
    public void DuplicateRegistration_IsIgnored()
    {
        var registry = new ConverterRegistry();
        registry.Register(new AbcConverter());
        registry.Register(new AbcConverter());
        Assert.Single(registry.Converters);
    }

    [Fact]
    public void FileDialogFilter_ListsExtensions()
    {
        var registry = new ConverterRegistry();
        registry.DiscoverConverters();
        Assert.Contains("*.abc", registry.FileDialogFilter);
        Assert.Contains("*.ly", registry.FileDialogFilter);
        Assert.Contains("*.krn", registry.FileDialogFilter);
        Assert.Contains("*.mei", registry.FileDialogFilter);
        Assert.Contains("*.tex", registry.FileDialogFilter);
        Assert.Contains("*.mup", registry.FileDialogFilter);
    }

    [Fact]
    public void LoadPluginsFrom_MissingDirectory_ReturnsZero() =>
        Assert.Equal(0, new ConverterRegistry().LoadPluginsFrom(Path.Combine(Path.GetTempPath(), "no-" + Guid.NewGuid())));
}

public class ConversionServiceTests
{
    private static ConversionService Create(InMemoryLogger? log = null)
    {
        var registry = new ConverterRegistry(log);
        registry.DiscoverConverters();
        return new ConversionService(registry, new MusicXmlSerializer(log), log);
    }

    [Theory]
    [InlineData("twinkle.abc")]
    [InlineData("twinkle.ly")]
    [InlineData("twinkle.nwctxt")]
    [InlineData("twinkle.krn")]
    [InlineData("twinkle.mei")]
    [InlineData("twinkle.tex")]
    [InlineData("twinkle.mup")]
    public async Task Samples_ConvertEndToEnd(string sample)
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var output = Path.Combine(dir.Path, "out.musicxml");
        var progress = new ListProgress();
        var result = await Create().ConvertFileAsync(Samples.Path(sample), output, null, progress);
        Assert.True(File.Exists(output));
        Assert.NotNull(XDocument.Load(output).Root);
        Assert.Equal(100, progress.Reports.Last().Percent, 0.001);
        Assert.Contains(progress.Reports, r => r.Stage == ConversionStage.Parsing);
        Assert.Contains(progress.Reports, r => r.Stage == ConversionStage.Converting);
        Assert.Contains(progress.Reports, r => r.Stage == ConversionStage.Serializing);
        Assert.Equal(output, result.OutputPath);
    }

    [Fact]
    public async Task UnsupportedFormat_Throws()
    {
        using var dir = new TempDir();
        var file = dir.Write("a.xyz", "data");
        var ex = await Assert.ThrowsAsync<ConversionException>(() => Create().ConvertFileAsync(file, file + ".xml"));
        Assert.Equal(ConversionErrorCategory.UnsupportedFormat, ex.Category);
    }

    [Fact]
    public async Task MissingInput_IsParseError()
    {
        var ex = await Assert.ThrowsAsync<ConversionException>(() =>
            Create().ConvertFileAsync(Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid() + ".abc"), "o.xml"));
        Assert.Equal(ConversionErrorCategory.ParseError, ex.Category);
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Create().ConvertFileAsync(Samples.Path("twinkle.abc"), Path.Combine(Path.GetTempPath(), "c.xml"), null, null, cts.Token));
    }
}
