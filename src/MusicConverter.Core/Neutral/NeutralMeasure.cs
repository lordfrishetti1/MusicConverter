namespace MusicConverter.Neutral;

public sealed class NeutralMeasure
{
    public int Number { get; set; }

    /// <summary>Only the attributes that change (or all of them for the first measure).</summary>
    public NeutralAttributes? Attributes { get; set; }

    public List<NeutralNote> Notes { get; } = new();

    /// <summary>Forward repeat sign at the start of this measure.</summary>
    public bool RepeatStart { get; set; }

    /// <summary>Backward repeat sign at the end of this measure; <see cref="RepeatTimes"/> is the total play count (2 by default).</summary>
    public bool RepeatEnd { get; set; }
    public int RepeatTimes { get; set; } = 2;

    /// <summary>Volta bracket numbers such as "1" or "1, 2" when this measure belongs to an ending.</summary>
    public string? EndingNumbers { get; set; }
    public bool EndingStart { get; set; }
    public bool EndingStop { get; set; }
}