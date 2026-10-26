namespace MusicConverter.Services;

/// <summary>Counts note-on events in a Standard MIDI File without sharing any code with the converter's parser.</summary>
internal static class MidiNoteCounter
{
    /// <returns>The number of note-ons with non-zero velocity, or null when the file cannot be walked reliably.</returns>
    public static int? Count(byte[] data)
    {
        if (data.Length < 14 || data[0] != 'M' || data[1] != 'T' || data[2] != 'h' || data[3] != 'd') return null;
        var position = 8 + ReadInt32(data, 4);
        var count = 0;

        while (position + 8 <= data.Length)
        {
            var isTrack = data[position] == 'M' && data[position + 1] == 'T' && data[position + 2] == 'r' && data[position + 3] == 'k';
            var length = ReadInt32(data, position + 4);
            var start = position + 8;
            var end = (int)Math.Min((long)start + length, data.Length);
            position = end;
            if (!isTrack) continue;

            var i = start;
            byte running = 0;
            while (i < end)
            {
                if (!SkipVariableLength(data, ref i, end)) return null;
                if (i >= end) return null;
                var b = data[i];
                byte status;
                if ((b & 0x80) != 0)
                {
                    status = b;
                    i++;
                    if (status < 0xF0) running = status;
                    else if (status is 0xF0 or 0xF7) running = 0;
                }
                else
                {
                    if (running == 0) return null;
                    status = running;
                }

                if (status == 0xFF)
                {
                    if (i >= end) return null;
                    i++;
                    var size = ReadVariableLength(data, ref i, end);
                    if (size < 0) return null;
                    i += size;
                }
                else if (status is 0xF0 or 0xF7)
                {
                    var size = ReadVariableLength(data, ref i, end);
                    if (size < 0) return null;
                    i += size;
                }
                else if (status >= 0xF0)
                {
                    return null;
                }
                else
                {
                    var command = status & 0xF0;
                    var dataBytes = command is 0xC0 or 0xD0 ? 1 : 2;
                    if (i + dataBytes > end) return null;
                    if (command == 0x90 && data[i + 1] > 0) count++;
                    i += dataBytes;
                }
            }
        }

        return count;
    }

    private static int ReadInt32(byte[] d, int p) => (d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3];

    private static bool SkipVariableLength(byte[] d, ref int i, int end) => ReadVariableLength(d, ref i, end) >= 0;

    private static int ReadVariableLength(byte[] d, ref int i, int end)
    {
        long value = 0;
        for (var n = 0; n < 4; n++)
        {
            if (i >= end) return -1;
            var b = d[i++];
            value = (value << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) == 0) return value > int.MaxValue ? -1 : (int)value;
        }

        return -1;
    }
}