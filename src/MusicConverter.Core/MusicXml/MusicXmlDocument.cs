namespace MusicConverter.MusicXml;

/// <summary>
/// Version-neutral MusicXML document model produced by converters. Each supported MusicXML version
/// has its own namespace (e.g. <c>MusicConverter.MusicXml.V3_1</c>) containing an
/// <see cref="IMusicXmlVersionWriter"/>; adding a version never touches converters.
/// </summary>
public sealed class MusicXmlDocument
{
    private readonly HashSet<string> _usedFeatures = new(StringComparer.Ordinal);

    public string? Title { get; set; }
    public string? Composer { get; set; }
    public string Software { get; set; } = "MusicConverter";
    public string? SourceFile { get; set; }
    public string? ConverterName { get; set; }
    public List<MusicXmlPart> Parts { get; } = new();

    /// <summary>Non-fatal issues found during conversion (unsupported constructs, etc.).</summary>
    public List<string> Warnings { get; } = new();

    public IReadOnlyCollection<string> UsedFeatures => _usedFeatures;

    /// <summary>
    /// Records use of a versioned feature (e.g. "legacy.encoder-element"). Version writers declare which
    /// features they consider deprecated, and the serializer warns about them.
    /// </summary>
    public void UseFeature(string featureId) => _usedFeatures.Add(featureId);
}

public sealed class MusicXmlPart
{
    public string Id { get; set; } = "P1";
    public string Name { get; set; } = "Part";
    public List<MusicXmlMeasure> Measures { get; } = new();
}

public sealed class MusicXmlMeasure
{
    public int Number { get; set; }
    public MusicXmlAttributes? Attributes { get; set; }
    public List<MusicXmlNote> Notes { get; } = new();
}

public sealed class MusicXmlAttributes
{
    public int? Divisions { get; set; }
    public int? Fifths { get; set; }
    public int? Beats { get; set; }
    public int? BeatType { get; set; }
    public string? ClefSign { get; set; }
    public int? ClefLine { get; set; }
}

public sealed record MusicXmlPitch(char Step, int Alter, int Octave);

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

public sealed record MusicXmlDirection(string Kind, string Value, string? Unit = null);

public sealed record MusicXmlLyric(int Number, string Text, string Syllabic);
