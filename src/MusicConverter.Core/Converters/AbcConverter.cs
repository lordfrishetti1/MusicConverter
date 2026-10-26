using MusicConverter.Abstractions;
using MusicConverter.Logging;
using MusicConverter.Neutral;

namespace MusicConverter.Converters;

/// <summary>Imports ABC notation (first tune of a file; voices become parts).</summary>
public sealed class AbcConverter : MusicFormatConverterBase
{
    private static readonly string[] Extensions = { ".abc" };

    public AbcConverter() : this(null) { }

    public AbcConverter(ILogger? logger) : base(logger) { }

    public override string FormatName => "ABC";

    public override IReadOnlyCollection<string> FileExtensions => Extensions;

    protected override NeutralScore Parse(string text, ConversionContext context) =>
        new AbcParser(text, context).Parse();
}