using System.Text.RegularExpressions;
using MusicConverter.Logging;
using MusicConverter.Neutral;

namespace MusicConverter.Converters;

/// <summary>Imports MusiXTeX sources (.tex / .mtx) that use the core note, rest and signature macros.</summary>
public sealed class MusiXTeXConverter : MusicFormatConverterBase
{
    private static readonly string[] Extensions = { ".tex", ".mtx" };

    private static readonly Regex Signature = new(
        @"musixtex|\\startpiece|\\startextract|\\begin\s*\{music\}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public MusiXTeXConverter() : this(null) { }

    public MusiXTeXConverter(ILogger? logger) : base(logger) { }

    public override string FormatName => "MusiXTeX";

    public override IReadOnlyCollection<string> FileExtensions => Extensions;

    public override bool CanRead(string filePath)
    {
        if (!base.CanRead(filePath)) return false;
        if (string.Equals(Path.GetExtension(filePath), ".mtx", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            using var reader = new StreamReader(filePath);
            var buffer = new char[65536];
            var read = reader.Read(buffer, 0, buffer.Length);
            return Signature.IsMatch(new string(buffer, 0, read));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    protected override NeutralScore Parse(string text, ConversionContext context) =>
        new MusiXTeXParser(text, context).Parse();
}

/// <summary>
/// Pitch letters follow MusiXTeX: digits 0-9 are the lowest steps, then A-N, then a-n (and o-z), each
/// letter one diatonic step higher than the last. 'A' is A1, so 'c' is middle C (C4) and 'e' the bottom
/// line of the treble staff. The mapping does not depend on the clef.
/// </summary>
internal sealed class MusiXTeXParser
{
    private const int FirstLetterStep = 12; // 'A' = A1 = 1 * 7 + 5

    private static readonly Regex NoteCommand = new(
        @"^(?<z>z?)(?<base>wh|w|h|q|c{1,4})(?<dir>[ula]?)(?<dots>p{0,2})$", RegexOptions.Compiled);

    private static readonly Regex MeterFraction = new(
        @"^\s*\{?\s*\\meterfrac\s*(?:\{\s*(\d+)\s*\}\s*\{\s*(\d+)\s*\}|(\d)(\d))", RegexOptions.Compiled);

    private static readonly Regex IntegerArgument = new(@"^\s*\{\s*(-?\d+)\s*\}", RegexOptions.Compiled);
    private static readonly Regex StaffIntegerArgument = new(@"^\s*\{?\s*(\d+)\s*\}?\s*\{\s*(-?\d+)\s*\}", RegexOptions.Compiled);
    private static readonly Regex ClefArgument = new(@"^\s*\{?\s*(\d+)\s*\}?\s*\{?\s*\\([A-Za-z]+)", RegexOptions.Compiled);
    private static readonly Regex NameArgument = new(@"^\s*\{?\s*(\d+)\s*\}?\s*\{([^{}]*)\}", RegexOptions.Compiled);

    private static readonly Dictionary<string, int> Rests = new(StringComparer.Ordinal)
    {
        ["pause"] = 1, ["hpause"] = 2, ["qp"] = 4, ["soupir"] = 4, ["ds"] = 8, ["dsoupir"] = 8,
        ["qs"] = 16, ["qsoupir"] = 16, ["hs"] = 32, ["hsoupir"] = 32
    };

    private static readonly HashSet<string> BarCommands = new(StringComparer.Ordinal)
    {
        "bar", "xbar", "zbar", "doublebar", "leftrepeat", "rightrepeat", "leftrightrepeat", "endpiece", "Endpiece"
    };

    private static readonly HashSet<string> IgnoredCommands = new(StringComparer.Ordinal)
    {
        "sk", "off", "en", "enotes", "relax", "hfil", "hss", "vskip", "qqsk", "qsk", "hsk", "wsk", "noteskip",
        "input", "def", "advance", "setclef", "startextract", "startpiece", "endextract", "endpiece", "begin", "end",
        "instrumentnumber", "setname", "setsign", "generalsignature", "generalmeter", "relativeaccid", "stoppiece",
        "contpiece", "zendextract", "zendpiece", "nobarnumbers", "barnumbers", "mulooseness", "smallmusicsize",
        "normalmusicsize", "largemusicsize", "NOTEs", "title", "fulltitle", "author", "maketitle", "subtitle",
        "setstaffs", "systemnumbers", "setlength", "parindent", "parskip"
    };

    private sealed class Staff
    {
        public Staff(PartBuilder builder) => Builder = builder;
        public PartBuilder Builder { get; }
        public int Fifths;
        public Dictionary<(char Step, int Octave), int> Accidentals { get; } = new();
        public List<NeutralPitch> PendingChord { get; } = new();
    }

    private readonly string _text;
    private readonly ConversionContext _ctx;
    private readonly NeutralScore _score = new();
    private readonly List<Staff> _staves = new();
    private readonly Dictionary<int, string> _names = new();

    private int? _beats, _beatType, _fifths;
    private string? _clefSign;
    private int _clefLine;
    private bool _inSlice;
    private int _staffIndex;

    public MusiXTeXParser(string text, ConversionContext ctx)
    {
        _text = Regex.Replace(text, @"(?<!\\)%[^\n]*", string.Empty);
        _ctx = ctx;
    }

    public NeutralScore Parse()
    {
        var i = 0;
        while (i < _text.Length)
        {
            _ctx.ThrowIfCancelled();
            var ch = _text[i];
            if (ch == '\\')
            {
                i = ReadCommand(i);
                _ctx.ReportParsing(i / (double)_text.Length, "Parsing MusiXTeX");
            }
            else
            {
                if (ch == '&' && _inSlice) _staffIndex++;
                i++;
            }
        }
        EndSlice();

        foreach (var staff in _staves)
        {
            staff.Builder.Flush();
            if (staff.Builder.MeasureCount > 0) _score.Parts.Add(staff.Builder.Part);
        }
        return _score;
    }

    private int LineAt(int position) => 1 + _text.AsSpan(0, Math.Min(position, _text.Length)).Count('\n');

    private int ReadCommand(int start)
    {
        var i = start + 1;
        if (i >= _text.Length) return i;
        if (!char.IsLetter(_text[i]) && _text[i] != '@') return i + 1;

        while (i < _text.Length && (char.IsLetter(_text[i]) || _text[i] == '@')) i++;
        var name = _text[(start + 1)..i];
        var rest = _text.AsSpan(i, Math.Min(200, _text.Length - i)).ToString();

        if (name.Equals("notes", StringComparison.OrdinalIgnoreCase))
        {
            EndSlice();
            _inSlice = true;
            _staffIndex = 0;
            return i;
        }
        if (name is "en" or "enotes")
        {
            EndSlice();
            return i;
        }
        if (BarCommands.Contains(name))
        {
            EndSlice();
            foreach (var staff in _staves)
            {
                staff.Builder.Flush();
                staff.Accidentals.Clear();
            }
            return i;
        }

        switch (name)
        {
            case "generalmeter":
                return ReadMeter(rest, i);
            case "generalsignature":
                return ReadSignature(rest, i, null);
            case "setsign":
                return ReadSignature(rest, i, true);
            case "setclef":
                return ReadClef(rest, i, start);
            case "setname":
                if (NameArgument.Match(rest) is { Success: true } nameMatch)
                {
                    var index = int.Parse(nameMatch.Groups[1].Value) - 1;
                    _names[index] = nameMatch.Groups[2].Value.Trim();
                    if (index >= 0 && index < _staves.Count) _staves[index].Builder.Part.Name = _names[index];
                    return i + nameMatch.Length;
                }
                return i;
            case "fulltitle":
            case "title":
                if (ReadBraced(i) is var (title, afterTitle))
                {
                    _score.Title ??= Clean(title);
                    return afterTitle;
                }
                return i;
            case "author":
                if (ReadBraced(i) is var (author, afterAuthor))
                {
                    _score.Composer ??= Clean(author);
                    return afterAuthor;
                }
                return i;
        }

        if (!_inSlice) return i;

        if (Rests.TryGetValue(name, out var restValue))
        {
            AddRest(restValue, start);
            return i;
        }
        if (NoteCommand.Match(name) is { Success: true } note && IsNoteCommand(note))
            return ReadNote(note, i, start);

        if (!IgnoredCommands.Contains(name))
            _ctx.Warn($"MusiXTeX command \\{name} is not supported and was ignored");
        return i;
    }

    private static bool IsNoteCommand(Match match) =>
        match.Groups["z"].Length > 0 ||
        match.Groups["dir"].Length > 0 ||
        match.Groups["base"].Value == "wh";

    private static string Clean(string value) =>
        Regex.Replace(value, @"\\[A-Za-z]+|[{}]", string.Empty).Trim();

    private (string Content, int End)? ReadBraced(int from)
    {
        var i = from;
        while (i < _text.Length && char.IsWhiteSpace(_text[i])) i++;
        if (i >= _text.Length || _text[i] != '{') return null;
        var depth = 0;
        for (var j = i; j < _text.Length; j++)
        {
            if (_text[j] == '{') depth++;
            else if (_text[j] == '}' && --depth == 0) return (_text[(i + 1)..j], j + 1);
        }
        return null;
    }

    private int ReadMeter(string rest, int next)
    {
        int beats, unit;
        var mixed = MeterFraction.Match(rest);
        if (mixed.Success)
        {
            beats = int.Parse(mixed.Groups[1].Success ? mixed.Groups[1].Value : mixed.Groups[3].Value);
            unit = int.Parse(mixed.Groups[2].Success ? mixed.Groups[2].Value : mixed.Groups[4].Value);
        }
        else if (Regex.IsMatch(rest, @"^\s*\{?\s*\\meterC\b"))
        {
            (beats, unit) = (4, 4);
        }
        else if (Regex.IsMatch(rest, @"^\s*\{?\s*\\allabreve\b"))
        {
            (beats, unit) = (2, 2);
        }
        else
        {
            _ctx.Warn("Unsupported \\generalmeter argument was ignored");
            return next;
        }

        if (beats <= 0 || !DurationHelper.IsValidNoteValue(unit))
            throw _ctx.ParseError($"Invalid time signature {beats}/{unit}", LineAt(next));

        _beats = beats;
        _beatType = unit;
        foreach (var staff in _staves) staff.Builder.SetTime(beats, unit);
        return next;
    }

    private int ReadSignature(string rest, int next, bool? perStaff)
    {
        if (perStaff == true)
        {
            if (StaffIntegerArgument.Match(rest) is not { Success: true } m) return next;
            var index = int.Parse(m.Groups[1].Value) - 1;
            var fifths = ValidFifths(int.Parse(m.Groups[2].Value), next);
            var staff = GetStaff(index);
            staff.Fifths = fifths;
            staff.Builder.SetKey(fifths);
            return next + m.Length;
        }

        if (IntegerArgument.Match(rest) is not { Success: true } match) return next;
        var value = ValidFifths(int.Parse(match.Groups[1].Value), next);
        _fifths = value;
        foreach (var staff in _staves)
        {
            staff.Fifths = value;
            staff.Builder.SetKey(value);
        }
        return next + match.Length;
    }

    private int ValidFifths(int value, int position) =>
        value is >= -7 and <= 7 ? value : throw _ctx.ParseError($"Invalid key signature {value}", LineAt(position));

    private int ReadClef(string rest, int next, int start)
    {
        if (ClefArgument.Match(rest) is not { Success: true } m) return next;
        var index = int.Parse(m.Groups[1].Value) - 1;
        var (sign, line) = m.Groups[2].Value switch
        {
            "bass" => ("F", 4),
            "alto" => ("C", 3),
            "tenor" => ("C", 4),
            "soprano" => ("C", 1),
            "mezzo" or "mezzosoprano" => ("C", 2),
            "baritone" => ("C", 5),
            _ => ("G", 2)
        };
        if (_clefSign is null)
        {
            _clefSign = sign;
            _clefLine = line;
        }
        GetStaff(index).Builder.SetClef(sign, line);
        return next + m.Length;
    }

    private Staff GetStaff(int index)
    {
        if (index < 0) throw _ctx.ParseError("Staff numbers start at 1");
        while (_staves.Count <= index)
        {
            var number = _staves.Count;
            var builder = new PartBuilder(_names.TryGetValue(number, out var name) ? name : $"Staff {number + 1}")
            {
                AutoFlush = false
            };
            var staff = new Staff(builder);
            if (_beats is int beats && _beatType is int unit) builder.SetTime(beats, unit);
            if (_fifths is int fifths)
            {
                staff.Fifths = fifths;
                builder.SetKey(fifths);
            }
            builder.SetClef(_clefSign ?? "G", _clefSign is null ? 2 : _clefLine);
            _staves.Add(staff);
        }
        return _staves[index];
    }

    private void EndSlice()
    {
        if (!_inSlice) return;
        _inSlice = false;
        foreach (var staff in _staves)
        {
            if (staff.PendingChord.Count == 0) continue;
            _ctx.Warn("A zero-width note was not followed by a note to attach to and was dropped");
            staff.PendingChord.Clear();
        }
    }

    private void AddRest(int noteValue, int position)
    {
        var staff = GetStaff(_staffIndex);
        staff.PendingChord.Clear();
        staff.Builder.Add(new NeutralNote { Ticks = DurationHelper.TicksFor(noteValue, 0) });
    }

    private int ReadNote(Match note, int next, int start)
    {
        var baseText = note.Groups["base"].Value;
        var noteValue = baseText switch
        {
            "wh" or "w" => 1,
            "h" => 2,
            "q" => 4,
            _ => 4 << baseText.Length // c = 8, cc = 16, ccc = 32, cccc = 64
        };
        var dots = note.Groups["dots"].Length;
        var zeroWidth = note.Groups["z"].Length > 0;

        var (pitches, end) = ReadPitches(next, LineAt(start));
        if (pitches.Count == 0)
        {
            // e.g. \metron{\qu}{60}: a note glyph used as a symbol, not a note
            _ctx.Warn($"Note command '{_text[start..next]}' at line {LineAt(start)} has no pitch and was ignored");
            return next;
        }

        var staff = GetStaff(_staffIndex);
        if (zeroWidth)
        {
            staff.PendingChord.AddRange(pitches);
            return end;
        }

        pitches.InsertRange(0, staff.PendingChord);
        staff.PendingChord.Clear();
        staff.Builder.Add(new NeutralNote { Pitches = pitches, Ticks = DurationHelper.TicksFor(noteValue, dots) });
        return end;
    }

    private (List<NeutralPitch> Pitches, int End) ReadPitches(int from, int line)
    {
        var staff = GetStaff(_staffIndex);
        var i = from;
        while (i < _text.Length && char.IsWhiteSpace(_text[i])) i++;

        var pitches = new List<NeutralPitch>();
        if (i < _text.Length && _text[i] == '{')
        {
            var close = _text.IndexOf('}', i);
            if (close < 0) throw _ctx.ParseError("Unterminated pitch argument", line);
            var j = i + 1;
            while (j < close)
            {
                if (char.IsWhiteSpace(_text[j])) { j++; continue; }
                var pitch = ReadOnePitch(staff, ref j, close, line);
                if (pitch is null) { j++; continue; }
                pitches.Add(pitch);
            }
            return (pitches, close + 1);
        }

        var single = ReadOnePitch(staff, ref i, _text.Length, line);
        if (single != null) pitches.Add(single);
        return (pitches, i);
    }

    private NeutralPitch? ReadOnePitch(Staff staff, ref int i, int limit, int line)
    {
        var accidental = (int?)null;
        var j = i;
        while (j < limit && _text[j] is '^' or '_' or '=')
        {
            var delta = _text[j] switch { '^' => 1, '_' => -1, _ => 0 };
            accidental = _text[j] == '=' ? 0 : (accidental ?? 0) + delta;
            j++;
        }
        if (j >= limit) return null;

        var letter = _text[j];
        int index;
        if (letter is >= '0' and <= '9') index = letter - '0' - 10;
        else if (letter is >= 'A' and <= 'N') index = letter - 'A';
        else if (letter is >= 'a' and <= 'n') index = 14 + letter - 'a';
        else if (letter is >= 'o' and <= 'z') index = 28 + letter - 'o';
        else return null;

        i = j + 1;
        var step = FirstLetterStep + index;
        if (step < 0) throw _ctx.ParseError($"Pitch '{letter}' is out of range", line);
        var name = "CDEFGAB"[step % 7];
        var octave = step / 7;
        if (accidental is < -2 or > 2) throw _ctx.ParseError("Unsupported accidental", line);

        int alter;
        if (accidental is int explicitAlter)
        {
            alter = explicitAlter;
            staff.Accidentals[(name, octave)] = alter;
        }
        else if (staff.Accidentals.TryGetValue((name, octave), out var carried))
        {
            alter = carried;
        }
        else
        {
            alter = KeySignatureHelper.AlterFor(name, staff.Fifths);
        }
        return new NeutralPitch(name, alter, octave);
    }
}
