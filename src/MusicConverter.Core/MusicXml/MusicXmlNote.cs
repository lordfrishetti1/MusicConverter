namespace MusicConverter.MusicXml;

public sealed class MusicXmlNote
{
    public bool IsRest { get; set; }
    public bool IsChord { get; set; }
    public MusicXmlPitch? Pitch { get; set; }
    public int Duration { get; set; }
    public string Type { get; set; } = "quarter";
    public int Dots { get; set; }
    public bool TieStart { get; set; }
    public bool TieStop { get; set; }
    public int? TupletActual { get; set; }
    public int? TupletNormal { get; set; }

    /// <summary>First / last note of a tuplet group; a bracket is written between them.</summary>
    public bool TupletStart { get; set; }
    public bool TupletStop { get; set; }
    public int Voice { get; set; } = 1;

    /// <summary>Divisions to step back (a &lt;backup&gt; element) before this note; used between voices.</summary>
    public int BackupBefore { get; set; }

    public bool SlurStart { get; set; }
    public bool SlurStop { get; set; }

    /// <summary>Grace note: it takes no time and is written without a &lt;duration&gt;.</summary>
    public bool IsGrace { get; set; }
    public bool GraceSlash { get; set; }

    /// <summary>MusicXML articulation element names (accent, staccato, tenuto, ...).</summary>
    public List<string> Articulations { get; } = new();

    /// <summary>MusicXML ornament element names (trill-mark, turn, mordent, ...).</summary>
    public List<string> Ornaments { get; } = new();
    public bool Fermata { get; set; }

    /// <summary>Directions (dynamics, wedges, tempo, words, pedal) written immediately before this note.</summary>
    public List<MusicXmlDirection> Directions { get; } = new();
    public List<MusicXmlLyric> Lyrics { get; } = new();
}