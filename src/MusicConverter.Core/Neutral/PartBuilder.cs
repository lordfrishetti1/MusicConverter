namespace MusicConverter.Neutral;

/// <summary>Collects notes into measures for a single part; shared by all parsers.</summary>
public sealed class PartBuilder
{
    private readonly List<NeutralNote> _current = new();
    private NeutralAttributes? _pending;
    private int _ticks;
    private int _capacity;
    private int? _pickupCapacity;

    public PartBuilder(string name) => Part = new NeutralPart { Name = name };

    public NeutralPart Part { get; }

    /// <summary>When true a measure closes automatically once the time signature is filled.</summary>
    public bool AutoFlush { get; set; } = true;

    public int MeasureCount => Part.Measures.Count;

    public int CapacityTicks => _capacity;

    /// <summary>Ticks already collected for the measure that is still open.</summary>
    public int PendingTicks => _ticks;

    /// <summary>The length requested by the last <see cref="SetPickup"/> if the pickup measure is still open.</summary>
    public int? PickupTicks => _pickupCapacity;

    /// <summary>Creates a builder with the same measure length, used for an additional voice of the same staff.</summary>
    public PartBuilder Fork() => new(Part.Name)
    {
        AutoFlush = AutoFlush,
        _capacity = _capacity,
        _pickupCapacity = _pickupCapacity
    };

    /// <summary>Raised after every measure is closed.</summary>
    public event Action? MeasureCompleted;

    public void SetTime(int beats, int beatType)
    {
        if (beats <= 0 || beatType <= 0) throw new ArgumentOutOfRangeException(nameof(beats));
        var p = Pending();
        p.Beats = beats;
        p.BeatType = beatType;
        _capacity = DurationHelper.TicksPerMeasure(beats, beatType);
    }

    public void SetKey(int fifths) => Pending().Fifths = fifths;

    public void SetClef(string sign, int line)
    {
        var p = Pending();
        p.ClefSign = sign;
        p.ClefLine = line;
    }

    /// <summary>Makes the next measure shorter (anacrusis).</summary>
    public void SetPickup(int ticks) => _pickupCapacity = ticks;

    public void Add(NeutralNote note)
    {
        _current.Add(note);
        if (note.IsGrace) return;
        _ticks += note.Ticks;
        var capacity = _pickupCapacity ?? _capacity;
        if (AutoFlush && capacity > 0 && _ticks >= capacity) Flush();
    }

    public void Flush()
    {
        if (_current.Count == 0) return;
        if (_current.All(n => n.IsGrace)) return; // grace notes wait for the note they decorate
        var measure = new NeutralMeasure { Number = Part.Measures.Count + 1, Attributes = _pending };
        measure.Notes.AddRange(_current);
        Part.Measures.Add(measure);
        _current.Clear();
        _pending = null;
        _ticks = 0;
        _pickupCapacity = null;
        MeasureCompleted?.Invoke();
    }

    private NeutralAttributes Pending() => _pending ??= new NeutralAttributes();
}
