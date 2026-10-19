using MusicConverter.Abstractions;
using MusicConverter.Logging;
using MusicConverter.MusicXml;
using MusicConverter.Neutral;

namespace MusicConverter.Converters;

/// <summary>
/// Imports LilyPond sources: header title/composer, \score, \new Staff/Voice, \relative, \time, \key,
/// \clef, \partial, \repeat volta, tuplets, ties, chords, rests and simple variables.
/// </summary>
public sealed class LilyPondConverter : MusicFormatConverterBase
{
    private static readonly string[] Extensions = { ".ly", ".ily" };

    public LilyPondConverter() : this(null) { }

    public LilyPondConverter(ILogger? logger) : base(logger) { }

    public override string FormatName => "LilyPond";

    public override IReadOnlyCollection<string> FileExtensions => Extensions;

    protected override NeutralScore Parse(string text, ConversionContext context) =>
        new LilyPondParser(ExpandIncludes(text, context), context).Parse();

    private static readonly System.Text.RegularExpressions.Regex IncludeRegex =
        new(@"^[ \t]*\\include[ \t]*""(?<file>[^""]+)""[ \t]*$", System.Text.RegularExpressions.RegexOptions.Multiline);

    /// <summary>Inlines <c>\include "file"</c> directives, resolving paths relative to the including file.</summary>
    private static string ExpandIncludes(string text, ConversionContext context)
    {
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(context.FilePath)) ?? string.Empty;
        var stack = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(context.FilePath) };
        return Expand(text, baseDir, stack, context, 0);
    }

    private static string Expand(string text, string dir, HashSet<string> stack, ConversionContext context, int depth)
    {
        return IncludeRegex.Replace(text, match =>
        {
            context.ThrowIfCancelled();
            var name = match.Groups["file"].Value;
            var path = Path.GetFullPath(Path.Combine(dir, name));
            if (depth >= 16 || !stack.Add(path))
            {
                context.Warn($"Circular or too deeply nested \\include \"{name}\" skipped.");
                return string.Empty;
            }
            try
            {
                if (!File.Exists(path) && string.Equals(name, "english.ly", StringComparison.OrdinalIgnoreCase))
                {
                    // Built into LilyPond, so it is normally not next to the score.
                    return "\\language \"english\" ";
                }
                if (!File.Exists(path))
                {
                    context.Warn($"Included file '{name}' was not found and was skipped.");
                    return string.Empty;
                }
                var included = File.ReadAllText(path);
                return Expand(included, Path.GetDirectoryName(path) ?? dir, stack, context, depth + 1);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                context.Warn($"Included file '{name}' could not be read: {ex.Message}");
                return string.Empty;
            }
            finally
            {
                stack.Remove(path);
            }
        });
    }
}

internal sealed class LilyPondParser
{
    private sealed class StaffState
    {
        public PartBuilder Builder = null!;
        public bool Relative;
        public int PrevStep;
        public int PrevOctave = 3;
        public int LastBase = 4;
        public int LastDots;
        public int TupActual = 1;
        public int TupNormal = 1;
        public NeutralNote? Last;
        public List<NeutralPitch>? LastChord;
        public List<NeutralDirection> PendingDirections = new();
        public bool InGrace, GraceSlash;

        /// <summary>Every note emitted by this voice, in order; used to align lyrics.</summary>
        public List<NeutralNote> Log = new();
        public int VoiceNumber = 1;

        public StaffState Fork(int voiceNumber) => new()
        {
            Builder = Builder.Fork(),
            Relative = Relative,
            PrevStep = PrevStep,
            PrevOctave = PrevOctave,
            LastBase = LastBase,
            LastDots = LastDots,
            TupActual = TupActual,
            TupNormal = TupNormal,
            VoiceNumber = voiceNumber
        };
        public StaffState Discarded() => new()
        {
            Builder = new PartBuilder("discarded"),
            Relative = Relative,
            PrevStep = PrevStep,
            PrevOctave = PrevOctave,
            LastBase = LastBase,
            LastDots = LastDots,
            TupActual = TupActual,
            TupNormal = TupNormal
        };
    }

    private static readonly HashSet<string> SilentCommands = new(StringComparer.Ordinal)
    {
        "p", "pp", "ppp", "f", "ff", "fff", "mp", "mf", "sf", "sfz", "fp", "cresc", "decresc", "dim", "break",
        "pageBreak", "noBreak", "noPageBreak", "stemUp", "stemDown", "stemNeutral", "autoBeamOn", "autoBeamOff",
        "slurUp", "slurDown", "slurNeutral", "tieUp", "tieDown", "tieNeutral", "voiceOne", "voiceTwo",
        "voiceThree", "voiceFour", "oneVoice", "fermata", "staccato", "tenuto", "accent", "marcato", "trill",
        "turn", "startTrillSpan", "stopTrillSpan", "glissando", "arpeggio", "sustainOn", "sustainOff",
        "startGroup", "stopGroup", "dynamicUp", "dynamicDown", "dynamicNeutral", "numericTimeSignature",
        "defaultTimeSignature", "once", "autoBeamOff", "bendAfter", "tiny", "small", "normalsize", "teeny",
        "stopStaff", "startStaff", "cadenzaOn", "cadenzaOff", "hideNotes", "unHideNotes", "easyHeadsOn",
        "easyHeadsOff", "accidentalStyle", "omit", "default"
    };

    private static readonly HashSet<string> StaffTypes = new(StringComparer.Ordinal)
    {
        "Staff", "Voice", "DrumStaff", "RhythmicStaff", "TabStaff", "NullVoice", "GregorianTranscriptionStaff"
    };

    private static readonly HashSet<string> GroupTypes = new(StringComparer.Ordinal)
    {
        "PianoStaff", "StaffGroup", "ChoirStaff", "GrandStaff", "Score"
    };

    private readonly string _root;
    private string _s;
    private int _pos;
    private readonly ConversionContext _ctx;
    private readonly NeutralScore _score = new();
    private readonly Dictionary<string, string> _vars = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> _functions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _schemeVars = new(StringComparer.Ordinal);
    private int _staffCounter;
    private int _variableDepth;
    private int _scoreBlocks;
    private int _bookBlocks;
    private int? _blockPickup;
    private int? _scorePickup; // \partial belongs to the score's timing, so every staff starts with the same anacrusis
    private bool _english;
    private List<NeutralNote>? _lastLog;
    private readonly Dictionary<string, List<NeutralNote>> _voiceLogs = new(StringComparer.Ordinal);
    private readonly List<(string Voice, string Text)> _pendingLyrics = new();
    private readonly Dictionary<object, int> _verseCounts = new(ReferenceEqualityComparer.Instance);

    public LilyPondParser(string text, ConversionContext ctx)
    {
        _root = _s = text;
        _ctx = ctx;
    }

    public NeutralScore Parse()
    {
        while (true)
        {
            SkipWs();
            if (Eof) break;
            Tick();

            var c = Peek();
            if (c == '\\')
            {
                var save = _pos;
                _pos++;
                var word = ReadWord();
                switch (word)
                {
                    case "version":
                        SkipWs();
                        if (Peek() == '"') SkipString();
                        break;
                    case "language":
                        SkipWs();
                        if (Peek() == '"') SetLanguage(ReadString());
                        break;
                    case "include":
                        _ctx.Warn("\\include files are not resolved.");
                        SkipWs();
                        if (Peek() == '"') SkipString();
                        break;
                    case "header":
                        ParseHeader();
                        break;
                    case "score":
                        ParseScoreBlock();
                        break;
                    case "book":
                        _bookBlocks++;
                        if (_bookBlocks > 1 && _score.Parts.Count > 0)
                        {
                            _ctx.Warn("An additional \\book block (usually a hymnal or melody-only copy of the same music) was skipped; only the first \\book is converted.");
                            SkipBlockArgument();
                            break;
                        }
                        goto case "bookpart";
                    case "bookpart":
                        SkipWs();
                        if (Peek() == '{') _pos++;
                        break;
                    case "paper":
                    case "layout":
                    case "midi":
                        SkipBlockArgument();
                        break;
                    default:
                        _pos = save;
                        ParseTopMusic();
                        break;
                }
            }
            else if (c == '}')
            {
                _pos++;
            }
            else if (char.IsLetter(c))
            {
                if (!TryAssignment())
                {
                    _ctx.Warn($"Unexpected top-level text near '{Snippet()}' ignored.");
                    SkipToken();
                }
            }
            else if (c == '{' || StartsWith("<<"))
            {
                ParseTopMusic();
            }
            else
            {
                _pos++;
            }
        }

        ResolveLyrics();
        return _score;
    }

    #region Cursor helpers

    private bool Eof => _pos >= _s.Length;

    private char Peek(int offset = 0) => _pos + offset < _s.Length ? _s[_pos + offset] : '\0';

    private bool StartsWith(string value) => string.CompareOrdinal(_s, _pos, value, 0, value.Length) == 0;

    private string Snippet() => _s.Substring(_pos, Math.Min(20, _s.Length - _pos)).Replace('\n', ' ');

    private int CurrentLine()
    {
        var line = 1;
        for (var i = 0; i < _pos && i < _s.Length; i++)
            if (_s[i] == '\n') line++;
        return line;
    }

    private ConversionException Fail(string message) => _ctx.ParseError(message, CurrentLine());

    private void Tick()
    {
        _ctx.ThrowIfCancelled();
        if (ReferenceEquals(_s, _root) && _root.Length > 0)
            _ctx.ReportParsing(Math.Min(1.0, _pos / (double)_root.Length), "Parsing LilyPond");
    }

    private void SkipWs()
    {
        while (!Eof)
        {
            var c = _s[_pos];
            if (char.IsWhiteSpace(c))
            {
                _pos++;
            }
            else if (c == '%')
            {
                if (Peek(1) == '{')
                {
                    var end = _s.IndexOf("%}", _pos + 2, StringComparison.Ordinal);
                    _pos = end < 0 ? _s.Length : end + 2;
                }
                else
                {
                    while (!Eof && _s[_pos] != '\n') _pos++;
                }
            }
            else
            {
                break;
            }
        }
    }

    private string ReadWord()
    {
        var start = _pos;
        while (!Eof && char.IsLetter(_s[_pos])) _pos++;
        return _s[start.._pos];
    }

    private int ReadInt()
    {
        var start = _pos;
        while (!Eof && char.IsDigit(_s[_pos])) _pos++;
        if (start == _pos) throw Fail("Expected a number");
        return int.TryParse(_s.AsSpan(start, _pos - start), out var v) ? v : throw Fail("Number is too large");
    }

    private void SkipString()
    {
        _pos++;
        while (!Eof && _s[_pos] != '"')
        {
            if (_s[_pos] == '\\') _pos++;
            _pos++;
        }
        if (Eof) throw Fail("Unterminated string");
        _pos++;
    }

    private string ReadString()
    {
        var start = _pos + 1;
        SkipString();
        return _s[start..(_pos - 1)];
    }

    private void SkipToken()
    {
        var start = _pos;
        while (!Eof && !char.IsWhiteSpace(_s[_pos]) && _s[_pos] is not ('{' or '}' or '<' or '>'))
            _pos++;
        if (_pos == start) _pos++;
    }

    private void SkipToEndOfLine()
    {
        while (!Eof && _s[_pos] != '\n') _pos++;
    }

    /// <summary>Skips a balanced {...} or &lt;&lt;...&gt;&gt; block starting at the cursor.</summary>
    private void SkipBalanced()
    {
        var depth = 0;
        var startLine = CurrentLine();
        do
        {
            if (Eof) throw _ctx.ParseError("Unbalanced braces: block opened here is never closed", startLine);
            var c = _s[_pos];
            if (c == '"') { SkipString(); continue; }
            if (c == '%')
            {
                SkipWs();
                continue;
            }
            if (c == '{') { depth++; _pos++; }
            else if (c == '<' && Peek(1) == '<') { depth++; _pos += 2; }
            else if (c == '}') { depth--; _pos++; }
            else if (c == '>' && Peek(1) == '>') { depth--; _pos += 2; }
            else _pos++;
        }
        while (depth > 0);
    }

    /// <summary>Skips an argument that is (eventually) a block, string or single token.</summary>
    private void SkipBlockArgument()
    {
        for (var i = 0; i < 8; i++)
        {
            SkipWs();
            if (Eof) return;
            var c = Peek();
            if (c == '{' || StartsWith("<<")) { SkipBalanced(); return; }
            if (c == '"') { SkipString(); return; }
            if (c == '\\') { _pos++; ReadWord(); continue; }
            if (c == '}') return;
            SkipToken();
        }
    }

    #endregion

    #region Top level

    private bool TryAssignment()
    {
        var save = _pos;
        var name = ReadWord();
        SkipWs();
        if (name.Length == 0 || Peek() != '=')
        {
            _pos = save;
            return false;
        }

        _pos++;
        SkipWs();
        var start = _pos;
        var c = Peek();
        if (c == '"')
        {
            SkipString();
        }
        else if (c == '{' || StartsWith("<<"))
        {
            SkipBalanced();
            _vars[name] = _s[start.._pos];
        }
        else if (c == '#')
        {
            SkipScheme();
            var scheme = _s[start.._pos];
            var m = System.Text.RegularExpressions.Regex.Match(scheme,
                @"^#\(define-music-function\s*\([^)]*\)\s*\((?<args>[^)]*)\)");
            if (m.Success) _functions[name] = m.Groups["args"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            else _schemeVars.Add(name);
        }
        else if (c == '\\')
        {
            SkipBlockArgument();
            _vars[name] = _s[start.._pos];
        }
        else
        {
            SkipToken();
        }
        return true;
    }

    private void ParseHeader()
    {
        SkipWs();
        if (Peek() != '{') throw Fail("Expected '{' after \\header");
        _pos++;
        while (true)
        {
            SkipWs();
            if (Eof) throw Fail("Unterminated \\header block");
            if (Peek() == '}') { _pos++; return; }

            var key = ReadWord();
            SkipWs();
            if (key.Length == 0 || Peek() != '=')
            {
                SkipToken();
                continue;
            }

            _pos++;
            SkipWs();
            if (Peek() == '"')
            {
                var value = ReadString();
                if (key == "title") _score.Title ??= value;
                else if (key == "composer") _score.Composer ??= value;
            }
            else
            {
                SkipBlockArgument();
            }
        }
    }

    private void ParseScoreBlock()
    {
        SkipWs();
        if (Peek() != '{') throw Fail("Expected '{' after \\score");
        var blockStart = _pos;
        SkipBlockArgument();
        var block = _s.Substring(blockStart, _pos - blockStart);
        _pos = blockStart;

        // A MIDI-only score repeats the music of an earlier engraved score; converting it would duplicate parts.
        if (_scoreBlocks > 0 && block.Contains("\\midi") && !block.Contains("\\layout"))
        {
            _pos = blockStart + block.Length;
            _ctx.Warn("A MIDI-only \\score block duplicates earlier music and was skipped.");
            return;
        }

        _scoreBlocks++;
        _scorePickup = null;
        _pos++;
        while (true)
        {
            SkipWs();
            if (Eof) throw Fail("Unterminated \\score block");
            Tick();
            if (Peek() == '}') { _pos++; ResolveLyrics(); return; }

            if (Peek() == '\\')
            {
                var save = _pos;
                _pos++;
                var word = ReadWord();
                if (word == "header") { ParseHeader(); continue; }
                if (word is "layout" or "midi" or "paper") { SkipBlockArgument(); continue; }
                _pos = save;
            }
            ParseTopMusic();
        }
    }

    private void ParseTopMusic()
    {
        SkipWs();
        if (StartsWith("<<"))
        {
            _pos += 2;
            while (true)
            {
                SkipWs();
                if (Eof) throw Fail("Unterminated '<<': missing '>>'");
                if (StartsWith(">>")) { _pos += 2; return; }
                if (StartsWith("\\\\")) { _pos += 2; continue; }
                Tick();
                ParseTopMusic();
            }
        }

        if (Peek() == '\\')
        {
            var save = _pos;
            _pos++;
            var word = ReadWord();
            if (word is "new" or "context")
            {
                SkipWs();
                var type = ReadWord();
                var name = ReadContextHeader();
                if (type == "Lyrics")
                {
                    ParseLyricsContext();
                }
                else if (GroupTypes.Contains(type))
                {
                    ParseTopMusic();
                }
                else if (StaffTypes.Contains(type))
                {
                    var staff = NewStaff(name);
                    if (name != null) _voiceLogs[name] = staff.Log;
                    ParseItem(staff);
                    FinishStaff(staff);
                }
                else
                {
                    _ctx.Warn($"Context '{type}' is not converted.");
                    SkipContextBody();
                }
                return;
            }
            if (word == "addlyrics")
            {
                AddLyricsToLast();
                return;
            }
            _pos = save;
        }

        var bare = NewStaff(null);
        ParseItem(bare);
        FinishStaff(bare);
    }

    /// <summary>Skips exactly one music expression: a block, string, or a variable reference (after any mode switch such as \lyricmode).</summary>
    private void SkipContextBody()
    {
        while (true)
        {
            SkipWs();
            if (Eof) return;
            var c = Peek();
            if (c == '{' || StartsWith("<<")) { SkipBalanced(); return; }
            if (c == '"') { SkipString(); return; }
            if (c != '\\') { SkipToken(); return; }
            _pos++;
            var word = ReadWord();
            if (word is not ("lyricmode" or "lyrics" or "chordmode" or "drummode" or "notemode" or "figuremode" or "with" or "new" or "context" or "addlyrics"))
                return;
        }
    }

    private string? ReadContextHeader()
    {
        string? name = null;
        SkipWs();
        if (Peek() == '=')
        {
            _pos++;
            SkipWs();
            if (Peek() == '"') name = ReadString();
            else name = ReadWord();
        }
        SkipWs();
        if (StartsWith("\\with"))
        {
            _pos += 5;
            SkipWs();
            if (Peek() == '{') SkipBalanced();
        }
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private StaffState NewStaff(string? name)
    {
        var builder = new PartBuilder(name ?? $"Part {++_staffCounter}");
        builder.SetTime(4, 4);
        builder.SetKey(0);
        builder.SetClef("G", 2);
        if (_scorePickup is { } pickup) builder.SetPickup(pickup);
        return new StaffState { Builder = builder };
    }

    private void FinishStaff(StaffState staff)
    {
        staff.Builder.Flush();
        _lastLog = staff.Log;
        if (staff.Builder.MeasureCount > 0) _score.Parts.Add(staff.Builder.Part);
    }

    #endregion

    #region Music

    private void ParseItem(StaffState st)
    {
        SkipWs();
        if (Eof) return;
        var c = Peek();
        switch (c)
        {
            case '{':
                ParseSequence(st);
                break;
            case '<':
                if (Peek(1) == '<') ParseSimultaneous(st);
                else ParseChord(st);
                break;
            case '\\':
                ParseCommand(st);
                break;
            case 'r':
            case 's':
                ParseRest(st);
                break;
            case 'R':
                ParseMultiMeasureRest(st);
                break;
            case 'q' when !char.IsLetter(Peek(1)):
                ParseRepeatedChord(st);
                break;
            case '~':
                if (st.Last != null) st.Last.TieToNext = true;
                _pos++;
                break;
            case '-':
                _pos++;
                if (Peek() is '.' or '>' or '^' or '_' or '!' or '+' or '-')
                {
                    AddMark(st, Peek() switch
                    {
                        '.' => "staccato", '>' => "accent", '-' => "tenuto", '^' => "strong-accent",
                        '!' => "staccatissimo", '_' => "detached-legato", _ => null
                    });
                    _pos++;
                }
                else if (char.IsDigit(Peek())) _pos++;
                break;
            case '^':
            case '_':
                _pos++;
                if (Peek() == '"') SkipString();
                break;
            case '"':
                SkipString();
                break;
            case '#':
                SkipScheme();
                break;
            case '(':
                if (st.Last != null) st.Last.SlurStart = true;
                _pos++;
                break;
            case ')':
                if (st.Last != null) st.Last.SlurStop = true;
                _pos++;
                break;
            case '|': case '[': case ']': case '!': case '?': case '.': case ',': case '\'':
                _pos++;
                break;
            default:
                if (c is >= 'a' and <= 'g')
                {
                    ParseNote(st);
                }
                else
                {
                    _ctx.Warn($"Unsupported LilyPond text '{c}' ignored.");
                    _pos++;
                }
                break;
        }
    }

    /// <summary>Skips a Scheme value starting at '#': a parenthesised form (strings, comments and #{ #} aware), a quoted datum or a token.</summary>
    private void SkipScheme()
    {
        while (Peek() == '#' && Peek(1) == '#') _pos++;
        _pos++; // '#'
        if (Peek() == '\'' || Peek() == '`' || Peek() == ',') _pos++;
        if (Peek() == '"') { SkipString(); return; }
        if (Peek() == '\\') { _pos += 2; SkipToken(); return; }
        if (Peek() != '(')
        {
            var start = _pos;
            while (!Eof && !char.IsWhiteSpace(_s[_pos]) && _s[_pos] is not ('{' or '}' or '<' or '>' or ')')) _pos++;
            if (_pos == start && !Eof) _pos++;
            return;
        }

        var depth = 0;
        do
        {
            if (Eof) throw Fail("Unterminated Scheme expression");
            var c = _s[_pos];
            if (c == '"') { SkipString(); continue; }
            if (c == ';') { SkipToEndOfLine(); continue; }
            if (c == '#' && Peek(1) == '\\') { _pos += 3; continue; }
            if (c == '#' && Peek(1) == '{')
            {
                var end = _s.IndexOf("#}", _pos + 2, StringComparison.Ordinal);
                if (end < 0) throw Fail("Unterminated #{ ... #} block");
                _pos = end + 2;
                continue;
            }
            if (c == '(') depth++;
            else if (c == ')') depth--;
            _pos++;
        }
        while (depth > 0);
    }

    private void ParseSequence(StaffState st)
    {
        var startLine = CurrentLine();
        _pos++;
        while (true)
        {
            SkipWs();
            if (Eof) throw _ctx.ParseError("Unbalanced braces: '{' is never closed", startLine);
            if (Peek() == '}') { _pos++; return; }
            Tick();
            ParseItem(st);
        }
    }

    private bool IsLyricsElement()
    {
        if (Peek() != '\\') return false;
        var save = _pos;
        _pos++;
        var word = ReadWord();
        var result = word == "addlyrics";
        if (!result && (word == "new" || word == "context"))
        {
            SkipWs();
            result = ReadWord() == "Lyrics";
        }
        _pos = save;
        return result;
    }

    /// <summary>
    /// Parses <c>&lt;&lt; ... &gt;&gt;</c>. Every music element becomes its own voice (MusicXML voice 1, 2, ...) laid over the
    /// same measures; items that emit no notes (\clef, \key, \global, ...) are applied to the staff.
    /// </summary>
    private void ParseSimultaneous(StaffState st)
    {
        if (st.Builder.PendingTicks != 0)
        {
            ParseSimultaneousInline(st);
            return;
        }

        _pos += 2;
        var offset = st.Builder.MeasureCount;
        var start = (st.Relative, st.PrevStep, st.PrevOctave, st.LastBase, st.LastDots);
        var savedPickup = _blockPickup;
        _blockPickup = st.Builder.PickupTicks;
        var voices = new List<StaffState>();
        try
        {
            while (true)
            {
                SkipWs();
                if (Eof) throw Fail("Unterminated '<<': missing '>>'");
                if (StartsWith(">>")) { _pos += 2; break; }
                if (StartsWith("\\\\")) { _pos += 2; continue; }
                Tick();

                if (IsLyricsElement())
                {
                    ParseItem(st);
                    continue;
                }

                var voice = (voices.Count > 0 ? voices[0] : st).Fork(voices.Count + 1);
                (voice.Relative, voice.PrevStep, voice.PrevOctave, voice.LastBase, voice.LastDots) = start;
                if (_blockPickup is { } pickup) voice.Builder.SetPickup(pickup);
                var itemStart = _pos;
                ParseItem(voice);
                voice.Builder.Flush();
                if (voice.Log.Count == 0)
                {
                    _pos = itemStart;
                    ParseItem(st);
                    continue;
                }

                _lastLog = voice.Log;
                voices.Add(voice);
            }
        }
        finally
        {
            _blockPickup = savedPickup;
        }

        if (voices.Count == 0) return;

        var first = voices[0];
        var autoFlush = st.Builder.AutoFlush;
        st.Builder.AutoFlush = false;
        foreach (var measure in first.Builder.Part.Measures)
        {
            var a = measure.Attributes;
            if (a != null)
            {
                if (a.Beats is { } beats && a.BeatType is { } beatType) st.Builder.SetTime(beats, beatType);
                if (a.Fifths is { } fifths) st.Builder.SetKey(fifths);
                if (a.ClefSign != null) st.Builder.SetClef(a.ClefSign, a.ClefLine ?? 2);
            }
            foreach (var note in measure.Notes) st.Builder.Add(note);
            st.Builder.Flush();
        }
        st.Builder.AutoFlush = autoFlush;

        var measures = st.Builder.Part.Measures;
        foreach (var voice in voices.Skip(1))
        {
            var index = offset;
            foreach (var measure in voice.Builder.Part.Measures)
            {
                while (measures.Count <= index) measures.Add(new NeutralMeasure { Number = measures.Count + 1 });
                measures[index++].Notes.AddRange(measure.Notes);
            }
        }

        st.Log.AddRange(first.Log);
        st.Last = first.Last;
        (st.PrevStep, st.PrevOctave, st.LastBase, st.LastDots) = (first.PrevStep, first.PrevOctave, first.LastBase, first.LastDots);
    }

    /// <summary>
    /// A voice block that begins part-way through a measure: the first voice continues in the staff, the others are padded
    /// with a leading rest and laid over the measures afterwards.
    /// </summary>
    private void ParseSimultaneousInline(StaffState st)
    {
        _pos += 2;
        var lead = st.Builder.PendingTicks;
        var offset = st.Builder.MeasureCount;
        var extra = new List<StaffState>();
        var index = 0;
        while (true)
        {
            SkipWs();
            if (Eof) throw Fail("Unterminated '<<': missing '>>'");
            if (StartsWith(">>")) { _pos += 2; break; }
            if (StartsWith("\\\\")) { _pos += 2; continue; }
            Tick();

            if (index == 0)
            {
                ParseItem(st);
            }
            else
            {
                var voice = st.Fork(index + 1);
                (voice.Relative, voice.PrevStep, voice.PrevOctave, voice.LastBase, voice.LastDots) =
                    (st.Relative, st.PrevStep, st.PrevOctave, st.LastBase, st.LastDots);
                voice.Builder.Add(new NeutralNote { Ticks = lead, Voice = voice.VoiceNumber });
                ParseItem(voice);
                voice.Builder.Flush();
                extra.Add(voice);
                (st.LastBase, st.LastDots) = (voice.LastBase, voice.LastDots);
                (st.PrevStep, st.PrevOctave) = (voice.PrevStep, voice.PrevOctave);
            }
            index++;
        }

        if (extra.Count == 0) return;
        if (st.Builder.PendingTicks != 0)
        {
            _ctx.Warn("A multi-voice passage ends in the middle of a measure: only its first voice is converted.");
            return;
        }

        var measures = st.Builder.Part.Measures;
        foreach (var voice in extra)
        {
            var at = offset;
            foreach (var measure in voice.Builder.Part.Measures)
            {
                while (measures.Count <= at) measures.Add(new NeutralMeasure { Number = measures.Count + 1 });
                measures[at++].Notes.AddRange(measure.Notes);
            }
        }
    }

    #region Lyrics

    private sealed class LyricToken
    {
        public string? Text;
        public bool HyphenBefore;
        public bool HyphenAfter;
    }

    private void SetLanguage(string name) => _english = string.Equals(name, "english", StringComparison.OrdinalIgnoreCase);

    private void ParseLyricsContext()
    {
        SkipWs();
        if (StartsWith("\\lyricsto"))
        {
            _pos += 9;
            SkipWs();
            string voice;
            if (Peek() == '"') voice = ReadString();
            else
            {
                var begin = _pos;
                while (!Eof && (char.IsLetterOrDigit(_s[_pos]) || _s[_pos] is '_' or '-')) _pos++;
                voice = _s[begin.._pos];
            }
            _pendingLyrics.Add((voice, ReadLyricsText()));
            return;
        }
        AddLyricsToLast();
    }

    private void AddLyricsToLast()
    {
        var text = ReadLyricsText();
        if (_lastLog != null) ApplyLyrics(_lastLog, text);
        else _ctx.Warn("Lyrics without a staff to attach to were skipped.");
    }

    /// <summary>Reads one lyrics expression and returns its raw text; variable references are expanded later.</summary>
    private string ReadLyricsText()
    {
        SkipWs();
        if (Eof) return string.Empty;
        var begin = _pos;
        var c = Peek();
        if (c == '{' || StartsWith("<<")) { SkipBalanced(); return _s[begin.._pos]; }
        if (c == '"') { SkipString(); return _s[begin.._pos]; }
        if (c == '\\')
        {
            _pos++;
            var word = ReadWord();
            if (word is "lyricmode" or "lyrics") return ReadLyricsText();
            return _s[begin.._pos];
        }
        SkipToken();
        return _s[begin.._pos];
    }

    private void ResolveLyrics()
    {
        foreach (var (voice, text) in _pendingLyrics)
        {
            if (_voiceLogs.TryGetValue(voice, out var log)) ApplyLyrics(log, text);
            else _ctx.Warn($"Lyrics refer to the unknown voice '{voice}' and were skipped.");
        }
        _pendingLyrics.Clear();
        _voiceLogs.Clear();
        _verseCounts.Clear();
    }

    private void TokenizeLyrics(string t, List<LyricToken> tokens, ref bool hyphenPending, int depth)
    {
        var i = 0;
        while (i < t.Length)
        {
            var c = t[i];
            if (char.IsWhiteSpace(c) || c is '{' or '}') { i++; continue; }
            if (c == '%')
            {
                while (i < t.Length && t[i] != '\n') i++;
                continue;
            }
            if (c == '"')
            {
                var sb = new System.Text.StringBuilder();
                i++;
                while (i < t.Length && t[i] != '"')
                {
                    if (t[i] == '\\' && i + 1 < t.Length) i++;
                    sb.Append(t[i++]);
                }
                i++;
                tokens.Add(new LyricToken { Text = sb.ToString(), HyphenBefore = hyphenPending });
                hyphenPending = false;
                continue;
            }
            if (c == '\\')
            {
                var end = i + 1;
                while (end < t.Length && char.IsLetter(t[end])) end++;
                var word = t[(i + 1)..end];
                i = end;
                if (word is "set" or "override" or "unset" or "revert" or "once" or "tweak")
                {
                    while (i < t.Length && t[i] != '\n') i++;
                }
                else if (word == "markup")
                {
                    while (i < t.Length && char.IsWhiteSpace(t[i])) i++;
                    if (i < t.Length && t[i] == '{')
                    {
                        var depthCount = 0;
                        do
                        {
                            if (t[i] == '{') depthCount++;
                            else if (t[i] == '}') depthCount--;
                            i++;
                        }
                        while (i < t.Length && depthCount > 0);
                    }
                }
                else if (depth < 16 && _vars.TryGetValue(word, out var body))
                {
                    TokenizeLyrics(body, tokens, ref hyphenPending, depth + 1);
                }
                continue;
            }

            var begin = i;
            while (i < t.Length && !char.IsWhiteSpace(t[i]) && t[i] is not ('{' or '}' or '"')) i++;
            var token = t[begin..i];
            switch (token)
            {
                case "--":
                    if (tokens.Count > 0) tokens[^1].HyphenAfter = true;
                    hyphenPending = true;
                    break;
                case "_":
                case "__":
                    tokens.Add(new LyricToken());
                    break;
                default:
                    tokens.Add(new LyricToken { Text = token, HyphenBefore = hyphenPending });
                    hyphenPending = false;
                    break;
            }
        }
    }

    /// <summary>
    /// Aligns one verse with a voice the way LilyPond does: every syllable (or skip) goes to the next note that
    /// is not a rest, not the continuation of a tie and not inside a slur.
    /// </summary>
    private void ApplyLyrics(List<NeutralNote> log, string text)
    {
        var tokens = new List<LyricToken>();
        var pending = false;
        TokenizeLyrics(text, tokens, ref pending, 0);

        var verse = _verseCounts.TryGetValue(log, out var count) ? count + 1 : 1;
        _verseCounts[log] = verse;

        var index = 0;
        var inSlur = false;
        NeutralNote? previous = null;
        foreach (var note in log)
        {
            if (index >= tokens.Count) break;
            var eligible = !note.IsRest && !inSlur && !(previous is { IsRest: false, TieToNext: true });
            if (note.SlurStop) inSlur = false;
            if (note.SlurStart) inSlur = true;
            previous = note;
            if (!eligible) continue;

            var token = tokens[index++];
            if (token.Text == null) continue;
            var syllabic = token.HyphenBefore
                ? (token.HyphenAfter ? "middle" : "end")
                : (token.HyphenAfter ? "begin" : "single");
            note.Lyrics.Add(new NeutralLyric(verse, token.Text, syllabic));
        }

        if (index < tokens.Count) _ctx.Warn($"Lyrics verse {verse} has {tokens.Count - index} more syllable(s) than notes; the extra text was dropped.");
    }

    #endregion

    private void ParseCommand(StaffState st)
    {
        _pos++;
        if (!char.IsLetter(Peek()))
        {
            if (Peek() is '<' or '>' or '!' && st.Last != null)
            {
                st.Last.Directions.Add(new NeutralDirection(DirectionKinds.Wedge, Peek() switch
                {
                    '<' => "crescendo", '>' => "diminuendo", _ => "stop"
                }));
            }
            if (!Eof) _pos++;
            return;
        }

        var word = ReadWord();
        switch (word)
        {
            case "relative": ParseRelative(st); break;
            case "absolute": WithRelative(st, false, () => ParseItem(st)); break;
            case "time": ParseTime(st); break;
            case "key": ParseKey(st); break;
            case "clef": ParseClef(st); break;
            case "partial":
            {
                var pickup = ReadPlainDuration();
                st.Builder.SetPickup(pickup);
                _blockPickup ??= pickup;
                if (st.Builder.MeasureCount == 0) _scorePickup ??= pickup;
                break;
            }
            case "times": ParseTuplet(st, timesSyntax: true); break;
            case "tuplet": ParseTuplet(st, timesSyntax: false); break;
            case "repeat": ParseRepeat(st); break;
            case "alternative":
                _ctx.Warn("\\alternative endings are all played once, in order.");
                ParseItem(st);
                break;
            case "tempo":
                ParseTempo(st);
                break;
            case "bar":
                SkipWs();
                if (Peek() == '"') SkipString();
                break;
            case "skip":
                Emit(st, null, ReadDuration(st, out _, false));
                break;
            case "set":
            case "override":
            case "revert":
            case "unset":
            case "tweak":
                SkipToEndOfLine();
                break;
            case "new":
            case "context":
                SkipWs();
                var type = ReadWord();
                var contextName = ReadContextHeader();
                if (type == "Lyrics") ParseLyricsContext();
                else if (StaffTypes.Contains(type) || GroupTypes.Contains(type))
                {
                    if (contextName != null) _voiceLogs[contextName] = st.Log;
                    ParseItem(st);
                }
                else
                {
                    _ctx.Warn($"Context '{type}' is not converted.");
                    SkipContextBody();
                }
                break;
            case "addlyrics":
                AddLyricsToLast();
                break;
            case "lyricmode":
            case "lyrics":
            case "chordmode":
            case "drummode":
            case "figuremode":
            case "markup":
            case "markuplist":
            case "header":
            case "layout":
            case "midi":
            case "paper":
            case "with":
                if (word is "lyricmode" or "lyrics") _ctx.Warn("Standalone lyrics are not converted.");
                else if (word is "chordmode" or "drummode" or "figuremode") _ctx.Warn($"\\{word} is not converted.");
                SkipBlockArgument();
                break;
            case "grace":
            case "acciaccatura":
            case "appoggiatura":
            case "slashedGrace":
                ParseGrace(st, word is "acciaccatura" or "slashedGrace");
                break;
            case "afterGrace":
                ParseItem(st);
                ParseGrace(st, false);
                break;
            case "transpose":
                _ctx.Warn("\\transpose is not applied; notes are converted as written.");
                SkipToken();
                SkipWs();
                SkipToken();
                ParseItem(st);
                break;
            case "mark":
                SkipWs();
                if (Peek() == '\\' && char.IsLetter(Peek(1)))
                {
                    _pos++;
                    if (ReadWord() == "markup") SkipBlockArgument();
                }
                else if (Peek() == '"') SkipString();
                else SkipToken();
                break;
            case "transposition":
                SkipWs();
                SkipToken();
                break;
            case "ottava":
                _ctx.Warn("\\ottava is not applied.");
                SkipWs();
                SkipToken();
                break;
            case "version":
            case "include":
                SkipWs();
                if (Peek() == '"') SkipString();
                break;
            case "language":
                SkipWs();
                if (Peek() == '"') SetLanguage(ReadString());
                break;
            default:
                if (TryMark(st, word)) break;
                if (SilentCommands.Contains(word) || _schemeVars.Contains(word)) break;
                if (_functions.TryGetValue(word, out var argTypes)) CallFunction(st, word, argTypes);
                else if (_vars.TryGetValue(word, out var body)) InsertVariable(st, word, body);
                else _ctx.Warn($"Unsupported LilyPond command \\{word} ignored.");
                break;
        }
    }

    private static readonly Dictionary<string, string> MarkNames = new(StringComparer.Ordinal)
    {
        ["staccato"] = "staccato", ["accent"] = "accent", ["tenuto"] = "tenuto", ["marcato"] = "strong-accent",
        ["staccatissimo"] = "staccatissimo", ["portato"] = "detached-legato", ["fermata"] = "fermata",
        ["trill"] = "trill-mark", ["turn"] = "turn", ["reverseturn"] = "inverted-turn", ["mordent"] = "mordent",
        ["prall"] = "inverted-mordent", ["prallmordent"] = "mordent", ["upprall"] = "inverted-mordent",
        ["lineprall"] = "inverted-mordent", ["prallprall"] = "inverted-mordent"
    };

    private static readonly HashSet<string> OrnamentNames = new(StringComparer.Ordinal)
        { "trill-mark", "turn", "inverted-turn", "mordent", "inverted-mordent" };

    private static readonly HashSet<string> DynamicNames = new(StringComparer.Ordinal)
        { "ppp", "pp", "p", "mp", "mf", "f", "ff", "fff", "sf", "sfz", "fp", "sfp", "rfz", "fz" };

    /// <summary>Handles articulation, ornament, fermata and dynamic commands, which mark the preceding note.</summary>
    private bool TryMark(StaffState st, string word)
    {
        if (MarkNames.TryGetValue(word, out var name)) { AddMark(st, name); return true; }
        if (DynamicNames.Contains(word))
        {
            st.Last?.Directions.Add(new NeutralDirection(DirectionKinds.Dynamics, word));
            return true;
        }
        return false;
    }

    private static void AddMark(StaffState st, string? name)
    {
        var note = st.Last;
        if (note == null || name == null) return;
        if (name == "fermata") note.Fermata = true;
        else if (note.IsRest) return;
        else if (OrnamentNames.Contains(name)) note.Ornaments.Add(name);
        else note.Articulations.Add(name);
    }

    /// <summary><c>\tempo "Text" 4=120</c> or <c>\tempo 4.=60</c>; the marking is attached to the next note.</summary>
    private void ParseTempo(StaffState st)
    {
        SkipWs();
        if (Peek() == '\\' && char.IsLetter(Peek(1)))
        {
            _pos++;
            ReadWord();
            SkipBlockArgument();
            SkipWs();
        }
        if (Peek() == '"')
        {
            var text = ReadString();
            if (text.Length > 0) st.PendingDirections.Add(new NeutralDirection(DirectionKinds.Words, text));
            SkipWs();
        }
        if (!char.IsDigit(Peek())) return;

        var start = _pos;
        while (char.IsDigit(Peek())) _pos++;
        var baseValue = int.Parse(_s.AsSpan(start, _pos - start));
        var dotted = false;
        if (Peek() == '.') { dotted = true; _pos++; }
        SkipWs();
        if (Peek() != '=') return;
        _pos++;
        SkipWs();
        start = _pos;
        while (char.IsDigit(Peek())) _pos++;
        if (_pos == start || !double.TryParse(_s.AsSpan(start, _pos - start), out var bpm)) return;
        while (Peek() == '-' || char.IsDigit(Peek())) _pos++; // a range such as 120-132 keeps its first value

        var unit = "quarter";
        if (!dotted)
            unit = baseValue switch { 1 => "whole", 2 => "half", 8 => "eighth", 16 => "16th", _ => "quarter" };
        if (dotted || baseValue is not (1 or 2 or 4 or 8 or 16))
            bpm *= 4.0 / baseValue * (dotted ? 1.5 : 1);
        st.PendingDirections.Add(new NeutralDirection(DirectionKinds.Tempo,
            Math.Round(bpm).ToString(System.Globalization.CultureInfo.InvariantCulture), unit));
    }

    /// <summary>Parses grace music; its notes carry no time and keep relative-pitch and duration state like LilyPond does.</summary>
    private void ParseGrace(StaffState st, bool slash)
    {
        var (was, wasSlash) = (st.InGrace, st.GraceSlash);
        (st.InGrace, st.GraceSlash) = (true, slash);
        try { ParseItem(st); }
        finally { (st.InGrace, st.GraceSlash) = (was, wasSlash); }
    }

    /// <summary>Consumes the arguments of a user-defined music function. Music arguments are kept, except for grace-style functions.</summary>
    private void CallFunction(StaffState st, string name, string[] argTypes)
    {
        _ctx.Warn($"User function \\{name} is not evaluated; its arguments are used as written.");
        foreach (var type in argTypes)
        {
            SkipWs();
            if (Eof) return;
            if (type == "ly:music?")
            {
                if (name.Contains("race", StringComparison.OrdinalIgnoreCase)) ParseGrace(st, false);
                else ParseItem(st);
                continue;
            }
            var c = Peek();
            if (c == '"') SkipString();
            else if (c == '{' || StartsWith("<<")) SkipBalanced();
            else if (c == '#') SkipScheme();
            else if (c == '\\' && char.IsLetter(Peek(1)))
            {
                _pos++;
                if (ReadWord() == "markup") SkipBlockArgument();
            }
            else SkipToken();
        }
    }

    private void InsertVariable(StaffState st, string name, string body)
    {
        if (++_variableDepth > 32) throw Fail($"Variable \\{name} is defined recursively");
        var saved = (_s, _pos);
        _s = body;
        _pos = 0;
        try
        {
            while (true)
            {
                SkipWs();
                if (Eof) break;
                ParseItem(st);
            }
        }
        finally
        {
            (_s, _pos) = saved;
            _variableDepth--;
        }
    }

    private void WithRelative(StaffState st, bool relative, Action action)
    {
        var saved = st.Relative;
        st.Relative = relative;
        try { action(); }
        finally { st.Relative = saved; }
    }

    private void ParseRelative(StaffState st)
    {
        SkipWs();
        var step = 0;
        var octave = 3;
        if (Peek() is >= 'a' and <= 'g')
        {
            var pitch = ReadPitch(false, ref step, ref octave);
            octave = pitch.Octave;
            step = "CDEFGAB".IndexOf(pitch.Step);
        }

        st.PrevStep = step;
        st.PrevOctave = octave;
        WithRelative(st, true, () => ParseItem(st));
    }

    private void ParseTime(StaffState st)
    {
        SkipWs();
        var beats = ReadInt();
        if (Peek() != '/') throw Fail("Expected '/' in \\time signature");
        _pos++;
        var beatType = ReadInt();
        if (beats <= 0 || beatType <= 0 || !DurationHelper.IsValidNoteValue(beatType))
            throw Fail($"Invalid time signature {beats}/{beatType}");
        st.Builder.SetTime(beats, beatType);
    }

    private void ParseKey(StaffState st)
    {
        SkipWs();
        if (Peek() is not (>= 'a' and <= 'g')) throw Fail("Expected a note name after \\key");
        ReadNoteName(out var stepIndex, out var alter);
        SkipWs();
        if (Peek() != '\\') throw Fail("Expected a mode such as \\major after \\key");
        _pos++;
        var mode = ReadWord();
        var fifths = KeySignatureHelper.Fifths("CDEFGAB"[stepIndex], alter, mode)
                     ?? throw Fail($"Unknown key mode \\{mode}");
        st.Builder.SetKey(fifths);
    }

    private void ParseClef(StaffState st)
    {
        SkipWs();
        string name;
        if (Peek() == '"') name = ReadString();
        else
        {
            var start = _pos;
            while (!Eof && (char.IsLetterOrDigit(_s[_pos]) || _s[_pos] == '_')) _pos++;
            name = _s[start.._pos];
        }

        var baseName = name.Split('_', '^')[0].ToLowerInvariant();
        if (name.Contains('_') || name.Contains('^')) _ctx.Warn($"Clef octave transposition in '{name}' is ignored.");
        switch (baseName)
        {
            case "treble": case "violin": case "g": case "french": st.Builder.SetClef("G", 2); break;
            case "bass": case "f": st.Builder.SetClef("F", 4); break;
            case "alto": case "c": st.Builder.SetClef("C", 3); break;
            case "tenor": st.Builder.SetClef("C", 4); break;
            case "percussion": st.Builder.SetClef("percussion", 2); break;
            default:
                _ctx.Warn($"Unsupported clef '{name}'; treble clef assumed.");
                st.Builder.SetClef("G", 2);
                break;
        }
    }

    private void SkipTempo()
    {
        SkipWs();
        if (Peek() == '\\' && char.IsLetter(Peek(1)))
        {
            _pos++;
            ReadWord();
            SkipBlockArgument();
            SkipWs();
        }
        if (Peek() == '"') { SkipString(); SkipWs(); }
        if (char.IsDigit(Peek()))
        {
            while (char.IsDigit(Peek()) || Peek() == '.') _pos++;
            SkipWs();
            if (Peek() == '=')
            {
                _pos++;
                SkipWs();
                while (char.IsDigit(Peek()) || Peek() == '-') _pos++;
            }
        }
    }

    private void ParseTuplet(StaffState st, bool timesSyntax)
    {
        SkipWs();
        var a = ReadInt();
        if (Peek() != '/') throw Fail("Expected a fraction such as 3/2 after the tuplet command");
        _pos++;
        var b = ReadInt();
        if (a <= 0 || b <= 0) throw Fail("Invalid tuplet ratio");

        var actual = timesSyntax ? b : a;
        var normal = timesSyntax ? a : b;
        var (savedActual, savedNormal) = (st.TupActual, st.TupNormal);
        st.TupActual *= actual;
        st.TupNormal *= normal;
        try { ParseItem(st); }
        finally { (st.TupActual, st.TupNormal) = (savedActual, savedNormal); }
    }

    private void ParseRepeat(StaffState st)
    {
        SkipWs();
        var kind = ReadWord();
        SkipWs();
        var count = Math.Clamp(ReadInt(), 1, 64);
        if (kind is not ("volta" or "unfold"))
        {
            _ctx.Warn($"\\repeat {kind} is not expanded; the music is played once.");
            count = 1;
        }

        SkipWs();
        var start = _pos;
        // LilyPond resolves relative pitches once for the repeated body, so every copy starts from the same reference.
        var (startStep, startOctave) = (st.PrevStep, st.PrevOctave);
        for (var i = 0; i < count; i++)
        {
            _pos = start;
            (st.PrevStep, st.PrevOctave) = (startStep, startOctave);
            ParseItem(st);
        }
    }

    private int ReadPlainDuration()
    {
        SkipWs();
        var value = ReadInt();
        if (!DurationHelper.IsValidNoteValue(value)) throw Fail($"Unsupported note value {value}");
        var dots = 0;
        while (Peek() == '.') { dots++; _pos++; }
        return DurationHelper.TicksFor(value, dots);
    }

    private void ReadNoteName(out int stepIndex, out int alter)
    {
        var letter = _s[_pos++];
        stepIndex = "cdefgab".IndexOf(letter);
        alter = 0;
        var first = true;
        if (_english)
        {
            while (true)
            {
                if (StartsWith("sharp")) { alter++; _pos += 5; }
                else if (StartsWith("flat")) { alter--; _pos += 4; }
                else if (StartsWith("ss")) { alter += 2; _pos += 2; }
                else if (StartsWith("ff")) { alter -= 2; _pos += 2; }
                else if (StartsWith("x")) { alter += 2; _pos++; }
                else if (StartsWith("s") && !char.IsLetter(Peek(1))) { alter++; _pos++; }
                else if (StartsWith("f") && !char.IsLetter(Peek(1))) { alter--; _pos++; }
                else break;
            }
            return;
        }

        while (true)
        {
            if (StartsWith("is")) { alter++; _pos += 2; }
            else if (StartsWith("es")) { alter--; _pos += 2; }
            else if (first && (letter == 'a' || letter == 'e') && Peek() == 's' && !char.IsLetter(Peek(1))) { alter--; _pos++; }
            else break;
            first = false;
        }
    }

    private NeutralPitch ReadPitch(bool relative, ref int refStep, ref int refOctave)
    {
        ReadNoteName(out var step, out var alter);

        var offset = 0;
        while (Peek() is '\'' or ',')
        {
            offset += Peek() == '\'' ? 1 : -1;
            _pos++;
        }
        while (Peek() is '!' or '?') _pos++;

        int octave;
        if (relative)
        {
            var diff = step - refStep;
            octave = refOctave + (diff > 3 ? -1 : diff < -3 ? 1 : 0) + offset;
            refStep = step;
            refOctave = octave;
        }
        else
        {
            octave = 3 + offset;
        }

        return new NeutralPitch("CDEFGAB"[step], alter, octave);
    }

    private void ParseNote(StaffState st)
    {
        var pitch = ReadPitch(st.Relative, ref st.PrevStep, ref st.PrevOctave);
        var ticks = ReadDuration(st, out _, false);
        Emit(st, new List<NeutralPitch> { pitch }, ticks);
    }

    private void ParseChord(StaffState st)
    {
        var startLine = CurrentLine();
        _pos++;
        var pitches = new List<NeutralPitch>();
        var refStep = st.PrevStep;
        var refOctave = st.PrevOctave;
        (int Step, int Octave)? first = null;

        while (true)
        {
            SkipWs();
            if (Eof) throw _ctx.ParseError("Unterminated chord: '<' is never closed", startLine);
            var c = Peek();
            if (c == '>') { _pos++; break; }
            if (c is >= 'a' and <= 'g')
            {
                var p = ReadPitch(st.Relative, ref refStep, ref refOctave);
                first ??= ("CDEFGAB".IndexOf(p.Step), p.Octave);
                pitches.Add(p);
            }
            else
            {
                SkipToken();
            }
        }

        if (pitches.Count == 0) throw _ctx.ParseError("Empty chord", startLine);
        if (st.Relative && first is { } f)
        {
            st.PrevStep = f.Step;
            st.PrevOctave = f.Octave;
        }

        var ticks = ReadDuration(st, out _, false);
        st.LastChord = pitches;
        Emit(st, pitches, ticks);
    }

    private void ParseRepeatedChord(StaffState st)
    {
        _pos++;
        var ticks = ReadDuration(st, out _, false);
        if (st.LastChord == null)
        {
            _ctx.Warn("Chord repeat 'q' without a preceding chord was ignored.");
            return;
        }
        if (st.Relative)
        {
            var first = st.LastChord[0];
            st.PrevStep = "CDEFGAB".IndexOf(first.Step);
            st.PrevOctave = first.Octave;
        }
        Emit(st, st.LastChord.ToList(), ticks);
    }

    private void ParseRest(StaffState st)
    {
        _pos++;
        if (char.IsLetter(Peek()))
        {
            _ctx.Warn("Unsupported LilyPond text ignored.");
            SkipToken();
            return;
        }
        var ticks = ReadDuration(st, out _, false);
        Emit(st, null, ticks);
    }

    private void ParseMultiMeasureRest(StaffState st)
    {
        _pos++;
        var ticks = ReadDuration(st, out var repeat, true);
        for (var i = 0; i < repeat; i++) Emit(st, null, ticks);
    }

    private int ReadDuration(StaffState st, out int repeat, bool restRepeat)
    {
        repeat = 1;
        if (char.IsDigit(Peek()))
        {
            var value = ReadInt();
            if (!DurationHelper.IsValidNoteValue(value)) throw Fail($"Unsupported note value {value}");
            var dots = 0;
            while (Peek() == '.') { dots++; _pos++; }
            st.LastBase = value;
            st.LastDots = dots;
        }
        else
        {
            while (Peek() == '.') { st.LastDots++; _pos++; }
        }

        var ticks = (double)DurationHelper.TicksFor(st.LastBase, st.LastDots);
        while (Peek() == '*')
        {
            _pos++;
            var num = ReadInt();
            var den = 1;
            if (Peek() == '/') { _pos++; den = ReadInt(); }
            if (den == 0) throw Fail("Division by zero in duration multiplier");
            if (restRepeat && den == 1) repeat = num;
            else ticks = ticks * num / den;
        }
        return (int)Math.Round(ticks);
    }

    private void Emit(StaffState st, List<NeutralPitch>? pitches, int ticks)
    {
        if (ticks == 0) return; // e.g. s1*0, a zero-length spacer used to carry events
        var note = new NeutralNote { Pitches = pitches ?? new List<NeutralPitch>(), Ticks = ticks, Voice = st.VoiceNumber };
        if (st.InGrace)
        {
            if (note.IsRest) return;
            note.IsGrace = true;
            note.GraceSlash = st.GraceSlash;
            st.Builder.Add(note);
            st.Last = note;
            return;
        }
        if (st.PendingDirections.Count > 0)
        {
            note.Directions.AddRange(st.PendingDirections);
            st.PendingDirections.Clear();
        }
        if (st.TupActual != 1 || st.TupNormal != 1)
        {
            note.Ticks = (int)Math.Round(ticks * (double)st.TupNormal / st.TupActual);
            note.TupletActual = st.TupActual;
            note.TupletNormal = st.TupNormal;
        }
        if (note.Ticks <= 0) throw Fail("Note duration is too short to represent");
        st.Builder.Add(note);
        st.Log.Add(note);
        st.Last = note;
    }

    #endregion
}
