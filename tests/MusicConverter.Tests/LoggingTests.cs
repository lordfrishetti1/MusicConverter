using MusicConverter.Logging;

namespace MusicConverter.Tests;

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