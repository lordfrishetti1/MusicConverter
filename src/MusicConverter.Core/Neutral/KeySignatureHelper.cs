namespace MusicConverter.Neutral;

public static class KeySignatureHelper
{
    private static readonly char[] SharpOrder = { 'F', 'C', 'G', 'D', 'A', 'E', 'B' };
    private static readonly char[] FlatOrder = { 'B', 'E', 'A', 'D', 'G', 'C', 'F' };

    /// <summary>Alteration (-1, 0, +1) a key signature with <paramref name="fifths"/> applies to a note letter.</summary>
    public static int AlterFor(char step, int fifths)
    {
        if (fifths > 0 && Array.IndexOf(SharpOrder, step) is int s && s >= 0 && s < fifths) return 1;
        if (fifths < 0 && Array.IndexOf(FlatOrder, step) is int f && f >= 0 && f < -fifths) return -1;
        return 0;
    }

    /// <summary>Position on the circle of fifths for a tonic and mode name (null when the mode is unknown).</summary>
    public static int? Fifths(char tonicStep, int tonicAlter, string? mode)
    {
        var offset = ModeOffset(mode);
        if (offset is null) return null;
        var letter = char.ToUpperInvariant(tonicStep) switch
        {
            'F' => -1, 'C' => 0, 'G' => 1, 'D' => 2, 'A' => 3, 'E' => 4, 'B' => 5, _ => (int?)null
        };
        if (letter is null) return null;
        return letter.Value + 7 * tonicAlter + offset.Value;
    }

    public static int? ModeOffset(string? mode)
    {
        var m = (mode ?? string.Empty).Trim().ToLowerInvariant();
        if (m.Length == 0 || m.StartsWith("maj") || m.StartsWith("ion")) return 0;
        if (m == "m" || m.StartsWith("min") || m.StartsWith("aeo")) return -3;
        if (m.StartsWith("mix")) return -1;
        if (m.StartsWith("dor")) return -2;
        if (m.StartsWith("phr")) return -4;
        if (m.StartsWith("lyd")) return 1;
        if (m.StartsWith("loc")) return -5;
        return null;
    }
}
