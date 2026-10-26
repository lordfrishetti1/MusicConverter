namespace MusicConverter.Neutral;

/// <summary>Format-independent score produced by parsers and consumed by the MusicXML mapper.</summary>
public sealed class NeutralScore
{
    public string? Title { get; set; }
    public string? Composer { get; set; }
    public List<NeutralPart> Parts { get; } = new();
}