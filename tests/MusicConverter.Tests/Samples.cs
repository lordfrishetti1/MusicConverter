namespace MusicConverter.Tests;

public static class Samples
{
    public static string Path(string name) =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "samples", name);
}