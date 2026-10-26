using MusicConverter.Converters;
using MusicConverter.Logging;
using MusicConverter.Neutral;

namespace MusicConverter.Tests;

public sealed class DiscoverableConverter : ScriptedConverter
{
    public DiscoverableConverter(ILogger logger) : base(logger) { }

    public override string FormatName => "Discoverable";

    public override IReadOnlyCollection<string> FileExtensions { get; } = new[] { ".disc" };

    protected override NeutralScore Parse(string text, ConversionContext context) => ScoreWith('C');
}