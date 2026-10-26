using MusicConverter.Abstractions;
using MusicConverter.Logging;
using MusicConverter.Neutral;

namespace MusicConverter.Converters;

/// <summary>Imports Humdrum **kern files (.krn, .hum).</summary>
public sealed class HumdrumConverter : MusicFormatConverterBase
{
    private static readonly string[] Extensions = { ".krn", ".hum" };

    public HumdrumConverter() : this(null) { }

    public HumdrumConverter(ILogger? logger) : base(logger) { }

    public override string FormatName => "Humdrum";

    public override IReadOnlyCollection<string> FileExtensions => Extensions;

    protected override NeutralScore Parse(string text, ConversionContext context) =>
        new HumdrumParser(text, context).Parse();
}