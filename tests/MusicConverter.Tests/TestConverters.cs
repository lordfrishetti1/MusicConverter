using MusicConverter.Abstractions;
using MusicConverter.Converters;
using MusicConverter.Logging;
using MusicConverter.Neutral;

namespace MusicConverter.Tests;

public abstract class ScriptedConverter : MusicFormatConverterBase
{
    protected ScriptedConverter(ILogger? logger) : base(logger) { }

    public override string FormatName => "Scripted";

    public override IReadOnlyCollection<string> FileExtensions { get; } = new[] { ".scr" };

    protected static NeutralScore ScoreWith(char step)
    {
        var score = new NeutralScore();
        var part = new NeutralPart();
        var measure = new NeutralMeasure { Number = 1 };
        measure.Notes.Add(new NeutralNote { Ticks = 480, Pitches = { new NeutralPitch(step, 0, 4) } });
        part.Measures.Add(measure);
        score.Parts.Add(part);
        return score;
    }
}

[IgnoreConverter]
public sealed class MappingFailureConverter : ScriptedConverter
{
    public MappingFailureConverter(ILogger? logger = null) : base(logger) { }

    protected override NeutralScore Parse(string text, ConversionContext context) => ScoreWith('H');
}

[IgnoreConverter]
public sealed class ParseCrashConverter : ScriptedConverter
{
    public ParseCrashConverter(ILogger? logger = null) : base(logger) { }

    protected override NeutralScore Parse(string text, ConversionContext context) =>
        throw new InvalidOperationException("boom");
}

public sealed class DiscoverableConverter : ScriptedConverter
{
    public DiscoverableConverter(ILogger logger) : base(logger) { }

    public override string FormatName => "Discoverable";

    public override IReadOnlyCollection<string> FileExtensions { get; } = new[] { ".disc" };

    protected override NeutralScore Parse(string text, ConversionContext context) => ScoreWith('C');
}

public sealed class BadConstructorConverter : ScriptedConverter
{
    public BadConstructorConverter(int unsupported) : base(null) { }

    protected override NeutralScore Parse(string text, ConversionContext context) => ScoreWith('C');
}
