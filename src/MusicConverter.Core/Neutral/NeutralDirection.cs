namespace MusicConverter.Neutral;

/// <summary>A dynamic, wedge, tempo, words or pedal mark; <see cref="Kind"/> uses the MusicXML.DirectionKinds constants.</summary>
public sealed record NeutralDirection(string Kind, string Value, string? Unit = null);