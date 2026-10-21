namespace MusicConverter.Neutral;

/// <summary>Format-independent score produced by parsers and consumed by the MusicXML mapper.</summary>
public sealed class NeutralScore
{
    public string? Title { get; set; }
    public string? Composer { get; set; }
    public List<NeutralPart> Parts { get; } = new();
}

public sealed class NeutralPart
{
    public string Name { get; set; } = "Part";
    public List<NeutralMeasure> Measures { get; } = new();
}

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

public sealed class NeutralAttributes
{
    public int? Fifths { get; set; }
    public int? Beats { get; set; }
    public int? BeatType { get; set; }
    public string? ClefSign { get; set; }
    public int? ClefLine { get; set; }
}

public sealed record NeutralPitch(char Step, int Alter, int Octave);

public sealed class NeutralNote
{
    /// <summary>Empty for rests; several entries for chords.</summary>
    public List<NeutralPitch> Pitches { get; init; } = new();

    public bool IsRest => Pitches.Count == 0;

    /// <summary>Sounding duration in ticks (<see cref="DurationHelper.TicksPerQuarter"/> per quarter note).</summary>
    public int Ticks { get; set; }

    public bool TieToNext { get; set; }

    /// <summary>Tuplet ratio (actual notes in the time of normal notes); null when not a tuplet.</summary>
    public int? TupletActual { get; set; }
    public int? TupletNormal { get; set; }

    /// <summary>Voice number within the part (1-based); voices of one staff are written with &lt;backup&gt;.</summary>
    public int Voice { get; set; } = 1;

    public bool SlurStart { get; set; }
    public bool SlurStop { get; set; }

    /// <summary>Grace note: takes no time (Ticks only gives its written value) and is not counted in the measure.</summary>
    public bool IsGrace { get; set; }
    public bool GraceSlash { get; set; }

    /// <summary>MusicXML articulation names: accent, strong-accent, staccato, staccatissimo, tenuto, detached-legato.</summary>
    public List<string> Articulations { get; } = new();

    /// <summary>MusicXML ornament names: trill-mark, turn, inverted-turn, mordent, inverted-mordent, shake, ...</summary>
    public List<string> Ornaments { get; } = new();
    public bool Fermata { get; set; }

    /// <summary>Directions placed just before the note (see <see cref="NeutralDirection"/>).</summary>
    public List<NeutralDirection> Directions { get; } = new();

    public List<NeutralLyric> Lyrics { get; } = new();
}

/// <summary>A dynamic, wedge, tempo, words or pedal mark; <see cref="Kind"/> uses the MusicXML.DirectionKinds constants.</summary>
public sealed record NeutralDirection(string Kind, string Value, string? Unit = null);

/// <summary>A lyric syllable; <see cref="Syllabic"/> is single, begin, middle or end.</summary>
public sealed record NeutralLyric(int Number, string Text, string Syllabic);
