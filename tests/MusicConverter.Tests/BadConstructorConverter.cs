using MusicConverter.Converters;
using MusicConverter.Neutral;

namespace MusicConverter.Tests;

public sealed class BadConstructorConverter : ScriptedConverter
{
    public BadConstructorConverter(int unsupported) : base(null) { }

    protected override NeutralScore Parse(string text, ConversionContext context) => ScoreWith('C');
}