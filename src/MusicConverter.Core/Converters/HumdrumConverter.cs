using System.Text.RegularExpressions;
using MusicConverter.Logging;
using MusicConverter.MusicXml;
using MusicConverter.Neutral;

namespace MusicConverter.Converters;

/// <summary>Imports Humdrum **kern files (.krn, .hum).</summary>
public sealed class HumdrumConverter : MusicFormatConverterBase
{
    private static readonly string[] Extensions = { ".krn", ".hum" };

    public HumdrumConverter() : this(null) { }

    public HumdrumConverter(ILogger? logger) : base(logger) { }

    public override string FormatName => "Humdrum";

    public override IReadOnlyCollection<string> FileExtensions => Extensions;

    protected override NeutralScore Parse(string text, ConversionContext context) =>
        new HumdrumParser(text, context).Parse();
}

internal sealed class HumdrumParser
{
    private static readonly Regex TimeRegex = new(@"^\*M(\d+)/(\d+)", RegexOptions.Compiled);
    private static readonly Regex ClefRegex = new(@"^\*clef([GFC])v?(\d)$", RegexOptions.Compiled);
    private static readonly Regex KeyRegex = new(@"^\*k\[([a-gA-G#\-n]*)\]$", RegexOptions.Compiled);

    private sealed class Track
    {
        public Track(string kind, PartBuilder? builder, bool secondary)
        {
            Kind = kind;
            Builder = builder;
            Secondary = secondary;
        }

        public string Kind { get; }
        public PartBuilder? Builder { get; }
        public bool Secondary { get; }
        public int Voice { get; set; } = 1;

        /// <summary>For lyric spines (**text, **silbe): the **kern spine the syllables belong to.</summary>
        public Track? Target { get; init; }
        public int Verse { get; init; } = 1;
        public bool InWord { get; set; }
    }

    private readonly Dictionary<(PartBuilder Main, int Voice), PartBuilder> _voiceBuilders = new();
    private readonly Dictionary<PartBuilder, int> _barStart = new();

    private readonly string[] _lines;
    private readonly ConversionContext _ctx;
    private readonly NeutralScore _score = new();
    private readonly List<PartBuilder> _builders = new();
    private List<Track> _tracks = new();
    private int _lineNo;

    public HumdrumParser(string text, ConversionContext ctx)
    {
        _lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        _ctx = ctx;
    }

    public NeutralScore Parse()
    {
        for (var i = 0; i < _lines.Length; i++)
        {
            _ctx.ThrowIfCancelled();
            _ctx.ReportParsing((i + 1) / (double)_lines.Length, "Parsing Humdrum");
            _lineNo = i + 1;
            var line = _lines[i].TrimEnd();
            if (line.Length == 0) continue;

            if (line.StartsWith("!!"))
            {
                ReadReferenceRecord(line);
                continue;
            }
            if (line.StartsWith('!')) continue;

            var tokens = line.Split('\t');
            if (tokens[0].StartsWith("**")) StartSpines(tokens);
            else if (_tracks.Count == 0) throw _ctx.ParseError("Data found before any '**' exclusive interpretation line", _lineNo);
            else if (tokens[0].StartsWith('*')) ReadInterpretations(tokens);
            else ReadData(tokens);
        }

        if (_builders.Count == 0)
            throw _ctx.ParseError("No **kern spines found; not a Humdrum **kern file", 1);

        foreach (var builder in _builders)
        {
            builder.Flush();
            MergeVoices(builder);
            _score.Parts.Add(builder.Part);
        }
        return _score;
    }

    private void ReadReferenceRecord(string line)
    {
        if (line.StartsWith("!!!COM:")) _score.Composer = line[7..].Trim();
        else if (line.StartsWith("!!!OTL:")) _score.Title = line[7..].Trim();
    }

    private void StartSpines(string[] tokens)
    {
        _tracks = new List<Track>();
        Track? lastKern = null;
        foreach (var token in tokens)
        {
            if (token == "**kern")
            {
                var builder = new PartBuilder($"Part {_builders.Count + 1}") { AutoFlush = false };
                _builders.Add(builder);
                lastKern = new Track(token, builder, false);
                _tracks.Add(lastKern);
            }
            else if (token is "**text" or "**silbe" && lastKern != null)
            {
                var verse = _tracks.Count(t => t.Target == lastKern && t.Kind != "**dynam") + 1;
                _tracks.Add(new Track(token, null, false) { Target = lastKern, Verse = verse });
            }
            else if (token == "**dynam" && lastKern != null)
            {
                _tracks.Add(new Track(token, null, false) { Target = lastKern });
            }
            else
            {
                _ctx.Warn($"Spine type '{token}' is not supported and was ignored");
                _tracks.Add(new Track(token, null, false));
            }
        }
    }

    private void ReadInterpretations(string[] tokens)
    {
        if (tokens.Length != _tracks.Count)
            throw _ctx.ParseError($"Expected {_tracks.Count} spines but found {tokens.Length}", _lineNo);

        var next = new List<Track>();
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            var track = _tracks[i];
            switch (token)
            {
                case "*-":
                    break;
                case "*^":
                    next.Add(track);
                    next.Add(new Track(track.Kind, track.Builder, true) { Target = track.Target, Verse = track.Verse });
                    break;
                case "*v":
                    if (!(i > 0 && tokens[i - 1] == "*v")) next.Add(track);
                    break;
                case "*+":
                case "*x":
                    throw _ctx.ParseError($"Spine operation '{token}' is not supported", _lineNo);
                default:
                    if (track.Builder != null && !track.Secondary) ApplyInterpretation(track.Builder, token);
                    next.Add(track);
                    break;
            }
        }
        _tracks = next;

        // Spines that share a part are numbered 1, 2, ... from left to right.
        foreach (var group in next.Where(t => t.Builder != null).GroupBy(t => t.Builder))
        {
            var voice = 0;
            foreach (var t in group) t.Voice = ++voice;
        }
    }

    private void ApplyInterpretation(PartBuilder builder, string token)
    {
        if (TimeRegex.Match(token) is { Success: true } time)
        {
            var beats = int.Parse(time.Groups[1].Value);
            var beatType = int.Parse(time.Groups[2].Value);
            if (beats <= 0 || !DurationHelper.IsValidNoteValue(beatType))
                throw _ctx.ParseError($"Invalid time signature '{token}'", _lineNo);
            builder.SetTime(beats, beatType);
        }
        else if (ClefRegex.Match(token) is { Success: true } clef)
        {
            builder.SetClef(clef.Groups[1].Value, int.Parse(clef.Groups[2].Value));
        }
        else if (KeyRegex.Match(token) is { Success: true } key)
        {
            var accidentals = key.Groups[1].Value;
            var sharps = accidentals.Count(c => c == '#');
            var flats = accidentals.Count(c => c == '-');
            builder.SetKey(sharps - flats);
        }
        else if (token.StartsWith("*I\"") && token.Length > 3)
        {
            builder.Part.Name = token[3..];
        }
        else if (Regex.Match(token, @"^\*MM(\d+(?:\.\d+)?)$") is { Success: true } tempo)
        {
            var bpm = Math.Round(double.Parse(tempo.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
            _pendingTempo[builder] = new NeutralDirection(DirectionKinds.Tempo,
                bpm.ToString(System.Globalization.CultureInfo.InvariantCulture), "quarter");
        }
    }

    private readonly Dictionary<PartBuilder, NeutralDirection> _pendingTempo = new();

    private void ReadData(string[] tokens)
    {
        if (tokens.Length != _tracks.Count)
            throw _ctx.ParseError($"Expected {_tracks.Count} spines but found {tokens.Length}", _lineNo);

        var lineNotes = new Dictionary<Track, NeutralNote?>();
        for (var i = 0; i < tokens.Length; i++)
        {
            var track = _tracks[i];
            var token = tokens[i];
            if (track.Target != null)
            {
                var target = lineNotes.GetValueOrDefault(track.Target);
                if (track.Kind == "**dynam") ReadDynamics(target, token);
                else if (!token.StartsWith('=')) ReadSyllable(track, target, token);
                continue;
            }
            if (track.Builder is null || token == ".") continue;

            if (token.StartsWith('='))
            {
                if (!track.Secondary)
                {
                    track.Builder.Flush();
                    MergeVoices(track.Builder);
                    if (token.Contains(":|")) track.Builder.MarkRepeatEnd();
                    if (token.Contains("|:")) track.Builder.MarkRepeatStart();
                }
                continue;
            }
            if (track.Secondary)
            {
                ReadNote(VoiceBuilder(track.Builder, track.Voice), token, track.Voice);
                continue;
            }
            var read = ReadNote(track.Builder, token, 1);
            if (read != null && _pendingTempo.Remove(track.Builder, out var tempo)) read.Directions.Add(tempo);
            lineNotes[track] = read;
        }
    }

    private PartBuilder VoiceBuilder(PartBuilder main, int voice)
    {
        if (_voiceBuilders.TryGetValue((main, voice), out var builder)) return builder;
        builder = main.Fork();
        builder.AutoFlush = false;
        _voiceBuilders[(main, voice)] = builder;
        return builder;
    }

    /// <summary>Lays the notes of extra voices (spine splits) over the measure just written for the first voice.</summary>
    private void MergeVoices(PartBuilder main)
    {
        _barStart.TryGetValue(main, out var barStart);
        foreach (var ((owner, voice), builder) in _voiceBuilders.Where(kv => kv.Key.Main == main).OrderBy(kv => kv.Key.Voice).ToList())
        {
            builder.Flush();
            var notes = builder.Part.Measures.SelectMany(m => m.Notes).ToList();
            builder.Part.Measures.Clear();
            if (notes.Count == 0) continue;
            if (main.MeasureCount > barStart)
            {
                main.Part.Measures[^1].Notes.AddRange(notes);
            }
            else
            {
                foreach (var note in notes) { note.Voice = 1; main.Add(note); }
                main.Flush();
            }
        }
        _barStart[main] = main.MeasureCount;
    }

    private NeutralNote? ReadNote(PartBuilder builder, string token, int voice)
    {
        var pitches = new List<NeutralPitch>();
        var tie = false;
        int ticks = 0;
        int? tupletActual = null, tupletNormal = null;
        var grace = false;
        var graceSlash = false;
        var articulations = new List<string>();
        var ornaments = new List<string>();
        var fermata = false;
        var slurStart = false;
        var slurStop = false;

        foreach (var sub in token.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var recip = string.Empty;
            var dots = 0;
            var letters = new List<char>();
            var alter = 0;
            var rest = false;

            foreach (var ch in sub)
            {
                if (char.IsDigit(ch)) recip += ch;
                else if (ch == '.') dots++;
                else if (ch is >= 'a' and <= 'g' or >= 'A' and <= 'G') letters.Add(ch);
                else if (ch == 'r') rest = true;
                else if (ch == '#') alter++;
                else if (ch == '-') alter--;
                else if (ch is 'q' or 'Q') { grace = true; graceSlash |= ch == 'q'; }
                else if (ch is '[' or '_') tie = true;
                else if (ch == '(') slurStart = true;
                else if (ch == ')') slurStop = true;
                else if (ch == ';') fermata = true;
                else if (HumdrumMarks.TryGetValue(ch, out var mark))
                {
                    var target = mark is "trill-mark" or "turn" or "mordent" or "inverted-mordent" ? ornaments : articulations;
                    if (!target.Contains(mark)) target.Add(mark);
                }
            }

            if (recip.Length == 0)
            {
                if (!grace) throw _ctx.ParseError($"Note '{sub}' has no duration", _lineNo);
            }
            else if (ticks == 0) (ticks, tupletActual, tupletNormal) = ReadDuration(recip, dots, sub);

            if (rest) continue;
            if (letters.Count == 0) throw _ctx.ParseError($"Note '{sub}' has neither a pitch nor a rest", _lineNo);
            if (alter is < -2 or > 2) throw _ctx.ParseError($"Unsupported accidental in '{sub}'", _lineNo);
            var lower = char.IsLower(letters[0]);
            var octave = lower ? 3 + letters.Count : 4 - letters.Count;
            pitches.Add(new NeutralPitch(char.ToUpperInvariant(letters[0]), alter, octave));
        }

        if (grace && pitches.Count == 0) return null;

        var note = new NeutralNote
        {
            Pitches = pitches,
            Ticks = grace ? Math.Max(ticks, 1) : ticks,
            Voice = voice,
            TieToNext = tie && pitches.Count > 0,
            TupletActual = grace ? null : tupletActual,
            TupletNormal = grace ? null : tupletNormal,
            IsGrace = grace,
            GraceSlash = grace && graceSlash,
            Fermata = fermata,
            SlurStart = slurStart,
            SlurStop = slurStop
        };
        if (pitches.Count > 0)
        {
            note.Articulations.AddRange(articulations);
            note.Ornaments.AddRange(ornaments);
        }
        builder.Add(note);
        return grace ? null : note;
    }

    private static readonly Dictionary<char, string> HumdrumMarks = new()
    {
        ['\''] = "staccato", ['`'] = "staccatissimo", ['~'] = "tenuto", ['^'] = "accent",
        ['t'] = "trill-mark", ['T'] = "trill-mark", ['M'] = "inverted-mordent", ['m'] = "mordent",
        ['S'] = "turn", ['$'] = "turn"
    };

    private static readonly Regex DynamicsRegex = new(@"rfz|sfz|sf|fz|mp|mf|ppp|pp|p|fff|ff|f|<|>|\[|\]", RegexOptions.Compiled);

    private static void ReadDynamics(NeutralNote? note, string token)
    {
        if (note is null || token == "." || token.StartsWith('!') || token.StartsWith('*')) return;
        foreach (Match m in DynamicsRegex.Matches(token))
        {
            note.Directions.Add(m.Value switch
            {
                "<" => new NeutralDirection(DirectionKinds.Wedge, "crescendo"),
                ">" => new NeutralDirection(DirectionKinds.Wedge, "diminuendo"),
                "[" or "]" => new NeutralDirection(DirectionKinds.Wedge, "stop"),
                var d => new NeutralDirection(DirectionKinds.Dynamics, d)
            });
        }
    }

    private static void ReadSyllable(Track text, NeutralNote? note, string token)
    {
        if (token == "." || token.StartsWith('!') || token.StartsWith('*')) return;
        var startsHyphen = token.Length > 1 && token[0] == '-';
        var endsHyphen = token.Length > 1 && token[^1] == '-';
        var clean = token.Trim('-').Replace('_', ' ').Trim();
        var inWord = text.InWord || startsHyphen;
        text.InWord = endsHyphen;
        if (note is null || note.IsRest || clean.Length == 0) return;
        var syllabic = (inWord, endsHyphen) switch
        {
            (true, true) => "middle",
            (true, false) => "end",
            (false, true) => "begin",
            _ => "single"
        };
        note.Lyrics.Add(new NeutralLyric(text.Verse, clean, syllabic));
    }

    private (int Ticks, int? Actual, int? Normal) ReadDuration(string recip, int dots, string token)
    {
        if (!int.TryParse(recip, out var value) || dots > 3)
            throw _ctx.ParseError($"Invalid duration in '{token}'", _lineNo);

        if (value == 0)
        {
            _ctx.Warn("Breve durations are notated as whole notes with a doubled duration");
            return (DurationHelper.WholeTicks * 2, null, null);
        }
        if (DurationHelper.IsValidNoteValue(value))
            return (DurationHelper.TicksFor(value, dots), null, null);

        var power = 1;
        while (power * 2 < value) power *= 2;
        if (power > 64 || power < 2)
            throw _ctx.ParseError($"Unsupported duration '{recip}' in '{token}'", _lineNo);

        var divisor = Gcd(value, power);
        var actual = value / divisor;
        var normal = power / divisor;
        var written = DurationHelper.TicksFor(power, dots);
        if (written * normal % actual != 0)
        {
            _ctx.Warn($"Duration '{recip}' cannot be represented exactly; it was notated as 1/{power}");
            return (written, null, null);
        }
        return (written * normal / actual, actual, normal);
    }

    private static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);
}
