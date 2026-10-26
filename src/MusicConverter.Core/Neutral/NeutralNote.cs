namespace MusicConverter.Neutral;

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