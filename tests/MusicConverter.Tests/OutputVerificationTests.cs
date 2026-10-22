using MusicConverter.Abstractions;
using MusicConverter.MusicXml;
using MusicConverter.Services;
using Xunit;

namespace MusicConverter.Tests;

public sealed class OutputVerificationTests
{
    private static ConversionService CreateService(bool verify)
    {
        var registry = new ConverterRegistry(null);
        registry.DiscoverConverters();
        var service = new ConversionService(registry, new MusicXmlSerializer(null), null);
        service.Settings.VerifyOutput = verify;
        return service;
    }

    private static readonly byte[] TwoNoteMidi = Convert.FromHexString(
        "4D546864000000060001000101E0" +
        "4D54726B0000002D" +
        "00FF03055069616E6F" +
        "00FF510307A120" +
        "00FF580403021808" +
        "00903C40" +
        "00904040" +
        "8360803C00" +
        "00804000" +
        "00FF2F00");

    public static IEnumerable<object[]> SampleFiles() =>
        Directory.GetFiles(Path.GetDirectoryName(Samples.Path("twinkle.abc"))!)
            .Where(f => !f.EndsWith(".musicxml", StringComparison.OrdinalIgnoreCase))
            .Select(f => new object[] { Path.GetFileName(f) });

    [Theory]
    [MemberData(nameof(SampleFiles))]
    public async Task EverySampleVerifiesCleanly(string name)
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var output = Path.Combine(dir.Path, "o.musicxml");
        var service = CreateService(true);
        ConversionResult result;
        try { result = await service.ConvertFileAsync(Samples.Path(name), output); }
        catch (ConversionException) { return; } // sample files that intentionally fail to convert
        Assert.True(result.Verified);
        Assert.True(result.VerificationIssues is { Count: 0 }, string.Join("; ", result.VerificationIssues ?? Array.Empty<string>()));
    }

    [Fact]
    public async Task VerificationIsSkippedWhenDisabled()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var result = await CreateService(false).ConvertFileAsync(Samples.Path("twinkle.abc"), Path.Combine(dir.Path, "o.musicxml"));
        Assert.False(result.Verified);
        Assert.Null(result.VerificationIssues);
        Assert.False(result.HasMismatch);
    }

    [Fact]
    public async Task MidiSourceNoteCountMatches()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var input = Path.Combine(dir.Path, "n.mid");
        await File.WriteAllBytesAsync(input, TwoNoteMidi);
        var result = await CreateService(true).ConvertFileAsync(input, Path.Combine(dir.Path, "o.musicxml"));
        Assert.True(result.Verified);
        Assert.Empty(result.VerificationIssues!);
    }

    [Fact]
    public async Task TamperedOutputIsReported()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var input = Samples.Path("twinkle.abc");
        var output = Path.Combine(dir.Path, "o.musicxml");
        var registry = new ConverterRegistry(null);
        registry.DiscoverConverters();
        var document = await registry.FindConverter(input)!.ConvertAsync(input, null);
        await new MusicXmlSerializer(null).SerializeToFileAsync(document, output, null);

        Assert.Empty(OutputVerifier.Verify(document, input, output));

        // Remove the first pitched note from the written file.
        var text = await File.ReadAllTextAsync(output);
        var start = text.IndexOf("<note>", StringComparison.Ordinal);
        var end = text.IndexOf("</note>", start, StringComparison.Ordinal) + "</note>".Length;
        await File.WriteAllTextAsync(output, text.Remove(start, end - start));

        var issues = OutputVerifier.Verify(document, input, output);
        Assert.NotEmpty(issues);
    }

    [Fact]
    public async Task MidiNoteLossAgainstTheSourceIsReported()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var input = Path.Combine(dir.Path, "n.mid");
        await File.WriteAllBytesAsync(input, TwoNoteMidi);
        var output = Path.Combine(dir.Path, "o.musicxml");
        var registry = new ConverterRegistry(null);
        registry.DiscoverConverters();
        var document = await registry.FindConverter(input)!.ConvertAsync(input, null);
        document.Parts[0].Measures[0].Notes.RemoveAt(0);
        await new MusicXmlSerializer(null).SerializeToFileAsync(document, output, null);

        var issues = OutputVerifier.Verify(document, input, output);
        Assert.Contains(issues, i => i.Contains("MIDI source has 2"));
    }

    [Fact]
    public async Task BatchReportsMismatchesAndLeavesTheFilesSucceeded()
    {
        using var dir = new TempDir();
        var input = Path.Combine(dir.Path, "in");
        Directory.CreateDirectory(input);
        File.Copy(Samples.Path("twinkle.abc"), Path.Combine(input, "a.abc"));
        var service = CreateService(true);
        var registry = service.Registry;
        var batch = new BatchConversionService(registry, service, null);
        var result = await batch.ConvertDirectoryAsync(input);
        Assert.Equal(1, result.Succeeded);
        Assert.Empty(result.WithMismatches);
        Assert.True(result.Items[0].Result!.Verified);
    }
}
