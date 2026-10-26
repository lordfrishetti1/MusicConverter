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