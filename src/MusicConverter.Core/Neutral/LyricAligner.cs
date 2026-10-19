namespace MusicConverter.Neutral;

/// <summary>One lyric token: a syllable, or a skip (<see cref="Text"/> null) that leaves its note without a syllable.</summary>
public sealed record LyricToken(string? Text, bool HyphenAfter);

/// <summary>Shared helpers that attach lyric syllables to neutral notes.</summary>
public static class LyricAligner
{
    /// <summary>
    /// Tokenises a free-text lyric line (ABC <c>w:</c> / NWC style): whitespace and '-' separate syllables,
    /// '_' (when standing alone) and '*' skip a note, '~' joins words, and '|' bar markers are ignored.
    /// </summary>
    public static List<LyricToken> Tokenize(string text, bool underscoreIsSpace = false)
    {
        var tokens = new List<LyricToken>();
        var current = new System.Text.StringBuilder();

        void End(bool hyphen)
        {
            if (current.Length > 0)
            {
                var s = current.ToString().Trim();
                current.Clear();
                if (s.Length == 0) return;
                if (s == "*" || (s == "_" && !underscoreIsSpace)) tokens.Add(new LyricToken(null, false));
                else tokens.Add(new LyricToken(s, hyphen));
            }
            else if (hyphen && tokens.Count > 0 && tokens[^1].Text != null)
            {
                tokens[^1] = tokens[^1] with { HyphenAfter = true };
            }
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                i++;
                current.Append(text[i]);
            }
            else if (char.IsWhiteSpace(c)) End(false);
            else if (c == '-') End(true);
            else if (c == '|') End(false);
            else if (c == '~') current.Append(' ');
            else if (c == '_' && underscoreIsSpace) { if (current.Length > 0) current.Append(' '); }
            else if ((c == '_' || c == '*') && current.Length == 0) { current.Append(c); End(false); }
            else current.Append(c);
        }
        End(false);
        return tokens;
    }

    /// <summary>Notes that can carry a syllable: pitched notes that are not the continuation of a tie.</summary>
    public static List<NeutralNote> Singable(IEnumerable<NeutralNote> notes)
    {
        var result = new List<NeutralNote>();
        NeutralNote? previous = null;
        foreach (var note in notes)
        {
            if (note.IsRest) continue;
            var continuation = previous is { TieToNext: true };
            previous = note;
            if (!continuation) result.Add(note);
        }
        return result;
    }

    /// <summary>Assigns the tokens to the notes in order; returns how many syllables were placed.</summary>
    public static int Apply(IReadOnlyList<NeutralNote> notes, IReadOnlyList<LyricToken> tokens, int verse)
    {
        var placed = 0;
        var previousHyphen = false;
        for (var i = 0; i < tokens.Count && i < notes.Count; i++)
        {
            var token = tokens[i];
            if (token.Text == null) { previousHyphen = false; continue; }
            var syllabic = (previousHyphen, token.HyphenAfter) switch
            {
                (true, true) => "middle",
                (true, false) => "end",
                (false, true) => "begin",
                _ => "single"
            };
            notes[i].Lyrics.Add(new NeutralLyric(verse, token.Text, syllabic));
            previousHyphen = token.HyphenAfter;
            placed++;
        }
        return placed;
    }
}
