using MusicConverter.Abstractions;
using MusicConverter.Converters;
using MusicConverter.Logging;

namespace MusicConverter.Tests;

public sealed class MidiConverterTests
{
    private static readonly byte[] SimpleMidi = Convert.FromHexString(
        "4D546864000000060001000101E0" +
        "4D54726B0000002D" +
        "00FF03055069616E6F" +
        "00FF510307A120" +
        "00FF580403021808" +
        "00903C40" +
        "00904040" +
        "8360803C00" +
        "00804000" +
        "00FF2F00");

    [Fact]
    public async Task ConvertsMidiNotesChordsTrackNameAndTimeSignature()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "score.mid");
        Directory.CreateDirectory(dir.Path);
        await File.WriteAllBytesAsync(path, SimpleMidi);

        var converter = new MidiConverter();
        Assert.True(converter.CanRead(path));
        Assert.True(converter.CanRead("score.MIDI"));
        Assert.False(converter.CanRead("score.mxl"));

        var document = await converter.ConvertAsync(path, null);

        var part = Assert.Single(document.Parts);
        Assert.Equal("Piano", part.Name);
        var measure = Assert.Single(part.Measures);
        Assert.Equal(3, measure.Attributes!.Beats);
        Assert.Equal(4, measure.Attributes.BeatType);
        Assert.Collection(measure.Notes.Where(n => !n.IsRest),
            first =>
            {
                Assert.Equal("C", first.Pitch!.Step.ToString());
                Assert.Equal(4, first.Pitch.Octave);
                Assert.Equal(480, first.Duration);
                Assert.False(first.IsChord);
                Assert.Contains(first.Directions, direction => direction.Kind == "tempo" && direction.Value == "120");
            },
            second =>
            {
                Assert.Equal("E", second.Pitch!.Step.ToString());
                Assert.Equal(480, second.Duration);
                Assert.True(second.IsChord);
            });
        Assert.Equal("MIDI", document.ConverterName);
    }

    [Fact]
    public async Task SloppyTimingIsQuantizedToCleanNoteValues()
    {
        // Four quarter notes with human-like jitter: starts off by a few ticks, lengths 440-500.
        var document = await ConvertAsync(Midi(
            (5, On(60)), (470, Off(60)),
            (10, On(62)), (465, Off(62)),
            (3, On(64)), (440, Off(64)),
            (37, On(65)), (500, Off(65))));

        var notes = document.AllNotes().ToList();
        Assert.All(notes, n => Assert.Equal(480, n.Duration));
        Assert.Equal(new[] { "C", "D", "E", "F" }, notes.Select(n => n.Pitch!.Step.ToString()).ToArray());
        Assert.All(notes, n => Assert.Equal("quarter", n.Type));
    }

    [Fact]
    public async Task OffBeatNotesAreSplitAtBeatsWithTies()
    {
        // A half note starting on the second eighth of beat 1 (tick 240) ends at 1200 in 4/4.
        var document = await ConvertAsync(Midi((240, On(60)), (960, Off(60))));

        var notes = document.AllNotes().ToList();
        Assert.True(notes[0].IsRest);
        var sounding = notes.Where(n => !n.IsRest).ToList();
        Assert.Equal(new[] { 240, 720 }, sounding.Select(n => n.Duration).ToArray());
        Assert.True(sounding[0].TieStart);
        Assert.True(sounding[1].TieStop);
        Assert.All(notes, n => Assert.NotEqual(0, n.Duration));
    }

    [Fact]
    public async Task TripletsAreDetectedPerBeat()
    {
        var document = await ConvertAsync(Midi(
            (0, On(60)), (160, Off(60)),
            (0, On(62)), (160, Off(62)),
            (0, On(64)), (160, Off(64)),
            (0, On(65)), (480, Off(65))));

        var notes = document.AllNotes().Where(n => !n.IsRest).ToList();
        Assert.Equal(4, notes.Count);
        Assert.All(notes.Take(3), n =>
        {
            Assert.Equal(160, n.Duration);
            Assert.Equal(3, n.TupletActual);
            Assert.Equal(2, n.TupletNormal);
            Assert.Equal("eighth", n.Type);
        });
        Assert.Null(notes[3].TupletActual);
        Assert.Equal(480, notes[3].Duration);
    }

    [Fact]
    public async Task EveryMeasureIsFilledExactly()
    {
        var document = await ConvertAsync(Midi(
            (7, On(60)), (1000, Off(60)),
            (130, On(62)), (2200, Off(62))));

        foreach (var measure in document.Parts[0].Measures)
            Assert.Equal(1920, measure.Notes.Where(n => !n.IsChord).Sum(n => n.Duration));
    }

    private static async Task<MusicConverter.MusicXml.MusicXmlDocument> ConvertAsync(byte[] data)
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var path = Path.Combine(dir.Path, "q.mid");
        await File.WriteAllBytesAsync(path, data);
        return await new MidiConverter().ConvertAsync(path, null);
    }

    private static byte[] On(int pitch) => new byte[] { 0x90, (byte)pitch, 0x40 };
    private static byte[] Off(int pitch) => new byte[] { 0x80, (byte)pitch, 0x00 };

    /// <summary>Single-track format-0 file at 480 ticks per quarter; each event is (delta ticks, message bytes).</summary>
    private static byte[] Midi(params (int Delta, byte[] Message)[] events)
    {
        var track = new List<byte>();
        foreach (var (delta, message) in events)
        {
            var groups = new Stack<byte>();
            groups.Push((byte)(delta & 0x7F));
            for (var rest = delta >> 7; rest > 0; rest >>= 7) groups.Push((byte)((rest & 0x7F) | 0x80));
            track.AddRange(groups);
            track.AddRange(message);
        }
        track.AddRange(new byte[] { 0, 0xFF, 0x2F, 0 });

        var file = new List<byte>(Convert.FromHexString("4D546864000000060000000101E0"));
        file.AddRange(new byte[] { (byte)'M', (byte)'T', (byte)'r', (byte)'k', 0, 0, (byte)(track.Count >> 8), (byte)track.Count });
        file.AddRange(track);
        return file.ToArray();
    }

    [Fact]
    public void RegistryDiscoversMidiConverterForTheFileDialogAndConversion()
    {
        var registry = new ConverterRegistry();
        registry.DiscoverConverters();

        Assert.IsType<MidiConverter>(registry.FindConverter("score.mid"));
        Assert.Contains("*.mid", registry.FileDialogFilter);
        Assert.Contains("*.midi", registry.FileDialogFilter);
    }

    [Fact]
    public async Task InvalidMidiIsReportedAsParseErrorWithFileContext()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "invalid.mid");
        Directory.CreateDirectory(dir.Path);
        await File.WriteAllBytesAsync(path, new byte[] { 0, 1, 2, 3 });
        var logger = new InMemoryLogger();

        var exception = await Assert.ThrowsAsync<ConversionException>(
            () => new MidiConverter(logger).ConvertAsync(path, null));

        Assert.Equal(ConversionErrorCategory.ParseError, exception.Category);
        Assert.Equal(path, exception.FilePath);
        Assert.Equal("MIDI", exception.ConverterName);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error);
    }
}
