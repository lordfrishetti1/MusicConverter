using System.Globalization;

namespace MusicConverter.Services;

/// <summary>
/// Append-only record of files a batch has finished, so an interrupted run can continue where it stopped.
/// Each line is <c>length|lastWriteTicks|path</c>; a changed file is converted again.
/// </summary>
internal sealed class BatchManifest
{
    public const string FileName = ".musicconverter-batch.log";

    private readonly string _path;
    private readonly HashSet<string> _done = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public BatchManifest(string directory)
    {
        _path = Path.Combine(directory, FileName);
    }

    public void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            foreach (var line in File.ReadLines(_path)) _done.Add(line);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Reset() => Delete();

    public bool IsDone(string input) => _done.Contains(Entry(input));

    public void MarkDone(string input)
    {
        var entry = Entry(input);
        lock (_gate)
        {
            if (!_done.Add(entry)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.AppendAllText(_path, entry + Environment.NewLine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public void Delete()
    {
        try { File.Delete(_path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Entry(string input)
    {
        var info = new FileInfo(input);
        return string.Create(CultureInfo.InvariantCulture, $"{info.Length}|{info.LastWriteTimeUtc.Ticks}|{input}");
    }
}
