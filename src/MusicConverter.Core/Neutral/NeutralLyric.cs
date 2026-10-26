namespace MusicConverter.Neutral;

/// <summary>A lyric syllable; <see cref="Syllabic"/> is single, begin, middle or end.</summary>
public sealed record NeutralLyric(int Number, string Text, string Syllabic);