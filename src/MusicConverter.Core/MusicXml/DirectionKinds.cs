namespace MusicConverter.MusicXml;

/// <summary>Direction kinds understood by the version writers.</summary>
public static class DirectionKinds
{
    public const string Dynamics = "dynamics";

    /// <summary>Value is crescendo, diminuendo or stop.</summary>
    public const string Wedge = "wedge";

    /// <summary>Value is the number of beat units per minute; Unit names the beat unit (default quarter).</summary>
    public const string Tempo = "tempo";
    public const string Words = "words";

    /// <summary>Value is start or stop.</summary>
    public const string Pedal = "pedal";
}