namespace MusicConverter.Tests;

public sealed class TempDir : IDisposable
{
    public TempDir() => Path = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "musicconverter-tests-" + Guid.NewGuid().ToString("N"));

    public string Path { get; }

    public string Write(string name, string content)
    {
        Directory.CreateDirectory(Path);
        var full = System.IO.Path.Combine(Path, name);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(Path)) Directory.Delete(Path, true); } catch (IOException) { }
    }
}