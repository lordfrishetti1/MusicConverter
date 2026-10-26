using MusicConverter.Abstractions;
using MusicConverter.Logging;
using MusicConverter.Neutral;

namespace MusicConverter.Converters;

/// <summary>Imports NoteWorthy Composer text files (.nwctxt).</summary>
public sealed class NwcTxtConverter : MusicFormatConverterBase
{
    private static readonly string[] Extensions = { ".nwctxt" };

    public NwcTxtConverter() : this(null) { }

    public NwcTxtConverter(ILogger? logger) : base(logger) { }

    public override string FormatName => "NWCTXT";

    public override IReadOnlyCollection<string> FileExtensions => Extensions;

    public override bool CanRead(string filePath)
    {
        if (base.CanRead(filePath)) return true;
        if (!string.Equals(Path.GetExtension(filePath), ".txt", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var reader = new StreamReader(filePath);
            return reader.ReadLine()?.StartsWith("!NoteWorthyComposer", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    protected override NeutralScore Parse(string text, ConversionContext context) =>
        new NwcTxtParser(text, context).Parse();
}