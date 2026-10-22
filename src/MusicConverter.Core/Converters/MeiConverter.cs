using MusicConverter.Abstractions;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using MusicConverter.Logging;
using MusicConverter.MusicXml;
using MusicConverter.Neutral;

namespace MusicConverter.Converters;

/// <summary>Imports Music Encoding Initiative files (.mei).</summary>
public sealed class MeiConverter : MusicFormatConverterBase
{
    private static readonly string[] Extensions = { ".mei" };

    public MeiConverter() : this(null) { }

    public MeiConverter(ILogger? logger) : base(logger) { }

    public override string FormatName => "MEI";

    public override IReadOnlyCollection<string> FileExtensions => Extensions;

    protected override NeutralScore Parse(string text, ConversionContext context) =>
        new MeiParser(text, context).Parse();
}

internal sealed class MeiParser
{
    private sealed class Staff
    {
        public Staff(PartBuilder builder) => Builder = builder;
        public PartBuilder Builder { get; set; }
        public int Fifths;
        public int Capacity = DurationHelper.TicksPerMeasure(4, 4);
        public int BeatTicks = DurationHelper.TicksFor(4, 0);
        public Dictionary<(char Step, int Octave), int> MeasureAccidentals { get; } = new();
    }

    private readonly string _text;
    private readonly ConversionContext _ctx;
    private readonly NeutralScore _score = new();
    private readonly Dictionary<string, Staff> _staves = new();
    private readonly List<string> _order = new();
    private readonly HashSet<string> _tieStarts = new(StringComparer.Ordinal);

    private int? _beats, _beatType, _fifths;
    private string? _clefShape;
    private int? _clefLine;
    private int _measureTotal, _measuresDone;

    public MeiParser(string text, ConversionContext ctx)
    {
        _text = text;
        _ctx = ctx;
    }

    private static XDocument LoadXml(string text)
    {
        // DTDs are parsed (internal entities stay valid) but expansion is capped to defuse entity-expansion bombs.
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Parse,
            XmlResolver = null,
            MaxCharactersFromEntities = 1_000_000
        };
        using var reader = XmlReader.Create(new StringReader(text), settings);
        return XDocument.Load(reader, LoadOptions.SetLineInfo);
    }

    public NeutralScore Parse()
    {
        XDocument doc;
        try
        {
            doc = LoadXml(_text);
        }
        catch (XmlException ex)
        {
            if (!_ctx.Lenient) throw _ctx.ParseError($"Invalid XML: {ex.Message}", ex.LineNumber > 0 ? ex.LineNumber : null);
            try
            {
                doc = LoadXml(RepairXml(_text));
                _ctx.Warn($"The XML was malformed ({ex.Message}) and was repaired; some content may be missing.");
            }
            catch (XmlException)
            {
                throw _ctx.ParseError($"Invalid XML: {ex.Message}", ex.LineNumber > 0 ? ex.LineNumber : null);
            }
        }

        var root = doc.Root;
        if (root is null || root.Name.LocalName != "mei")
            throw _ctx.ParseError("Root element is not <mei>; not an MEI file", 1);

        ReadHeader(root);

        var score = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "score");
        if (score is null) throw _ctx.ParseError("No <score> element found in MEI file");

        _measureTotal = Math.Max(1, score.Descendants().Count(e => e.Name.LocalName == "measure"));
        Walk(score);

        foreach (var key in _order)
        {
            _staves[key].Builder.Flush();
            _score.Parts.Add(_staves[key].Builder.Part);
        }
        return _score;
    }

    private static readonly System.Text.RegularExpressions.Regex XmlToken = new(
        @"<!--.*?-->|<!\[CDATA\[.*?\]\]>|<\?.*?\?>|<!DOCTYPE[^>]*>|<(?<close>/)?(?<name>[A-Za-z_][\w:.\-]*)(?<rest>[^<>]*?)(?<self>/)?>",
        System.Text.RegularExpressions.RegexOptions.Singleline);

    /// <summary>Best-effort fix for damaged XML: drops illegal characters, escapes stray '&amp;', discards a truncated final tag and balances tags.</summary>
    private static string RepairXml(string text)
    {
        var cleaned = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
            if (ch is '\t' or '\n' or '\r' || (ch >= ' ' && ch != '\uFFFE' && ch != '\uFFFF')) cleaned.Append(ch);
        text = System.Text.RegularExpressions.Regex.Replace(
            cleaned.ToString(), @"&(?!(?:amp|lt|gt|quot|apos|#\d+|#x[0-9a-fA-F]+);)", "&amp;");

        var output = new System.Text.StringBuilder(text.Length + 64);
        var open = new Stack<string>();
        var position = 0;
        foreach (System.Text.RegularExpressions.Match m in XmlToken.Matches(text))
        {
            output.Append(text, position, m.Index - position);
            position = m.Index + m.Length;
            if (!m.Groups["name"].Success) { output.Append(m.Value); continue; }

            var name = m.Groups["name"].Value;
            if (m.Groups["close"].Success)
            {
                if (!open.Contains(name)) continue;
                while (open.Peek() != name) output.Append("</").Append(open.Pop()).Append('>');
                open.Pop();
                output.Append(m.Value);
            }
            else
            {
                if (!m.Groups["self"].Success) open.Push(name);
                output.Append(m.Value);
            }
        }

        var tail = text[position..];
        var cut = tail.IndexOf('<');
        output.Append(cut < 0 ? tail : tail[..cut]);
        while (open.Count > 0) output.Append("</").Append(open.Pop()).Append('>');
        return output.ToString();
    }

    private void ReadHeader(XElement root)
    {
        var head = root.Elements().FirstOrDefault(e => e.Name.LocalName == "meiHead");
        if (head is null) return;
        _score.Title = head.Descendants().FirstOrDefault(e => e.Name.LocalName == "title")?.Value.Trim();
        var composer = head.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "composer" ||
            (e.Name.LocalName == "persName" && (string?)e.Attribute("role") == "composer"));
        _score.Composer = composer?.Value.Trim();
    }

    private static string? Attr(XElement e, string name) => (string?)e.Attribute(name);

    private void Walk(XElement container)
    {
        using var nesting = _ctx.EnterNesting();
        foreach (var child in container.Elements())
        {
            _ctx.ThrowIfCancelled();
            try
            {
            switch (child.Name.LocalName)
            {
                case "scoreDef":
                    ReadScoreDef(child);
                    break;
                case "measure":
                    ReadMeasure(child);
                    break;
                case "ending":
                    ReadEnding(child);
                    break;
                case "section":
                case "app":
                case "lem":
                case "mdiv":
                case "score":
                    Walk(child);
                    break;
                case "rdg":
                    _ctx.Warn("Alternative readings (<rdg>) were ignored");
                    break;
            }
            }
            catch (Exception ex) when (_ctx.Recover(ex)) { }
        }
    }

    private void ReadScoreDef(XElement def)
    {
        ReadDefaults(def);
        ApplyToAll(def);

        foreach (var staffDef in def.Descendants().Where(e => e.Name.LocalName == "staffDef"))
        {
            var key = Attr(staffDef, "n") ?? "1";
            var staff = GetStaff(key);
            var label = Attr(staffDef, "label") ??
                        staffDef.Elements().FirstOrDefault(e => e.Name.LocalName == "label")?.Value.Trim();
            if (!string.IsNullOrWhiteSpace(label)) staff.Builder.Part.Name = label!;
            ApplyDef(staff, staffDef);
        }
    }

    // Defaults remembered from <scoreDef> so staves that appear later inherit them.
    private void ReadDefaults(XElement def)
    {
        if (ParseMeter(def) is var (beats, unit)) { _beats = beats; _beatType = unit; }
        if (KeyOf(def) is int fifths) _fifths = fifths;
        if (ReadClef(def) is var (shape, line)) { _clefShape = shape; _clefLine = line; }
    }

    private void ApplyToAll(XElement def)
    {
        foreach (var staff in _staves.Values) ApplyDef(staff, def);
    }

    private void ApplyDef(Staff staff, XElement def)
    {
        if (ParseMeter(def) is var (beats, unit))
        {
            staff.Builder.SetTime(beats, unit);
            staff.Capacity = DurationHelper.TicksPerMeasure(beats, unit);
            staff.BeatTicks = DurationHelper.TicksFor(unit, 0);
        }
        if (KeyOf(def) is int fifths)
        {
            staff.Fifths = fifths;
            staff.Builder.SetKey(fifths);
        }
        if (ReadClef(def) is var (shape, line)) staff.Builder.SetClef(shape, line);
    }

    private (int Beats, int Unit)? ParseMeter(XElement def)
    {
        // MEI 4+ also allows a <meterSig> child carrying the same information as the @meter.* attributes.
        var child = def.Elements().FirstOrDefault(e => e.Name.LocalName == "meterSig");
        var sym = Attr(def, "meter.sym") ?? (child != null ? Attr(child, "sym") : null);
        if (sym == "common") return (4, 4);
        if (sym == "cut") return (2, 2);

        var count = Attr(def, "meter.count") ?? (child != null ? Attr(child, "count") : null);
        var unit = Attr(def, "meter.unit") ?? (child != null ? Attr(child, "unit") : null);
        if (count is null || unit is null) return null;

        var first = new string(count.TakeWhile(char.IsDigit).ToArray());
        if (!int.TryParse(first, out var beats) || !int.TryParse(unit, out var beatType) ||
            beats <= 0 || !DurationHelper.IsValidNoteValue(beatType))
            throw _ctx.ParseError($"Invalid meter {count}/{unit}", LineOf(def));
        if (count.Contains('+')) _ctx.Warn("Additive meters are reduced to their first group");
        return (beats, beatType);
    }

    private int? KeyOf(XElement def)
    {
        var child = def.Elements().FirstOrDefault(e => e.Name.LocalName == "keySig");
        return ParseKey(Attr(def, "key.sig") ?? (child != null ? Attr(child, "sig") : null));
    }

    private int? ParseKey(string? sig)
    {
        if (string.IsNullOrWhiteSpace(sig) || sig == "mixed") return null;
        if (sig == "0") return 0;
        var digits = new string(sig.TakeWhile(char.IsDigit).ToArray());
        if (digits.Length == 0 || !int.TryParse(digits, out var count) || sig.Length != digits.Length + 1)
            throw _ctx.ParseError($"Invalid key signature '{sig}'");
        return sig[^1] switch
        {
            's' => count,
            'f' => -count,
            _ => throw _ctx.ParseError($"Invalid key signature '{sig}'")
        };
    }

    private (string Shape, int Line)? ReadClef(XElement def)
    {
        var clef = def.Elements().FirstOrDefault(e => e.Name.LocalName == "clef");
        var shape = Attr(def, "clef.shape") ?? (clef != null ? Attr(clef, "shape") : null);
        if (shape is null) return null;
        var lineText = Attr(def, "clef.line") ?? (clef != null ? Attr(clef, "line") : null);
        var line = int.TryParse(lineText, out var parsed) ? parsed : shape switch { "F" => 4, "C" => 3, _ => 2 };
        return (shape, line);
    }

    private Staff GetStaff(string key)
    {
        if (_staves.TryGetValue(key, out var existing)) return existing;

        var builder = new PartBuilder($"Staff {key}") { AutoFlush = false };
        var staff = new Staff(builder);
        if (_beats is int beats && _beatType is int unit)
        {
            builder.SetTime(beats, unit);
            staff.Capacity = DurationHelper.TicksPerMeasure(beats, unit);
            staff.BeatTicks = DurationHelper.TicksFor(unit, 0);
        }
        if (_fifths is int fifths)
        {
            staff.Fifths = fifths;
            builder.SetKey(fifths);
        }
        if (_clefShape is { } shape) builder.SetClef(shape, _clefLine ?? 2);
        _staves[key] = staff;
        _order.Add(key);
        return staff;
    }

    private string? _ending;
    private readonly HashSet<string> _endingStarted = new();

    private void ReadEnding(XElement ending)
    {
        var raw = Attr(ending, "n") ?? Attr(ending, "label") ?? string.Empty;
        var numbers = string.Join(", ", raw.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.TrimEnd('.')));
        _ending = numbers.Length > 0 ? numbers : "1";
        _endingStarted.Clear();
        try { Walk(ending); }
        finally
        {
            foreach (var key in _endingStarted)
                if (_staves[key].Builder.Part.Measures.Count > 0) _staves[key].Builder.Part.Measures[^1].EndingStop = true;
            _ending = null;
            _endingStarted.Clear();
        }
    }

    private void ApplyBarlines(Staff staff, string staffKey, XElement measureElement)
    {
        var measures = staff.Builder.Part.Measures;
        if (measures.Count == 0) return;
        var target = measures[^1];
        var left = Attr(measureElement, "left");
        var right = Attr(measureElement, "right");
        if (left is "rptstart" or "rptboth") target.RepeatStart = true;
        if (right is "rptend" or "rptboth") target.RepeatEnd = true;
        if (_ending != null)
        {
            target.EndingNumbers = _ending;
            if (_endingStarted.Add(staffKey)) target.EndingStart = true;
        }
    }

    private void ReadMeasure(XElement measure)
    {
        _measuresDone++;
        _ctx.ReportParsing(_measuresDone / (double)_measureTotal, "Parsing MEI");

        // <tie startid="#n1" endid="#n2"/> control events are equivalent to @tie="i" on the start note.
        _tieStarts.Clear();
        foreach (var tie in measure.Elements().Where(e => e.Name.LocalName == "tie"))
        {
            if (Attr(tie, "startid") is { } startId) _tieStarts.Add(startId.TrimStart('#'));
        }
        CollectEvents(measure);

        foreach (var staffElement in measure.Elements().Where(e => e.Name.LocalName == "staff"))
        {
            _staffKey = Attr(staffElement, "n") ?? "1";
            var staff = GetStaff(_staffKey);
            staff.MeasureAccidentals.Clear();

            var layers = staffElement.Elements().Where(e => e.Name.LocalName == "layer").ToList();
            var before = staff.Builder.MeasureCount;
            _onset = 0;
            _recordLayer = true;
            if (layers.Count > 0) ReadLayerContent(staff, layers[0]);
            _recordLayer = false;
            staff.Builder.Flush();
            if (staff.Builder.MeasureCount > before) ApplyBarlines(staff, _staffKey, measure);
            // Further layers are extra voices of the same staff, laid over the measure the first layer produced.
            for (var i = 1; i < layers.Count; i++)
            {
                _onset = 0;
                var main = staff.Builder;
                var voiceBuilder = main.Fork();
                voiceBuilder.AutoFlush = false;
                staff.Builder = voiceBuilder;
                staff.MeasureAccidentals.Clear();
                try
                {
                    ReadLayerContent(staff, layers[i]);
                    voiceBuilder.Flush();
                }
                finally
                {
                    staff.Builder = main;
                }

                var voiceNumber = int.TryParse(Attr(layers[i], "n"), out var n) && n > 1 ? n : i + 1;
                foreach (var extra in voiceBuilder.Part.Measures.SelectMany(m => m.Notes)) extra.Voice = voiceNumber;
                var notes = voiceBuilder.Part.Measures.SelectMany(m => m.Notes).ToList();
                if (notes.Count == 0) continue;
                if (main.MeasureCount > before)
                {
                    main.Part.Measures[^1].Notes.AddRange(notes);
                }
                else
                {
                    foreach (var note in notes) { note.Voice = 1; main.Add(note); }
                    main.Flush();
                }
            }
        }

        ResolveEvents();
    }

    private static readonly Dictionary<string, string> ArticMap = new(StringComparer.Ordinal)
    {
        ["stacc"] = "staccato", ["ten"] = "tenuto", ["acc"] = "accent", ["marc"] = "strong-accent",
        ["stacciss"] = "staccatissimo"
    };

    private static readonly HashSet<string> DynamicNames = new(StringComparer.Ordinal)
        { "ppp", "pp", "p", "mp", "mf", "f", "ff", "fff", "sf", "sfz", "fp", "sfp", "rfz", "fz" };

    private sealed record MeiEvent(int Order, string? Id, string Staff, double Beat, Action<NeutralNote> Apply);

    private readonly List<MeiEvent> _events = new();
    private readonly Dictionary<string, NeutralNote> _idNotes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(int Onset, NeutralNote Note)>> _layerNotes = new();
    private string _staffKey = "1";
    private int _onset;
    private bool _recordLayer;
    private (bool Active, bool Slash) _graceGroup;

    private static string? IdOf(XElement e) => (string?)e.Attribute(XNamespace.Xml + "id");

    private static string StaffOf(XElement e) =>
        (Attr(e, "staff") ?? "1").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "1";

    private static double? Beat(string? value) =>
        double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var b) ? b : null;

    private void CollectEvents(XElement measure)
    {
        _events.Clear();
        _idNotes.Clear();
        _layerNotes.Clear();
        foreach (var e in measure.Elements())
        {
            var name = e.Name.LocalName;
            switch (name)
            {
                case "dynam":
                {
                    var text = e.Value.Trim();
                    var lower = text.ToLowerInvariant();
                    var direction = DynamicNames.Contains(lower)
                        ? new NeutralDirection(DirectionKinds.Dynamics, lower)
                        : new NeutralDirection(DirectionKinds.Words, text);
                    AddStart(e, 1, n => n.Directions.Add(direction));
                    break;
                }
                case "dir":
                    if (e.Value.Trim() is { Length: > 0 } dir)
                        AddStart(e, 1, n => n.Directions.Add(new NeutralDirection(DirectionKinds.Words, dir)));
                    break;
                case "tempo":
                    AddTempo(e);
                    break;
                case "hairpin":
                {
                    var kind = Attr(e, "form") is "dim" or "decres" or "dimin" ? "diminuendo" : "crescendo";
                    AddStart(e, 1, n => n.Directions.Add(new NeutralDirection(DirectionKinds.Wedge, kind)));
                    AddEnd(e, n => n.Directions.Add(new NeutralDirection(DirectionKinds.Wedge, "stop")));
                    break;
                }
                case "fermata":
                    AddStart(e, 1, n => n.Fermata = true);
                    break;
                case "trill":
                    AddStart(e, 1, n => AddOrnament(n, "trill-mark"));
                    break;
                case "mordent":
                    AddStart(e, 1, n => AddOrnament(n, Attr(e, "form") == "upper" ? "inverted-mordent" : "mordent"));
                    break;
                case "turn":
                    AddStart(e, 1, n => AddOrnament(n, Attr(e, "form") == "lower" ? "inverted-turn" : "turn"));
                    break;
                case "slur":
                    AddStart(e, 1, n => n.SlurStart = true);
                    AddEnd(e, n => n.SlurStop = true);
                    break;
            }
        }
    }

    private static void AddOrnament(NeutralNote note, string name)
    {
        if (!note.IsRest && !note.Ornaments.Contains(name)) note.Ornaments.Add(name);
    }

    private void AddStart(XElement e, int order, Action<NeutralNote> apply)
    {
        var id = Attr(e, "startid")?.TrimStart('#');
        var beat = Beat(Attr(e, "tstamp"));
        if (id == null && beat == null) return;
        _events.Add(new MeiEvent(order, id, StaffOf(e), beat ?? 1, apply));
    }

    private void AddEnd(XElement e, Action<NeutralNote> apply)
    {
        var id = Attr(e, "endid")?.TrimStart('#');
        if (id != null) { _events.Add(new MeiEvent(0, id, StaffOf(e), 0, apply)); return; }

        // tstamp2 is "<measures>m+<beat>"; an end in a later measure is closed on this measure's last note.
        var m = Regex.Match(Attr(e, "tstamp2") ?? string.Empty, @"^(\d+)m\+([\d.]+)$");
        var beat = m.Success && m.Groups[1].Value == "0" ? Beat(m.Groups[2].Value) : null;
        _events.Add(new MeiEvent(0, null, StaffOf(e), beat ?? double.PositiveInfinity, apply));
    }

    private void AddTempo(XElement e)
    {
        var text = e.Value.Trim();
        var bpm = Beat(Attr(e, "midi.bpm") ?? Attr(e, "mm"));
        var unit = Attr(e, "mm.unit") switch { "1" => "whole", "2" => "half", "8" => "eighth", "16" => "16th", _ => "quarter" };
        AddStart(e, 1, n =>
        {
            if (text.Length > 0) n.Directions.Add(new NeutralDirection(DirectionKinds.Words, text));
            if (bpm is > 0) n.Directions.Add(new NeutralDirection(DirectionKinds.Tempo,
                Math.Round(bpm.Value).ToString(System.Globalization.CultureInfo.InvariantCulture), unit));
        });
    }

    private void ResolveEvents()
    {
        foreach (var ev in _events.OrderBy(x => x.Order))
        {
            NeutralNote? target = null;
            if (ev.Id != null) _idNotes.TryGetValue(ev.Id, out target);
            else if (_layerNotes.TryGetValue(ev.Staff, out var notes) && notes.Count > 0)
            {
                var beatTicks = _staves.TryGetValue(ev.Staff, out var staff) ? staff.BeatTicks : DurationHelper.TicksFor(4, 0);
                var at = double.IsPositiveInfinity(ev.Beat) ? int.MaxValue : (int)Math.Round((ev.Beat - 1) * beatTicks);
                target = notes.LastOrDefault(x => x.Onset <= at).Note ?? notes[0].Note;
            }
            if (target != null) ev.Apply(target);
            else _ctx.Warn("A control event (dynamic, slur, ornament, ...) could not be attached to a note and was ignored");
        }
    }

    private void Track(NeutralNote note, XElement element)
    {
        if (IdOf(element) is { } id) _idNotes[id] = note;
        foreach (var child in element.Descendants().Where(d => IdOf(d) != null)) _idNotes[IdOf(child)!] = note;
        if (!note.IsGrace)
        {
            if (_recordLayer)
            {
                if (!_layerNotes.TryGetValue(_staffKey, out var list)) _layerNotes[_staffKey] = list = new();
                list.Add((_onset, note));
            }
            _onset += note.Ticks;
        }
    }

    private static void ReadMarks(XElement element, NeutralNote note)
    {
        var codes = new List<string>();
        if (Attr(element, "artic") is { } attr) codes.AddRange(attr.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        foreach (var child in element.Elements().Where(e => e.Name.LocalName == "artic"))
            if (Attr(child, "artic") is { } a) codes.AddRange(a.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        foreach (var code in codes)
            if (ArticMap.TryGetValue(code, out var name) && !note.Articulations.Contains(name)) note.Articulations.Add(name);

        if (Attr(element, "fermata") != null) note.Fermata = true;
        if (Attr(element, "slur") is { } slur)
        {
            if (slur.StartsWith('i')) note.SlurStart = true;
            else if (slur.StartsWith('t')) note.SlurStop = true;
        }
    }

    private void ReadLayerContent(Staff staff, XElement container, int? tupletActual = null, int? tupletNormal = null)
    {
        using var nesting = _ctx.EnterNesting();
        foreach (var element in container.Elements())
        {
            _ctx.ThrowIfCancelled();
            try
            {
            switch (element.Name.LocalName)
            {
                case "note":
                case "chord":
                    ReadNoteOrChord(staff, element, tupletActual, tupletNormal);
                    break;
                case "rest":
                case "space":
                {
                    var restNote = new NeutralNote
                    {
                        Ticks = ReadTicks(element, tupletActual, tupletNormal),
                        TupletActual = tupletActual,
                        TupletNormal = tupletNormal
                    };
                    if (_graceGroup.Active) break;
                    ReadMarks(element, restNote);
                    Track(restNote, element);
                    staff.Builder.Add(restNote);
                    break;
                }
                case "mRest":
                case "mSpace":
                {
                    var measureRest = new NeutralNote { Ticks = staff.Capacity };
                    Track(measureRest, element);
                    staff.Builder.Add(measureRest);
                    break;
                }
                case "beam":
                    ReadLayerContent(staff, element, tupletActual, tupletNormal);
                    break;
                case "tuplet":
                    ReadTuplet(staff, element);
                    break;
                case "graceGrp":
                {
                    var was = _graceGroup;
                    _graceGroup = (true, Attr(element, "grace") == "acc");
                    try { ReadLayerContent(staff, element); }
                    finally { _graceGroup = was; }
                    break;
                }
                case "clef":
                    if (ReadClefElement(element) is var (shape, line)) staff.Builder.SetClef(shape, line);
                    break;
                case "barLine":
                case "artic":
                case "accid":
                case "dot":
                    break;
                default:
                    _ctx.Warn($"MEI element <{element.Name.LocalName}> is not supported and was ignored");
                    break;
            }
            }
            catch (Exception ex) when (_ctx.Recover(ex)) { }
        }
    }

    private static (string Shape, int Line)? ReadClefElement(XElement clef)
    {
        var shape = Attr(clef, "shape");
        if (shape is null) return null;
        return (shape, int.TryParse(Attr(clef, "line"), out var line) ? line : 2);
    }

    private void ReadTuplet(Staff staff, XElement tuplet)
    {
        if (!int.TryParse(Attr(tuplet, "num"), out var num) || num <= 0)
            throw _ctx.ParseError("<tuplet> requires a numeric @num", LineOf(tuplet));
        var numBase = int.TryParse(Attr(tuplet, "numbase"), out var b) && b > 0 ? b : DefaultNumBase(num);
        ReadLayerContent(staff, tuplet, num, numBase);
    }

    private static int DefaultNumBase(int num)
    {
        var power = 1;
        while (power * 2 < num) power *= 2;
        return power;
    }

    private void ReadNoteOrChord(Staff staff, XElement element, int? tupletActual, int? tupletNormal)
    {
        var graceAttr = Attr(element, "grace");
        var isGrace = graceAttr != null || _graceGroup.Active;

        var pitches = new List<NeutralPitch>();
        var tie = false;
        XElement durationSource = element;

        if (element.Name.LocalName == "chord")
        {
            foreach (var note in element.Elements().Where(e => e.Name.LocalName == "note"))
            {
                pitches.Add(ReadPitch(staff, note));
                tie |= IsTieStart(note);
            }
            if (pitches.Count == 0) throw _ctx.ParseError("<chord> contains no notes", LineOf(element));
        }
        else
        {
            pitches.Add(ReadPitch(staff, element));
            tie = IsTieStart(element);
        }

        var ticks = ReadTicks(element, tupletActual, tupletNormal);
        var neutral = new NeutralNote
        {
            Pitches = pitches,
            Ticks = ticks,
            TieToNext = tie,
            TupletActual = isGrace ? null : tupletActual,
            TupletNormal = isGrace ? null : tupletNormal,
            IsGrace = isGrace,
            GraceSlash = isGrace && (graceAttr == "acc" || _graceGroup is { Active: true, Slash: true })
        };
        ReadVerses(element, neutral);
        ReadMarks(element, neutral);
        Track(neutral, element);
        staff.Builder.Add(neutral);
    }

    private static void ReadVerses(XElement element, NeutralNote note)
    {
        var seen = new HashSet<int>();
        var auto = 0;
        foreach (var verse in element.Descendants().Where(e => e.Name.LocalName == "verse"))
        {
            auto++;
            var number = int.TryParse(Attr(verse, "n"), out var n) ? n : auto;
            var syl = verse.Elements().FirstOrDefault(e => e.Name.LocalName == "syl");
            var text = syl?.Value.Trim();
            if (string.IsNullOrEmpty(text) || !seen.Add(number)) continue;
            var syllabic = Attr(syl!, "wordpos") switch
            {
                "i" => "begin",
                "m" => "middle",
                "t" => "end",
                _ => "single"
            };
            note.Lyrics.Add(new NeutralLyric(number, text, syllabic));
        }
    }

    private bool IsTieStart(XElement note) =>
        Attr(note, "tie") is "i" or "m" ||
        ((string?)note.Attribute(XNamespace.Xml + "id") is { } id && _tieStarts.Contains(id));

    private NeutralPitch ReadPitch(Staff staff, XElement note)
    {
        var name = Attr(note, "pname");
        if (name is null || name.Length != 1 || "abcdefg".IndexOf(char.ToLowerInvariant(name[0])) < 0)
            throw _ctx.ParseError($"Note has missing or invalid @pname '{name}'", LineOf(note));
        if (!int.TryParse(Attr(note, "oct"), out var octave))
            throw _ctx.ParseError("Note has missing or invalid @oct", LineOf(note));

        var step = char.ToUpperInvariant(name[0]);
        var childAccid = note.Elements().FirstOrDefault(e => e.Name.LocalName == "accid");
        var accidText = Attr(note, "accid.ges") ?? Attr(note, "accid") ??
                        (childAccid != null ? Attr(childAccid, "accid.ges") ?? Attr(childAccid, "accid") : null);
        var written = AlterFromAccidental(accidText, note);
        var key = (step, octave);
        int alter;
        if (written is int explicitAlter)
        {
            alter = explicitAlter;
            staff.MeasureAccidentals[key] = alter;
        }
        else if (staff.MeasureAccidentals.TryGetValue(key, out var carried))
        {
            alter = carried;
        }
        else
        {
            alter = KeySignatureHelper.AlterFor(step, staff.Fifths);
        }
        return new NeutralPitch(step, alter, octave);
    }

    private int? AlterFromAccidental(string? accid, XElement note) => accid switch
    {
        null => null,
        "n" => 0,
        "s" => 1,
        "f" => -1,
        "ss" or "x" => 2,
        "ff" => -2,
        _ => throw _ctx.ParseError($"Unsupported accidental '{accid}'", LineOf(note))
    };

    private int ReadTicks(XElement element, int? tupletActual, int? tupletNormal)
    {
        var durText = Attr(element, "dur");
        if (durText is null)
            throw _ctx.ParseError($"<{element.Name.LocalName}> has no @dur", LineOf(element));

        int written;
        if (durText == "breve")
        {
            _ctx.Warn("Breve durations are notated as whole notes with a doubled duration");
            written = DurationHelper.WholeTicks * 2;
        }
        else if (int.TryParse(durText, out var value) && DurationHelper.IsValidNoteValue(value))
        {
            var dots = int.TryParse(Attr(element, "dots"), out var d) ? d : 0;
            written = DurationHelper.TicksFor(value, Math.Clamp(dots, 0, 3));
        }
        else
        {
            throw _ctx.ParseError($"Unsupported duration '{durText}'", LineOf(element));
        }

        if (tupletActual is int a && tupletNormal is int n)
        {
            if (written * n % a != 0)
            {
                _ctx.Warn("A tuplet duration cannot be represented exactly and was approximated");
                return (int)Math.Round(written * (double)n / a);
            }
            return written * n / a;
        }
        return written;
    }

    private static int? LineOf(XElement element) =>
        element is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : null;
}
