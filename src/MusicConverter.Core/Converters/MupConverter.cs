using MusicConverter.Abstractions;
using System.Text;
using System.Text.RegularExpressions;
using MusicConverter.Logging;
using MusicConverter.Neutral;

namespace MusicConverter.Converters;

/// <summary>Imports Arkkra Mup files (.mup).</summary>
public sealed class MupConverter : MusicFormatConverterBase
{
    private static readonly string[] Extensions = { ".mup" };

    public MupConverter() : this(null) { }

    public MupConverter(ILogger? logger) : base(logger) { }

    public override string FormatName => "MUP";

    public override IReadOnlyCollection<string> FileExtensions => Extensions;

    protected override NeutralScore Parse(string text, ConversionContext context) =>
        new MupParser(text, context).Parse();
}

internal sealed class MupParser
{
    private static readonly Regex ContextLine = new(
        @"^(?<kw>score|header|footer|header2|footer2|top|bottom|top2|bottom2|block|grids|headshapes|symbol|shapes|control|music)\b(?!\s*=)(?<rest>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex StaffContextLine = new(
        @"^(?<kw>staff|voice)\s+(?<num>\d+)\b(?<rest>.*)$", RegexOptions.Compiled);

    private static readonly Regex Param = new(
        @"\G(?<name>[A-Za-z_]\w*)\s*=\s*(?<value>""[^""]*""|[^\s;]+(?:[ \t]+(?:major|minor|dorian|phrygian|lydian|mixolydian|locrian|aeolian|ionian))?)",
        RegexOptions.Compiled);

    private static readonly Regex StaffPrefix = new(@"\G(\d+)(?:\s+(\d+))?\s*:", RegexOptions.Compiled);
    private static readonly Regex TimeValue = new(@"^(\d+)(\.*)", RegexOptions.Compiled);
    private static readonly Regex Quoted = new(@"""([^""]*)""", RegexOptions.Compiled);
    private static readonly Regex Word = new(@"\G[A-Za-z]+", RegexOptions.Compiled);

    private static readonly HashSet<string> BarWords = new(StringComparer.Ordinal)
    {
        "bar", "dblbar", "endbar", "repeatstart", "repeatend", "repeatboth", "restart", "invisbar"
    };

    private static readonly HashSet<string> StyleWords = new(StringComparer.Ordinal)
    {
        "grace", "cue", "xnote", "diam"
    };

    private static readonly HashSet<string> SkippedStatements = new(StringComparer.Ordinal)
    {
        "lyrics", "rom", "ital", "bold", "boldital", "print", "left", "right", "center", "mussym", "octave", "pedal",
        "midi", "roll", "newscore", "newpage", "multirest", "samescorebegin", "samescoreend", "samepagebegin",
        "samepageend", "rehearsal", "ending", "crescendo", "decrescendo", "phrase", "slur", "justify", "postscript",
        "printcmd", "mirrored", "define", "include", "if", "else", "endif", "ifdef", "ifndef", "undef", "eval"
    };

    private static readonly Dictionary<string, (string Sign, int Line, int Octave)> Clefs = new(StringComparer.Ordinal)
    {
        ["treble"] = ("G", 2, 4), ["soprano"] = ("C", 1, 4), ["mezzosoprano"] = ("C", 2, 4), ["alto"] = ("C", 3, 4),
        ["8bass"] = ("F", 4, 4), ["treble8"] = ("G", 2, 3), ["tenor"] = ("C", 4, 3), ["baritone"] = ("C", 5, 3),
        ["bass"] = ("F", 4, 3), ["frenchviolin"] = ("G", 1, 5), ["8treble"] = ("G", 2, 5), ["bass8"] = ("F", 4, 2),
        ["subbass"] = ("F", 5, 2)
    };

    private sealed class Segment
    {
        public Segment(string kind, int startLine, int? number)
        {
            Kind = kind;
            StartLine = startLine;
            Number = number;
        }

        public string Kind { get; }
        public int StartLine { get; }
        public int? Number { get; }
        public StringBuilder Text { get; } = new();
    }

    private sealed class Staff
    {
        public Staff(PartBuilder builder) => Builder = builder;
        public PartBuilder Builder { get; }
        public int Fifths;
        public int Capacity = DurationHelper.TicksPerMeasure(4, 4);
        public int DefaultOctave = 4;
        public bool OctaveIsExplicit;
        public List<(int Voice, int Verse, List<LyricToken> Tokens)> Lyrics { get; } = new();
        private readonly SortedDictionary<int, PartBuilder> _voices = new();
        private int _barStart;

        public void Add(NeutralNote note)
        {
            if (note.Voice <= 1)
            {
                Builder.Add(note);
                return;
            }
            if (!_voices.TryGetValue(note.Voice, out var voice))
            {
                voice = Builder.Fork();
                voice.AutoFlush = false;
                _voices[note.Voice] = voice;
            }
            voice.Add(note);
        }

        /// <summary>Closes the bar; notes of voices above 1 are laid over the measure written for voice 1.</summary>
        public void FlushBar()
        {
            Builder.Flush();
            foreach (var voice in _voices.Values)
            {
                voice.Flush();
                var notes = voice.Part.Measures.SelectMany(m => m.Notes).ToList();
                voice.Part.Measures.Clear();
                if (notes.Count == 0) continue;
                if (Builder.MeasureCount > _barStart)
                {
                    Builder.Part.Measures[^1].Notes.AddRange(notes);
                }
                else
                {
                    foreach (var note in notes) { note.Voice = 1; Builder.Add(note); }
                    Builder.Flush();
                }
            }
            _barStart = Builder.MeasureCount;
        }
    }

    private readonly string _text;
    private readonly ConversionContext _ctx;
    private readonly NeutralScore _score = new();
    private readonly List<Staff> _staves = new();

    private int? _beats, _beatType, _fifths;
    private string _clef = "treble";
    private int? _defaultOctave;
    private int _currentStaff = 1;
    private int _currentVoice = 1;
    private int _lastValue = 4;
    private int _lastDots;
    private bool _graceNext;
    private List<(Staff Staff, NeutralNote Note)>? _tuplet;
    private int _musicStartLine;
    private string _music = string.Empty;
    private bool _sawMusic;

    public MupParser(string text, ConversionContext ctx)
    {
        _text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        _ctx = ctx;
    }

    public NeutralScore Parse()
    {
        var segments = SplitContexts();
        var done = 0;
        foreach (var segment in segments)
        {
            _ctx.ThrowIfCancelled();
            var text = segment.Text.ToString();
            switch (segment.Kind)
            {
                case "score":
                case "staff":
                case "voice":
                    ApplyParams(text, segment.Kind == "score" ? null : segment.Number, segment.StartLine);
                    break;
                case "header":
                    ReadHeader(text);
                    break;
                case "music":
                    _sawMusic = true;
                    ReadMusic(text, segment.StartLine);
                    break;
            }
            _ctx.ReportParsing(++done / (double)segments.Count, "Parsing MUP");
        }

        if (!_sawMusic) throw _ctx.ParseError("No 'music' context found; not a MUP file");
        if (_tuplet != null)
        {
            if (!_ctx.Lenient) throw _ctx.ParseError("Unterminated tuplet: missing '}'");
            _ctx.Warn("Unterminated tuplet: missing '}'; notes kept as written.");
            _tuplet = null;
        }

        foreach (var staff in _staves)
        {
            staff.FlushBar();
            if (staff.Builder.MeasureCount == 0) continue;
            foreach (var (voice, verse, tokens) in staff.Lyrics)
            {
                var notes = LyricAligner.Singable(staff.Builder.Part.Measures.SelectMany(m => m.Notes).Where(n => n.Voice == voice));
                LyricAligner.Apply(notes, tokens, verse);
            }
            _score.Parts.Add(staff.Builder.Part);
        }
        return _score;
    }

    private List<Segment> SplitContexts()
    {
        var segments = new List<Segment>();
        var current = new Segment("", 1, null);
        segments.Add(current);

        var lines = _text.Split('\n');
        for (var n = 0; n < lines.Length; n++)
        {
            var raw = lines[n];
            var commentAt = IndexOfComment(raw);
            var line = (commentAt >= 0 ? raw[..commentAt] : raw).Trim();

            var match = ContextLine.Match(line);
            if (match.Success)
            {
                current = new Segment(match.Groups["kw"].Value, n + 1, null);
                segments.Add(current);
                line = match.Groups["rest"].Value.Trim();
            }
            else if (current.Kind != "music" && StaffContextLine.Match(line) is { Success: true } staffMatch)
            {
                current = new Segment(staffMatch.Groups["kw"].Value, n + 1, int.Parse(staffMatch.Groups["num"].Value));
                segments.Add(current);
                line = staffMatch.Groups["rest"].Value.Trim();
            }
            current.Text.Append(line).Append('\n');
        }
        return segments;
    }

    private static int IndexOfComment(string line)
    {
        var inQuote = false;
        for (var i = 0; i < line.Length - 1; i++)
        {
            if (line[i] == '"') inQuote = !inQuote;
            else if (!inQuote && line[i] == '/' && line[i + 1] == '/') return i;
        }
        return -1;
    }

    private void ReadHeader(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("title2", StringComparison.Ordinal))
            {
                var strings = Quoted.Matches(line).Select(m => m.Groups[1].Value.Trim()).Where(s => s.Length > 0).ToList();
                if (strings.Count > 0) _score.Composer ??= strings[^1];
            }
            else if (line.StartsWith("title", StringComparison.Ordinal))
            {
                var first = Quoted.Matches(line).Select(m => m.Groups[1].Value.Trim()).FirstOrDefault(s => s.Length > 0);
                if (first != null) _score.Title ??= first;
            }
        }
    }

    private void ApplyParams(string text, int? staffNumber, int line)
    {
        var rest = text.Trim();
        while (rest.Length > 0)
        {
            var match = Param.Match(rest);
            if (!match.Success)
            {
                var skip = rest.IndexOfAny(new[] { ' ', '\t', '\n', ';' });
                rest = skip < 0 ? string.Empty : rest[(skip + 1)..].TrimStart();
                continue;
            }
            ApplyParam(match.Groups["name"].Value, match.Groups["value"].Value.Trim(), staffNumber, line);
            rest = rest[match.Length..].TrimStart(' ', '\t', '\n', ';');
        }
    }

    private void ApplyParam(string name, string value, int? staffNumber, int line)
    {
        var targets = staffNumber is int n ? new[] { GetStaff(n) } : _staves.ToArray();

        switch (name)
        {
            case "staffs":
                if (!int.TryParse(value, out var count) || count < 1 || count > 40)
                    throw _ctx.ParseError($"Invalid staffs value '{value}'", line);
                GetStaff(count);
                break;
            case "time":
                var (beats, unit) = ParseTime(value, line);
                if (staffNumber is null) { _beats = beats; _beatType = unit; }
                foreach (var staff in targets) SetTime(staff, beats, unit);
                break;
            case "key":
                var fifths = ParseKey(value, line);
                if (staffNumber is null) _fifths = fifths;
                foreach (var staff in targets) SetKey(staff, fifths);
                break;
            case "clef":
                if (!Clefs.ContainsKey(value))
                    throw _ctx.ParseError($"Unknown clef '{value}'", line);
                if (staffNumber is null) _clef = value;
                foreach (var staff in targets) SetClef(staff, value);
                break;
            case "defoct":
                if (!int.TryParse(value, out var octave) || octave is < 0 or > 9)
                    throw _ctx.ParseError($"Invalid defoct '{value}'", line);
                if (staffNumber is null) _defaultOctave = octave;
                foreach (var staff in targets)
                {
                    staff.DefaultOctave = octave;
                    staff.OctaveIsExplicit = true;
                }
                break;
            case "label":
                if (staffNumber is int labelled) GetStaff(labelled).Builder.Part.Name = value.Trim('"');
                break;
        }
    }

    private (int Beats, int Unit) ParseTime(string value, int line)
    {
        if (value == "c") return (4, 4);
        if (value == "cut") return (2, 2);
        var parts = value.Split('/');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var beats) || !int.TryParse(parts[1], out var unit) ||
            beats <= 0 || !DurationHelper.IsValidNoteValue(unit))
            throw _ctx.ParseError($"Invalid time signature '{value}'", line);
        return (beats, unit);
    }

    private int ParseKey(string value, int line)
    {
        var sharpsOrFlats = Regex.Match(value, @"^(\d+)\s*([#&])$");
        if (sharpsOrFlats.Success)
        {
            var count = int.Parse(sharpsOrFlats.Groups[1].Value);
            if (count > 7) throw _ctx.ParseError($"Invalid key signature '{value}'", line);
            return sharpsOrFlats.Groups[2].Value == "#" ? count : -count;
        }

        var named = Regex.Match(value, @"^([a-gA-G])([#&]?)(?:\s+(\w+))?$");
        if (named.Success)
        {
            var alter = named.Groups[2].Value switch { "#" => 1, "&" => -1, _ => 0 };
            var fifths = KeySignatureHelper.Fifths(named.Groups[1].Value[0], alter, named.Groups[3].Value);
            if (fifths is int f && Math.Abs(f) <= 7) return f;
        }
        throw _ctx.ParseError($"Unsupported key signature '{value}'", line);
    }

    private void SetTime(Staff staff, int beats, int unit)
    {
        staff.Builder.SetTime(beats, unit);
        staff.Capacity = DurationHelper.TicksPerMeasure(beats, unit);
    }

    private void SetKey(Staff staff, int fifths)
    {
        staff.Fifths = fifths;
        staff.Builder.SetKey(fifths);
    }

    private void SetClef(Staff staff, string clef)
    {
        var (sign, line, octave) = Clefs[clef];
        staff.Builder.SetClef(sign, line);
        if (!staff.OctaveIsExplicit) staff.DefaultOctave = octave;
    }

    private Staff GetStaff(int number)
    {
        if (number < 1 || number > 40) throw _ctx.ParseError($"Invalid staff number {number}");
        while (_staves.Count < number)
        {
            var builder = new PartBuilder($"Staff {_staves.Count + 1}") { AutoFlush = false };
            var staff = new Staff(builder);
            if (_beats is int beats && _beatType is int unit) SetTime(staff, beats, unit);
            if (_fifths is int fifths) SetKey(staff, fifths);
            if (_defaultOctave is int octave)
            {
                staff.DefaultOctave = octave;
                staff.OctaveIsExplicit = true;
            }
            SetClef(staff, _clef);
            _staves.Add(staff);
        }
        return _staves[number - 1];
    }

    private int LineAt(int position) =>
        _musicStartLine + _music.AsSpan(0, Math.Min(position, _music.Length)).Count('\n');

    private void ReadMusic(string text, int startLine)
    {
        _music = text;
        _musicStartLine = startLine;
        var i = 0;

        while (true)
        {
            _ctx.ThrowIfCancelled();
            while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] == ';')) i++;
            if (i >= text.Length) break;

            var line = LineAt(i);
            var c = text[i];
            var statementStart = i;
            try
            {

            if (c == '{')
            {
                if (_tuplet != null) throw _ctx.ParseError("Nested tuplets are not supported", line);
                _tuplet = new List<(Staff, NeutralNote)>();
                i++;
                continue;
            }
            if (c == '}')
            {
                i = CloseTuplet(text, i, line);
                continue;
            }

            var word = Word.Match(text, i);
            if (word.Success)
            {
                var w = word.Value;
                var after = i + w.Length;
                var nextIsEquals = IsEqualsNext(text, after);

                if (!nextIsEquals && BarWords.Contains(w) && !IsWordChar(text, after))
                {
                    StartNewMeasure();
                    if (w is "repeatend" or "repeatboth") foreach (var s in _staves) s.Builder.MarkRepeatEnd();
                    if (w is "repeatstart" or "repeatboth") foreach (var s in _staves) s.Builder.MarkRepeatStart();
                    i = after;
                    continue;
                }
                if (nextIsEquals && Param.Match(text, i) is { Success: true } param)
                {
                    ApplyParam(param.Groups["name"].Value, param.Groups["value"].Value.Trim(), null, line);
                    i += param.Length;
                    continue;
                }
                if (!nextIsEquals && w == "lyrics")
                {
                    var lyricEnd = FindStatementEnd(text, i);
                    if (lyricEnd < 0) lyricEnd = text.Length;
                    ReadLyrics(text[(i + w.Length)..lyricEnd], line);
                    i = Math.Min(lyricEnd + 1, text.Length);
                    continue;
                }
                if (!nextIsEquals && SkippedStatements.Contains(w))
                {
                    _ctx.Warn($"MUP statement '{w}' is not supported and was ignored");
                    var eol = text.IndexOf('\n', i);
                    i = eol < 0 ? text.Length : eol;
                    continue;
                }
            }

            if (StaffPrefix.Match(text, i) is { Success: true } prefix)
            {
                _currentStaff = int.Parse(prefix.Groups[1].Value);
                _currentVoice = prefix.Groups[2].Success ? int.Parse(prefix.Groups[2].Value) : 1;
                GetStaff(_currentStaff);
                i += prefix.Length;
                continue;
            }

            var end = FindStatementEnd(text, i);
            if (end < 0) throw _ctx.ParseError("Missing ';' at end of chord", line);
            ReadChord(text[i..end].Trim(), line);
            i = end + 1;

            }
            catch (Exception ex) when (_ctx.Recover(ex))
            {
                var semi = text.IndexOf(';', statementStart);
                i = semi < 0 ? text.Length : Math.Max(semi + 1, statementStart + 1);
            }
        }
    }

    /// <summary>Reads <c>lyrics [above|below|between] staff[,voice]: "syl" "syl-" ...</c>; syllables are matched to notes in order.</summary>
    private void ReadLyrics(string text, int line)
    {
        var head = Regex.Match(text, @"^\s*(?:above|below|between)?\s*(\d+)\s*(?:,\s*(\d+))?\s*:(?<body>.*)$", RegexOptions.Singleline);
        if (!head.Success)
        {
            _ctx.Warn($"MUP lyrics statement on line {line} was not understood and was ignored");
            return;
        }

        var staff = GetStaff(int.Parse(head.Groups[1].Value));
        var voice = head.Groups[2].Success ? int.Parse(head.Groups[2].Value) : 1;
        var tokens = new List<LyricToken>();
        foreach (Match m in Regex.Matches(head.Groups["body"].Value, "\"((?:[^\"\\\\]|\\\\.)*)\""))
        {
            var s = m.Groups[1].Value.Replace("\\\"", "\"");
            if (s == "_") { tokens.Add(new LyricToken(null, false)); continue; }
            var hyphen = s.EndsWith('-') && s.Length > 1;
            s = s.TrimEnd('-').Replace('_', ' ').Trim();
            tokens.Add(s.Length == 0 ? new LyricToken(null, false) : new LyricToken(s, hyphen));
        }

        var verse = staff.Lyrics.Count(l => l.Voice == voice) + 1;
        staff.Lyrics.Add((voice, verse, tokens));
    }

    private static bool IsEqualsNext(string text, int from)
    {
        var i = from;
        while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
        return i < text.Length && text[i] == '=';
    }

    private static bool IsWordChar(string text, int index) =>
        index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '_');

    private static int FindStatementEnd(string text, int from)
    {
        var inQuote = false;
        for (var i = from; i < text.Length; i++)
        {
            if (text[i] == '"') inQuote = !inQuote;
            else if (!inQuote && text[i] == ';') return i;
        }
        return -1;
    }

    private void StartNewMeasure()
    {
        FlushTupletWarning();
        foreach (var staff in _staves) staff.FlushBar();
    }

    private void FlushTupletWarning()
    {
        if (_tuplet == null) return;
        if (!_ctx.Lenient) throw _ctx.ParseError("A bar line appeared inside a tuplet");
        _ctx.Warn("A bar line appeared inside a tuplet; the tuplet was dropped.");
        _tuplet = null;
    }

    private int CloseTuplet(string text, int index, int line)
    {
        if (_tuplet == null) throw _ctx.ParseError("Unexpected '}' without matching '{'", line);
        var end = FindStatementEnd(text, index);
        if (end < 0) throw _ctx.ParseError("Missing ';' after tuplet", line);

        var number = Regex.Match(text[(index + 1)..end], @"\d+");
        if (!number.Success || !int.TryParse(number.Value, out var actual) || actual < 2)
            throw _ctx.ParseError("Tuplet requires a number such as '} 3;'", line);

        var normal = 1;
        while (normal * 2 < actual) normal *= 2;

        foreach (var (staff, note) in _tuplet)
        {
            if (note.Ticks * normal % actual != 0)
            {
                _ctx.Warn("A tuplet duration cannot be represented exactly and was approximated");
            }
            note.TupletActual = actual;
            note.TupletNormal = normal;
            note.Ticks = (int)Math.Round(note.Ticks * (double)normal / actual);
            staff.Add(note);
        }
        _tuplet = null;
        return end + 1;
    }

    private void ReadChord(string text, int line)
    {
        text = Quoted.Replace(text, string.Empty);
        text = Regex.Replace(text, @"<<.*?>>", string.Empty).Trim();
        if (text.Length == 0) return;

        if (StyleWords.Contains(text))
        {
            if (text == "grace") _graceNext = true;
            return;
        }

        var value = _lastValue;
        var dots = _lastDots;
        var index = 0;
        if (TimeValue.Match(text) is { Success: true } time)
        {
            value = int.Parse(time.Groups[1].Value);
            dots = time.Groups[2].Length;
            index = time.Length;
            if (!DurationHelper.IsValidNoteValue(value) || dots > 3)
                throw _ctx.ParseError($"Unsupported time value '{time.Value}'", line);
            _lastValue = value;
            _lastDots = dots;
        }

        while (index < text.Length && char.IsWhiteSpace(text[index])) index++;

        var staff = GetStaff(_currentStaff);
        var ticks = DurationHelper.TicksFor(value, dots);
        var pitches = new List<NeutralPitch>();
        var rest = false;

        if (string.CompareOrdinal(text, index, "mr", 0, 2) == 0 || string.CompareOrdinal(text, index, "ms", 0, 2) == 0)
        {
            rest = true;
            ticks = staff.Capacity;
            index += 2;
        }
        else if (index < text.Length && text[index] is 'r' or 's')
        {
            rest = true;
            index++;
        }
        else
        {
            while (index < text.Length && text[index] is >= 'a' and <= 'g')
                pitches.Add(ReadPitch(text, ref index, staff, line));
            if (pitches.Count == 0) throw _ctx.ParseError($"Cannot understand '{text}'", line);
        }

        var tie = false;
        foreach (Match attribute in Regex.Matches(text[index..], @"[A-Za-z]+"))
        {
            if (attribute.Value == "tie") tie = true;
            else _ctx.Warn($"MUP chord attribute '{attribute.Value}' is not supported and was ignored");
        }

        if (_graceNext)
        {
            _graceNext = false;
            _ctx.Warn("Grace notes are not supported and were skipped");
            return;
        }
        var note = new NeutralNote
        {
            Pitches = rest ? new List<NeutralPitch>() : pitches, Ticks = ticks, TieToNext = tie && !rest, Voice = _currentVoice
        };
        if (_tuplet != null) _tuplet.Add((staff, note));
        else staff.Add(note);
    }

    private NeutralPitch ReadPitch(string text, ref int index, Staff staff, int line)
    {
        var step = char.ToUpperInvariant(text[index++]);
        int? alter = null;
        var octave = staff.DefaultOctave;

        while (index < text.Length)
        {
            var c = text[index];
            if (c == '#') alter = (alter ?? 0) + 1;
            else if (c == '&') alter = (alter ?? 0) - 1;
            else if (c == 'x') alter = (alter ?? 0) + 2;
            else if (c == 'n') alter = 0;
            else if (c == '+') octave++;
            else if (c == '-') octave--;
            else if (char.IsDigit(c)) octave = c - '0';
            else break;
            index++;
        }

        if (alter is < -2 or > 2) throw _ctx.ParseError("Unsupported accidental", line);
        if (octave is < 0 or > 9) throw _ctx.ParseError($"Octave {octave} is out of range", line);
        return new NeutralPitch(step, alter ?? KeySignatureHelper.AlterFor(step, staff.Fifths), octave);
    }
}
