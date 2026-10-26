using MusicConverter.Abstractions;
using MusicConverter.Logging;
using MusicConverter.Neutral;

namespace MusicConverter.Converters;

/// <summary>Imports Arkkra Mup files (.mup).</summary>
public sealed class MupConverter : MusicFormatConverterBase
{
    private static readonly string[] Extensions = { ".mup" };

    public MupConverter() : this(null) { }

    public MupConverter(ILogger? logger) : base(logger) { }

    public override string FormatName => "MUP";

    public override IReadOnlyCollection<string> FileExtensions => Extensions;

    protected override NeutralScore Parse(string text, ConversionContext context) =>
        new MupParser(text, context).Parse();
}