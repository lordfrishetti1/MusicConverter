using MusicConverter.Abstractions;
using MusicConverter.Logging;
using MusicConverter.Neutral;

namespace MusicConverter.Converters;

/// <summary>Imports Music Encoding Initiative files (.mei).</summary>
public sealed class MeiConverter : MusicFormatConverterBase
{
    private static readonly string[] Extensions = { ".mei" };

    public MeiConverter() : this(null) { }

    public MeiConverter(ILogger? logger) : base(logger) { }

    public override string FormatName => "MEI";

    public override IReadOnlyCollection<string> FileExtensions => Extensions;

    protected override NeutralScore Parse(string text, ConversionContext context) =>
        new MeiParser(text, context).Parse();
}