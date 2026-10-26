namespace MusicConverter.Neutral;

public sealed class NeutralPart
{
    public string Name { get; set; } = "Part";
    public List<NeutralMeasure> Measures { get; } = new();
}