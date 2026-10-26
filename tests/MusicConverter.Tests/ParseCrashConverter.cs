using MusicConverter.Abstractions;
using MusicConverter.Converters;
using MusicConverter.Logging;
using MusicConverter.Neutral;

namespace MusicConverter.Tests;

[IgnoreConverter]
public sealed class ParseCrashConverter : ScriptedConverter
{
    public ParseCrashConverter(ILogger? logger = null) : base(logger) { }

    protected override NeutralScore Parse(string text, ConversionContext context) =>
        throw new InvalidOperationException("boom");
}