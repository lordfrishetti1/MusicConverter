using MusicConverter.Abstractions;
using MusicConverter.Converters;
using MusicConverter.Logging;

namespace MusicConverter.Tests;

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