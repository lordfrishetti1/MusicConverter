using MusicConverter.Abstractions;
using MusicConverter.Converters;
using MusicConverter.Logging;
using MusicConverter.Neutral;

namespace MusicConverter.Tests;

[IgnoreConverter]
public sealed class MappingFailureConverter : ScriptedConverter
{
    public MappingFailureConverter(ILogger? logger = null) : base(logger) { }

    protected override NeutralScore Parse(string text, ConversionContext context) => ScoreWith('H');
}