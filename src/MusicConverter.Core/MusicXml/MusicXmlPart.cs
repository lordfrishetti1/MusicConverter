namespace MusicConverter.MusicXml;

public sealed class MusicXmlPart
{
    public string Id { get; set; } = "P1";
    public string Name { get; set; } = "Part";
    public List<MusicXmlMeasure> Measures { get; } = new();
}