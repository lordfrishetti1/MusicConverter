namespace MusicConverter.Abstractions;

public enum ConversionStage
{
    Parsing,
    Converting,
    Serializing,
    Completed
}

/// <summary>
/// Progress snapshot. <see cref="Percent"/> is overall (0-100); <see cref="StagePercent"/> is within <see cref="Stage"/>.
/// Parsing owns 0-45, converting (mapping to MusicXML) 45-70, serializing 70-100.
/// </summary>
public sealed record ConversionProgress(ConversionStage Stage, double Percent, string Message, double StagePercent = 0)
{
    public static ConversionProgress ForStage(ConversionStage stage, double fraction, string message)
    {
        fraction = Math.Clamp(double.IsNaN(fraction) ? 0 : fraction, 0, 1);
        var (start, end) = stage switch
        {
            ConversionStage.Parsing => (0.0, 45.0),
            ConversionStage.Converting => (45.0, 70.0),
            ConversionStage.Serializing => (70.0, 100.0),
            _ => (100.0, 100.0)
        };
        return new ConversionProgress(stage, start + (end - start) * fraction, message, fraction * 100);
    }
}
