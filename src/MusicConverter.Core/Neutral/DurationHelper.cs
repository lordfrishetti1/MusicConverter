namespace MusicConverter.Neutral;

public static class DurationHelper
{
    public const int TicksPerQuarter = 480;
    public const int WholeTicks = TicksPerQuarter * 4;

    private static readonly (string Name, int Ticks)[] Types =
    {
        ("whole", WholeTicks), ("half", WholeTicks / 2), ("quarter", WholeTicks / 4), ("eighth", WholeTicks / 8),
        ("16th", WholeTicks / 16), ("32nd", WholeTicks / 32), ("64th", WholeTicks / 64)
    };

    public static bool IsValidNoteValue(int value) => value is 1 or 2 or 4 or 8 or 16 or 32 or 64;

    /// <summary>Ticks for a note value (4 = quarter) with the given number of dots.</summary>
    public static int TicksFor(int noteValue, int dots)
    {
        if (!IsValidNoteValue(noteValue)) throw new ArgumentOutOfRangeException(nameof(noteValue));
        var ticks = WholeTicks / noteValue;
        var total = ticks;
        var add = ticks;
        for (var i = 0; i < dots; i++)
        {
            add /= 2;
            total += add;
        }
        return total;
    }

    public static int TicksPerMeasure(int beats, int beatType) => beats * WholeTicks / beatType;

    /// <summary>Best-effort notated type and dot count for a written duration.</summary>
    public static (string Type, int Dots) Describe(int writtenTicks)
    {
        foreach (var (name, ticks) in Types)
        {
            for (var dots = 0; dots <= 2; dots++)
            {
                if (ticks * ((1 << (dots + 1)) - 1) == writtenTicks * (1 << dots))
                    return (name, dots);
            }
        }

        foreach (var (name, ticks) in Types)
        {
            if (ticks <= writtenTicks) return (name, 0);
        }
        return ("64th", 0);
    }
}
