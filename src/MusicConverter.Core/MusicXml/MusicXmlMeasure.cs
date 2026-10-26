namespace MusicConverter.MusicXml;

public sealed class MusicXmlMeasure
{
    public int Number { get; set; }
    public MusicXmlAttributes? Attributes { get; set; }
    public List<MusicXmlNote> Notes { get; } = new();

    public bool RepeatStart { get; set; }
    public bool RepeatEnd { get; set; }
    public int RepeatTimes { get; set; } = 2;
    public string? EndingNumbers { get; set; }
    public bool EndingStart { get; set; }
    public bool EndingStop { get; set; }
}