using System.Text.RegularExpressions;
using MusicConverter.Logging;
using MusicConverter.MusicXml;
using MusicConverter.Neutral;

namespace MusicConverter.Converters;

/// <summary>Imports ABC notation (first tune of a file; voices become parts).</summary>
public sealed class AbcConverter : MusicFormatConverterBase
{
    private static readonly string[] Extensions = { ".abc" };

    public AbcConverter() : this(null) { }

    public AbcConverter(ILogger? logger) : base(logger) { }

    public override string FormatName => "ABC";

    public override IReadOnlyCollection<string> FileExtensions => Extensions;

    protected override NeutralScore Parse(string text, ConversionContext context) =>
        new AbcParser(text, context).Parse();
}

internal sealed class AbcParser
{
    private static readonly Regex FieldLine = new(@"^[A-Za-z]:", RegexOptions.Compiled);
    private static readonly Regex KeyRegex =
        new(@"^\s*([A-G])([#b]?)\s*([A-Za-z]*)", RegexOptions.Compiled);

    private sealed class Voice
    {
        public Voice(PartBuilder builder) => Builder = builder;
        public PartBuilder Builder { get; }
        public Dictionary<(char, int), int> Accidentals { get; } = new();
        public bool EndingOpen { get; set; }
    }

    private readonly string[] _lines;
    private readonly ConversionContext _ctx;
    private readonly NeutralScore _score = new();
    private readonly Dictionary<string, Voice> _voices = new();
    private readonly Dictionary<string, string> _voiceNames = new();
    private readonly Dictionary<string, string> _voiceClefs = new();
    private readonly List<Voice> _voiceOrder = new();
    private readonly Dictionary<char, int> _keyAlters = new();

    private int _beats = 4, _beatType = 4, _fifths;
    private string _clefSign = "G";
    private int _clefLine = 2;
    private int _unitTicks;
    private bool _inBody, _seenTune;
    private Voice? _voice;
    private NeutralNote? _last;
    private int _tupletRemaining, _tupletActual = 1, _tupletNormal = 1;
    private int _brokenDirection, _brokenCount;
    private int _lineNo;
    private int _verse;
    private readonly List<NeutralNote> _lineNotes = new();

    public AbcParser(string text, ConversionContext ctx)
    {
        _lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        _ctx = ctx;
    }

    public NeutralScore Parse()
    {
        for (var i = 0; i < _lines.Length; i++)
        {
            _ctx.ThrowIfCancelled();
            _ctx.ReportParsing((i + 1) / (double)_lines.Length, "Parsing ABC");
            _lineNo = i + 1;

            var line = StripComment(_lines[i]).Trim();
            if (line.Length == 0)
            {
                if (_inBody && _seenTune) break;
                continue;
            }

            if (line.StartsWith("X:", StringComparison.Ordinal))
            {
                if (_seenTune)
                {
                    _ctx.Warn("Additional tunes in the file are ignored; only the first tune is converted.");
                    break;
                }
                _seenTune = true;
                continue;
            }

            if (FieldLine.IsMatch(line))
            {
                HandleField(line[0], line[2..].Trim());
                continue;
            }

            if (!_inBody) EnterBody();
            ParseMusicLine(line);
        }

        foreach (var voice in _voiceOrder)
        {
            voice.Builder.Flush();
            if (voice.EndingOpen) voice.Builder.MarkEndingStop();
            if (voice.Builder.MeasureCount > 0) _score.Parts.Add(voice.Builder.Part);
        }
        return _score;
    }

    private static string StripComment(string line)
    {
        var index = line.IndexOf('%');
        return index >= 0 ? line[..index] : line;
    }

    private void HandleTempo(string value)
    {
        var text = System.Text.RegularExpressions.Regex.Match(value, "\"([^\"]*)\"");
        if (text.Success && text.Groups[1].Value.Length > 0)
            _pendingDirs.Add(new NeutralDirection(DirectionKinds.Words, text.Groups[1].Value));

        var m = System.Text.RegularExpressions.Regex.Match(value, @"(?:(\d+)/(\d+)\s*)?=?\s*(\d+)\s*$");
        if (!m.Success || !double.TryParse(m.Groups[3].Value, out var bpm) || bpm <= 0) return;
        var unit = "quarter";
        if (m.Groups[1].Success)
        {
            var quarters = 4.0 * int.Parse(m.Groups[1].Value) / int.Parse(m.Groups[2].Value);
            switch (quarters)
            {
                case 4: unit = "whole"; break;
                case 2: unit = "half"; break;
                case 0.5: unit = "eighth"; break;
                case 0.25: unit = "16th"; break;
                case 1: break;
                default: bpm *= quarters; break;
            }
        }
        _pendingDirs.Add(new NeutralDirection(DirectionKinds.Tempo,
            Math.Round(bpm).ToString(System.Globalization.CultureInfo.InvariantCulture), unit));
    }

    private void HandleField(char field, string value)
    {
        switch (field)
        {
            case 'T':
                _score.Title ??= value;
                break;
            case 'C':
                _score.Composer ??= value;
                break;
            case 'M':
                SetMeter(value);
                break;
            case 'L':
                SetUnit(value);
                break;
            case 'K':
                SetKey(value);
                if (!_inBody) EnterBody();
                break;
            case 'V':
                HandleVoice(value);
                break;
            case 'Q':
                HandleTempo(value);
                break;
            case 'w':
                _verse++;
                LyricAligner.Apply(LyricAligner.Singable(_lineNotes), LyricAligner.Tokenize(value), _verse);
                break;
            case 'W':
                _ctx.Warn("Lyrics after the tune (W:) are not converted.");
                break;
        }
    }

    private void SetMeter(string value)
    {
        if (value.Equals("C", StringComparison.OrdinalIgnoreCase)) { ApplyMeter(4, 4); return; }
        if (value.Equals("C|", StringComparison.OrdinalIgnoreCase)) { ApplyMeter(2, 2); return; }
        if (value.Equals("none", StringComparison.OrdinalIgnoreCase)) return;

        var parts = value.Split('/');
        if (parts.Length == 2 && int.TryParse(parts[0].Trim(), out var beats) && int.TryParse(parts[1].Trim(), out var beatType)
            && beats > 0 && beatType > 0)
        {
            ApplyMeter(beats, beatType);
            return;
        }
        throw _ctx.ParseError($"Invalid meter 'M:{value}'", _lineNo);
    }

    private void ApplyMeter(int beats, int beatType)
    {
        _beats = beats;
        _beatType = beatType;
        _voice?.Builder.SetTime(beats, beatType);
    }

    private void SetUnit(string value)
    {
        var parts = value.Split('/');
        if (parts.Length == 2 && int.TryParse(parts[0].Trim(), out var num) && int.TryParse(parts[1].Trim(), out var den)
            && num > 0 && den > 0)
        {
            _unitTicks = DurationHelper.WholeTicks * num / den;
            return;
        }
        throw _ctx.ParseError($"Invalid unit note length 'L:{value}'", _lineNo);
    }

    private void SetKey(string value)
    {
        var clefMatch = Regex.Match(value, @"clef\s*=\s*(\w+)", RegexOptions.IgnoreCase);
        if (clefMatch.Success)
        {
            (_clefSign, _clefLine) = clefMatch.Groups[1].Value.ToLowerInvariant() switch
            {
                "bass" => ("F", 4),
                "alto" => ("C", 3),
                "tenor" => ("C", 4),
                _ => ("G", 2)
            };
            _voice?.Builder.SetClef(_clefSign, _clefLine);
        }

        var text = Regex.Replace(value, @"clef\s*=\s*\w+", string.Empty, RegexOptions.IgnoreCase).Trim();
        _keyAlters.Clear();

        // Trailing accidentals ("^f", "_b", "=c"), optionally after "exp", modify (or, with exp, replace) the key signature.
        var accidentalTokens = Regex.Matches(text, @"(?<![A-Za-z])(\^\^|__|\^|_|=)([A-Ga-g])(?![A-Za-z])");
        var explicitKey = Regex.IsMatch(text, @"\bexp\b", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\bexp\b", string.Empty, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"(\^\^|__|\^|_|=)[A-Ga-g]", string.Empty).Trim();

        if (text.Length == 0 || text.StartsWith("none", StringComparison.OrdinalIgnoreCase))
        {
            _fifths = 0;
        }
        else if (text.StartsWith("HP", StringComparison.Ordinal) || text.StartsWith("Hp", StringComparison.Ordinal))
        {
            // Highland pipes: F and C are sharp, G natural; HP shows no signature, Hp shows D major.
            _fifths = text.StartsWith("Hp", StringComparison.Ordinal) ? 2 : 0;
            _keyAlters['F'] = 1;
            _keyAlters['C'] = 1;
            _keyAlters['G'] = 0;
        }
        else
        {
            var m = KeyRegex.Match(text);
            if (!m.Success) throw _ctx.ParseError($"Invalid key 'K:{value}'", _lineNo);

            var alter = m.Groups[2].Value switch { "#" => 1, "b" => -1, _ => 0 };
            var mode = m.Groups[3].Value;
            var fifths = KeySignatureHelper.Fifths(m.Groups[1].Value[0], alter, mode);
            if (fifths is null) throw _ctx.ParseError($"Unknown key mode '{mode}' in 'K:{value}'", _lineNo);
            _fifths = fifths.Value;
        }

        if (explicitKey)
        {
            _fifths = 0;
            foreach (var step in "ABCDEFG") _keyAlters[step] = 0;
        }
        else if (accidentalTokens.Count > 0)
        {
            foreach (var step in "ABCDEFG")
                _keyAlters.TryAdd(step, KeySignatureHelper.AlterFor(step, _fifths));
        }

        foreach (Match t in accidentalTokens)
        {
            _keyAlters[char.ToUpperInvariant(t.Groups[2].Value[0])] = t.Groups[1].Value switch
            {
                "^^" => 2, "^" => 1, "__" => -2, "_" => -1, _ => 0
            };
        }

        if (_voice != null)
        {
            _voice.Builder.SetKey(_fifths);
            _voice.Accidentals.Clear();
        }
    }

    private void EnterBody()
    {
        _inBody = true;
        if (_unitTicks == 0)
        {
            _unitTicks = _beats / (double)_beatType < 0.75
                ? DurationHelper.WholeTicks / 16
                : DurationHelper.WholeTicks / 8;
        }
    }

    private void HandleVoice(string value)
    {
        var match = Regex.Match(value, "^(\\S+)(.*)$");
        if (!match.Success) return;
        var id = match.Groups[1].Value;
        var nameMatch = Regex.Match(match.Groups[2].Value, "name\\s*=\\s*\"([^\"]*)\"");
        if (nameMatch.Success) _voiceNames[id] = nameMatch.Groups[1].Value;
        var clefMatch = Regex.Match(match.Groups[2].Value, @"clef\s*=\s*(\w+)", RegexOptions.IgnoreCase);
        if (clefMatch.Success) _voiceClefs[id] = clefMatch.Groups[1].Value.ToLowerInvariant();
        if (_inBody) SwitchVoice(id);
        else _pendingVoice = id;
    }

    private string? _pendingVoice;

    private void SwitchVoice(string id)
    {
        if (!_voices.TryGetValue(id, out var voice))
        {
            var name = _voiceNames.TryGetValue(id, out var n) ? n : $"Voice {id}";
            var builder = new PartBuilder(name) { AutoFlush = false };
            builder.SetTime(_beats, _beatType);
            builder.SetKey(_fifths);
            var (sign, line) = _voiceClefs.TryGetValue(id, out var clef)
                ? clef switch { "bass" => ("F", 4), "alto" => ("C", 3), "tenor" => ("C", 4), _ => ("G", 2) }
                : (_clefSign, _clefLine);
            builder.SetClef(sign, line);
            voice = new Voice(builder);
            _voices[id] = voice;
            _voiceOrder.Add(voice);
        }
        _voice = voice;
        _last = null;
    }

    private Voice CurrentVoice()
    {
        if (_voice == null) SwitchVoice(_pendingVoice ?? "1");
        return _voice!;
    }

    private void Bar()
    {
        var voice = CurrentVoice();
        voice.Builder.Flush();
        voice.Accidentals.Clear();
        _last = null;
    }

    private void ParseMusicLine(string line)
    {
        _lineNotes.Clear();
        _verse = 0;
        var i = 0;
        while (i < line.Length)
        {
            _ctx.ThrowIfCancelled();
            var c = line[i];
            switch (c)
            {
                case ' ':
                case '\t':
                case '\\':
                case '&':
                case ')':
                case '`':
                    i++;
                    break;
                case '"':
                    SkipPast(line, ref i, '"');
                    _ctx.Warn("Chord symbols and annotations are ignored.");
                    break;
                case '!':
                {
                    var end = line.IndexOf('!', i + 1);
                    var name = end < 0 ? string.Empty : line[(i + 1)..end];
                    i = end < 0 ? line.Length : end + 1;
                    Decorate(name);
                    break;
                }
                case '+':
                {
                    var end = line.IndexOf('+', i + 1);
                    if (end < 0) { i++; break; }
                    Decorate(line[(i + 1)..end]);
                    i = end + 1;
                    break;
                }
                case '{':
                    _inGrace = true;
                    _graceSlash = i + 1 < line.Length && line[i + 1] == '/';
                    i += _graceSlash ? 2 : 1;
                    break;
                case '}':
                    _inGrace = false;
                    i++;
                    break;
                case '(':
                    ParseTupletOrSlur(line, ref i);
                    break;
                case '|':
                case ':':
                    ParseBar(line, ref i);
                    break;
                case ']':
                    ParseBar(line, ref i);
                    break;
                case '[':
                    ParseBracket(line, ref i);
                    break;
                case '-':
                    if (_last != null) _last.TieToNext = true;
                    i++;
                    break;
                case '>':
                case '<':
                    ParseBroken(line, ref i);
                    break;
                case 'z':
                case 'x':
                case 'Z':
                    ParseRest(line, ref i);
                    break;
                case '.': Decorate("staccato"); i++; break;
                case 'H': Decorate("fermata"); i++; break;
                case 'T': Decorate("trill"); i++; break;
                case 'M': Decorate("lowermordent"); i++; break;
                case 'P': Decorate("uppermordent"); i++; break;
                case 'S': Decorate("segno"); i++; break;
                case 'L': Decorate("accent"); i++; break;
                case '~': case 'O': case 'u': case 'v':
                    _ctx.Warn("Some decorations are ignored.");
                    i++;
                    break;
                default:
                    if (IsNoteStart(c))
                    {
                        ParseSingleNote(line, ref i);
                    }
                    else
                    {
                        _ctx.Warn($"Unsupported ABC character '{c}' ignored.");
                        i++;
                    }
                    break;
            }
        }
    }

    private static bool IsNoteStart(char c) => c is '^' or '_' or '=' or (>= 'A' and <= 'G') or (>= 'a' and <= 'g');

    private static void SkipPast(string line, ref int i, char terminator)
    {
        var end = line.IndexOf(terminator, i + 1);
        i = end < 0 ? line.Length : end + 1;
    }

    private void ParseTupletOrSlur(string line, ref int i)
    {
        i++;
        if (i >= line.Length || !char.IsDigit(line[i])) return;

        var p = ReadInt(line, ref i);
        // ABC 2.1 §4.13: for 5, 7 and 9 notes q is 3 in compound time (6/8, 9/8, 12/8) and 2 otherwise.
        var compound = _beatType == 8 && _beats % 3 == 0 && _beats > 3;
        var q = p switch { 2 => 3, 3 => 2, 4 => 3, 5 or 7 or 9 => compound ? 3 : 2, 6 => 2, 8 => 3, _ => 2 };
        var r = p;
        if (i < line.Length && line[i] == ':')
        {
            i++;
            if (i < line.Length && char.IsDigit(line[i])) q = ReadInt(line, ref i);
            if (i < line.Length && line[i] == ':')
            {
                i++;
                if (i < line.Length && char.IsDigit(line[i])) r = ReadInt(line, ref i);
            }
        }
        if (p < 2) throw _ctx.ParseError($"Invalid tuplet (p={p})", _lineNo);
        _tupletActual = p;
        _tupletNormal = q;
        _tupletRemaining = r;
    }

    private void ParseBar(string line, ref int i)
    {
        var begin = i;
        if (line[i] == '[') i++;
        var runStart = i;
        while (i < line.Length && (line[i] is '|' or ':' or ']')) i++;
        var run = line[runStart..i];
        Bar();

        var voice = CurrentVoice();
        var endsRepeat = run.StartsWith(':');
        var startsRepeat = run.EndsWith(':') && run.Length > 0;
        if (endsRepeat) voice.Builder.MarkRepeatEnd();

        // Volta numbers follow the bar: |1  :|2  |[1  [1,2
        var ending = ParseEndingNumbers(line, ref i, runStart > begin || run.Length > 0);
        var closesEnding = voice.EndingOpen && (endsRepeat || ending != null || run.Contains(']'));
        if (closesEnding)
        {
            voice.Builder.MarkEndingStop();
            voice.EndingOpen = false;
        }

        if (startsRepeat) voice.Builder.MarkRepeatStart();
        if (ending != null)
        {
            voice.Builder.MarkEndingStart(ending);
            voice.EndingOpen = true;
        }
    }

    /// <summary>Reads volta numbers such as "1", "1,2" or "1-3" and returns them as "1, 2, 3"; null when none follow.</summary>
    private static string? ParseEndingNumbers(string line, ref int i, bool afterBar)
    {
        var p = i;
        if (p < line.Length && line[p] == '[' && p + 1 < line.Length && char.IsDigit(line[p + 1])) p++;
        else if (!(afterBar && p < line.Length && char.IsDigit(line[p]))) return null;

        var numbers = new List<int>();
        while (p < line.Length && char.IsDigit(line[p]))
        {
            var number = ReadInt(line, ref p);
            if (p + 1 < line.Length && line[p] == '-' && char.IsDigit(line[p + 1]))
            {
                p++;
                var to = ReadInt(line, ref p);
                for (var n = number; n <= Math.Min(to, number + 20); n++) numbers.Add(n);
            }
            else numbers.Add(number);
            if (p + 1 < line.Length && line[p] == ',' && char.IsDigit(line[p + 1])) p++;
            else break;
        }
        i = p;
        return string.Join(", ", numbers.Distinct());
    }

    private void ParseBracket(string line, ref int i)
    {
        if (i + 2 < line.Length && char.IsLetter(line[i + 1]) && line[i + 2] == ':')
        {
            var end = line.IndexOf(']', i);
            var content = end < 0 ? line[(i + 1)..] : line[(i + 1)..end];
            i = end < 0 ? line.Length : end + 1;
            _ctx.Warn("Inline fields are only partially supported.");
            if (content.Length >= 2 && content[0] == 'K') SetKey(content[2..].Trim());
            else if (content.Length >= 2 && content[0] == 'M') SetMeter(content[2..].Trim());
            else if (content.Length >= 2 && content[0] == 'L') SetUnit(content[2..].Trim());
            return;
        }

        if (i + 1 < line.Length && char.IsDigit(line[i + 1]))
        {
            ParseBar(line, ref i);
            return;
        }

        if (i + 1 < line.Length && line[i + 1] == '"')
        {
            i += 2;
            return;
        }

        if (i + 1 < line.Length && line[i + 1] == '|')
        {
            ParseBar(line, ref i);
            return;
        }

        ParseChord(line, ref i);
    }

    private void ParseChord(string line, ref int i)
    {
        var voice = CurrentVoice();
        i++;
        var pitches = new List<NeutralPitch>();
        var innerTicks = 0;
        var tie = false;

        while (true)
        {
            if (i >= line.Length) throw _ctx.ParseError("Unterminated chord: missing ']'", _lineNo);
            var c = line[i];
            if (c == ']') { i++; break; }
            if (c == '-') { tie = true; i++; continue; }
            if (c is ' ' or '\t') { i++; continue; }
            if (!IsNoteStart(c)) throw _ctx.ParseError($"Unexpected '{c}' inside chord", _lineNo);

            pitches.Add(ReadPitch(line, ref i, voice));
            var ticks = ReadLength(line, ref i);
            if (innerTicks == 0) innerTicks = ticks;
        }

        if (pitches.Count == 0) throw _ctx.ParseError("Empty chord", _lineNo);

        var outer = ReadLengthFactor(line, ref i);
        var note = new NeutralNote { Pitches = pitches, Ticks = (int)Math.Round(innerTicks * outer), TieToNext = tie };
        AddNote(note, voice);
    }

    private void ParseSingleNote(string line, ref int i)
    {
        var voice = CurrentVoice();
        var pitch = ReadPitch(line, ref i, voice);
        var ticks = ReadLength(line, ref i);
        AddNote(new NeutralNote { Pitches = { pitch }, Ticks = ticks }, voice);
    }

    private void ParseRest(string line, ref int i)
    {
        var voice = CurrentVoice();
        var c = line[i++];
        var ticks = ReadLength(line, ref i);
        if (c == 'Z')
        {
            var bars = Math.Max(1, ticks / Math.Max(1, _unitTicks));
            var capacity = voice.Builder.CapacityTicks > 0
                ? voice.Builder.CapacityTicks
                : DurationHelper.TicksPerMeasure(_beats, _beatType);
            ticks = capacity;
            for (var b = 1; b < bars; b++)
            {
                AddNote(new NeutralNote { Ticks = ticks }, voice);
                voice.Builder.Flush();
            }
        }
        AddNote(new NeutralNote { Ticks = ticks }, voice);
    }

    private void ParseBroken(string line, ref int i)
    {
        var c = line[i];
        var count = 0;
        while (i < line.Length && line[i] == c) { count++; i++; }
        if (_last == null) throw _ctx.ParseError("Broken rhythm without a preceding note", _lineNo);

        var longer = (1 << (count + 1)) - 1;
        var divisor = 1 << count;
        _brokenDirection = c == '>' ? 1 : -1;
        _brokenCount = count;
        _last.Ticks = c == '>' ? _last.Ticks * longer / divisor : _last.Ticks / divisor;
    }

    private bool _inGrace, _graceSlash, _pendingFermata;
    private readonly List<string> _pendingArt = new();
    private readonly List<string> _pendingOrn = new();
    private readonly List<NeutralDirection> _pendingDirs = new();

    private void Decorate(string name)
    {
        switch (name.ToLowerInvariant())
        {
            case "staccato": case "wedge": _pendingArt.Add("staccato"); break;
            case "accent": case "emphasis": _pendingArt.Add("accent"); break;
            case "tenuto": _pendingArt.Add("tenuto"); break;
            case "marcato": _pendingArt.Add("strong-accent"); break;
            case "trill": case "trill(": case "trill)": _pendingOrn.Add("trill-mark"); break;
            case "turn": _pendingOrn.Add("turn"); break;
            case "lowermordent": case "mordent": _pendingOrn.Add("mordent"); break;
            case "uppermordent": case "pralltriller": _pendingOrn.Add("inverted-mordent"); break;
            case "fermata": case "invertedfermata": _pendingFermata = true; break;
            case "crescendo(": case "<(": _pendingDirs.Add(new NeutralDirection(DirectionKinds.Wedge, "crescendo")); break;
            case "diminuendo(": case ">(": _pendingDirs.Add(new NeutralDirection(DirectionKinds.Wedge, "diminuendo")); break;
            case "crescendo)": case "<)": case "diminuendo)": case ">)":
                _pendingDirs.Add(new NeutralDirection(DirectionKinds.Wedge, "stop")); break;
            case "ppp": case "pp": case "p": case "mp": case "mf": case "f": case "ff": case "fff": case "sfz":
                _pendingDirs.Add(new NeutralDirection(DirectionKinds.Dynamics, name.ToLowerInvariant())); break;
            default:
                if (_warnedDecorations.Add(name)) _ctx.Warn($"ABC decoration '{name}' is not converted.");
                break;
        }
    }

    private readonly HashSet<string> _warnedDecorations = new(StringComparer.OrdinalIgnoreCase);

    private void AddNote(NeutralNote note, Voice voice)
    {
        if (_inGrace)
        {
            note.IsGrace = true;
            note.GraceSlash = _graceSlash;
            note.Ticks = Math.Max(1, note.Ticks);
            voice.Builder.Add(note);
            return;
        }

        note.Articulations.AddRange(_pendingArt);
        note.Ornaments.AddRange(_pendingOrn);
        note.Directions.AddRange(_pendingDirs);
        if (_pendingFermata) note.Fermata = true;
        _pendingArt.Clear(); _pendingOrn.Clear(); _pendingDirs.Clear(); _pendingFermata = false;

        if (_brokenDirection != 0)
        {
            var longer = (1 << (_brokenCount + 1)) - 1;
            var divisor = 1 << _brokenCount;
            note.Ticks = _brokenDirection > 0 ? note.Ticks / divisor : note.Ticks * longer / divisor;
            _brokenDirection = 0;
        }

        if (_tupletRemaining > 0)
        {
            note.Ticks = (int)Math.Round(note.Ticks * (double)_tupletNormal / _tupletActual);
            note.TupletActual = _tupletActual;
            note.TupletNormal = _tupletNormal;
            _tupletRemaining--;
        }

        if (note.Ticks <= 0) throw _ctx.ParseError("Note length is too short to represent", _lineNo);
        voice.Builder.Add(note);
        _lineNotes.Add(note);
        _last = note;
    }

    private NeutralPitch ReadPitch(string line, ref int i, Voice voice)
    {
        int? explicitAlter = null;
        while (i < line.Length && line[i] is '^' or '_' or '=')
        {
            var a = line[i++];
            explicitAlter = a == '=' ? 0 : (explicitAlter ?? 0) + (a == '^' ? 1 : -1);
        }

        if (i >= line.Length || !(line[i] is >= 'A' and <= 'G' or >= 'a' and <= 'g'))
            throw _ctx.ParseError("Accidental not followed by a note letter", _lineNo);

        var letter = line[i++];
        var step = char.ToUpperInvariant(letter);
        var octave = char.IsLower(letter) ? 5 : 4;
        while (i < line.Length && line[i] is '\'' or ',')
        {
            octave += line[i] == '\'' ? 1 : -1;
            i++;
        }

        int alter;
        if (explicitAlter is int e)
        {
            alter = e;
            voice.Accidentals[(step, octave)] = e;
        }
        else if (!voice.Accidentals.TryGetValue((step, octave), out alter))
        {
            alter = _keyAlters.TryGetValue(step, out var keyAlter) ? keyAlter : KeySignatureHelper.AlterFor(step, _fifths);
        }

        return new NeutralPitch(step, alter, octave);
    }

    private int ReadLength(string line, ref int i) =>
        (int)Math.Round(_unitTicks * ReadLengthFactor(line, ref i));

    private double ReadLengthFactor(string line, ref int i)
    {
        double num = 1;
        if (i < line.Length && char.IsDigit(line[i])) num = ReadInt(line, ref i);

        double den = 1;
        if (i < line.Length && line[i] == '/')
        {
            var slashes = 0;
            while (i < line.Length && line[i] == '/') { slashes++; i++; }
            if (i < line.Length && char.IsDigit(line[i])) den = ReadInt(line, ref i);
            else den = Math.Pow(2, slashes);
            if (den == 0) throw _ctx.ParseError("Division by zero in note length", _lineNo);
        }
        return num / den;
    }

    private static int ReadInt(string line, ref int i)
    {
        var start = i;
        while (i < line.Length && char.IsDigit(line[i])) i++;
        return int.TryParse(line.AsSpan(start, i - start), out var v) ? v : int.MaxValue / 1024;
    }
}
