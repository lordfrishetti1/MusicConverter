using MusicConverter.Abstractions;
using MusicConverter.Converters;

namespace MusicConverter.Tests;

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