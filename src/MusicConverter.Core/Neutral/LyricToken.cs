namespace MusicConverter.Neutral;

/// <summary>One lyric token: a syllable, or a skip (<see cref="Text"/> null) that leaves its note without a syllable.</summary>
public sealed record LyricToken(string? Text, bool HyphenAfter);