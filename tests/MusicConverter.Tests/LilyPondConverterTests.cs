using MusicConverter.Abstractions;
using MusicConverter.Converters;
using MusicConverter.Logging;
using MusicConverter.MusicXml;
using Xunit;

namespace MusicConverter.Tests;

public class LilyPondConverterTests
{
    private static async Task<MusicXml.MusicXmlDocument> ConvertText(string ly, InMemoryLogger? logger = null)
    {
        using var dir = new TempDir();
        var path = dir.Write("t.ly", ly);
        return await new LilyPondConverter(logger).ConvertAsync(path, null);
    }

    private static string[] Notes(MusicXml.MusicXmlDocument doc) => doc.AllNotes().Select(n => n.Describe()).ToArray();

    [Fact]
    public async Task ConvertsSampleFile()
    {
        var progress = new ListProgress();
        var doc = await new LilyPondConverter().ConvertAsync(Samples.Path("twinkle.ly"), progress);

        Assert.Equal("Twinkle, Twinkle, Little Star", doc.Title);
        Assert.Equal("Traditional", doc.Composer);
        var part = Assert.Single(doc.Parts);
        Assert.Equal("Melody", part.Name);
        Assert.Equal(5, part.Measures.Count);
        Assert.Equal("C4:480", part.Measures[0].Notes[0].Describe());
        Assert.Equal("G4:480", part.Measures[0].Notes[2].Describe());
        Assert.Equal("G4:960", part.Measures[1].Notes[2].Describe());

        Assert.True(part.Measures[3].Notes[2].TieStart);
        var m5 = part.Measures[4].Notes;
        Assert.True(m5[0].TieStop);
        Assert.Equal(new[] { "C4:480", "E4:480", "G4:480" }, m5.Skip(1).Take(3).Select(n => n.Describe()).ToArray());
        Assert.Equal(2, m5.Count(n => n.IsChord));
        Assert.True(m5[4].IsRest);
        Assert.Equal(new[] { "D4:240", "E4:240" }, m5.Skip(5).Select(n => n.Describe()).ToArray());
        Assert.Contains(progress.Reports, r => r.Stage == ConversionStage.Parsing);
    }

    [Fact]
    public async Task AbsoluteOctavesAndAccidentals()
    {
        var doc = await ConvertText("{ cis''2 bes,2 c'4. d'8 r2 }");
        Assert.Equal(new[] { "C+15:960", "B-12:960", "C4:720", "D4:240", "r960" }, Notes(doc));
    }

    [Fact]
    public async Task RelativeOctaveSelection()
    {
        var doc = await ConvertText("\\relative c'' { c b a g, c' }");
        Assert.Equal(new[] { "C5:480", "B4:480", "A4:480", "G3:480", "C5:480" }, Notes(doc));
    }

    [Theory]
    [InlineData("\\key g \\major c'1", 1)]
    [InlineData("\\key es \\major c'1", -3)]
    [InlineData("\\key a \\minor c'1", 0)]
    [InlineData("\\key d \\dorian c'1", 0)]
    public async Task KeySignatures(string body, int fifths)
    {
        var doc = await ConvertText("{ " + body + " }");
        Assert.Equal(fifths, doc.Parts[0].Measures[0].Attributes!.Fifths);
    }

    [Theory]
    [InlineData("\\tuplet 3/2 { c'8 d' e' } f'4")]
    [InlineData("\\times 2/3 { c'8 d' e' } f'4")]
    public async Task Tuplets(string body)
    {
        var doc = await ConvertText("{ \\time 2/4 " + body + " }");
        var part = Assert.Single(doc.Parts);
        Assert.Single(part.Measures);
        var tuplet = part.Measures[0].Notes.Take(3).ToList();
        Assert.All(tuplet, n => Assert.Equal((3, 2, 160), (n.TupletActual, n.TupletNormal, n.Duration)));
        Assert.All(tuplet, n => Assert.Equal("eighth", n.Type));
    }

    [Fact]
    public async Task VariablesAndRepeats()
    {
        var doc = await ConvertText("melody = { c'1 }\n\\score { \\new Staff { \\repeat volta 2 \\melody } }");
        Assert.Single(doc.Parts[0].Measures);
        Assert.True(doc.Parts[0].Measures[0].RepeatStart);
        Assert.True(doc.Parts[0].Measures[0].RepeatEnd);
    }

    [Fact]
    public async Task VoltaAlternativesProduceEndings()
    {
        var doc = await ConvertText("{ \\repeat volta 2 { c'1 } \\alternative { { d'1 } { e'1 } } }");
        var m = doc.Parts[0].Measures;
        Assert.Equal(3, m.Count);
        Assert.True(m[0].RepeatStart);
        Assert.True(m[1].EndingStart);
        Assert.True(m[1].RepeatEnd);
        Assert.True(m[2].EndingStart);
    }

    [Fact]
    public async Task PianoStaffProducesTwoParts()
    {
        var doc = await ConvertText("\\score { \\new PianoStaff << \\new Staff { c'1 } \\new Staff { \\clef bass c1 } >> }");
        Assert.Equal(2, doc.Parts.Count);
        Assert.Equal("F", doc.Parts[1].Measures[0].Attributes!.ClefSign);
    }

    [Fact]
    public async Task PartialCreatesShortFirstMeasure()
    {
        var doc = await ConvertText("{ \\partial 4 c'4 d'1 }");
        Assert.Equal(new[] { 1, 1 }, doc.Parts[0].Measures.Select(m => m.Notes.Count).ToArray());
        Assert.Equal(480, doc.Parts[0].Measures[0].Notes[0].Duration);
    }

    [Fact]
    public async Task WarnsAboutUnsupportedCommands()
    {
        var logger = new InMemoryLogger();
        var doc = await ConvertText("{ \\foo c'1 \\addlyrics { la } }", logger);
        Assert.Contains(doc.Warnings, w => w.Contains("\\foo"));
        Assert.True(logger.Contains(LogLevel.Warning, "Unsupported LilyPond command"));
        Assert.Single(doc.AllNotes());
    }

    [Fact]
    public async Task ConvertsOrnamentsDynamicsTempoAndGraceNotes()
    {
        var doc = await ConvertText("{ \\tempo \"Allegro\" 4=120 c'4-. \\p \\< d'4->\\! e'4\\fermata \\acciaccatura f'8 g'4\\trill }");
        var notes = doc.AllNotes().ToList();
        Assert.Contains(notes[0].Directions, d => d.Kind == DirectionKinds.Tempo && d.Value == "120");
        Assert.Contains(notes[0].Directions, d => d.Kind == DirectionKinds.Words && d.Value == "Allegro");
        Assert.Contains("staccato", notes[0].Articulations);
        Assert.Contains(notes[0].Directions, d => d.Kind == DirectionKinds.Dynamics && d.Value == "p");
        Assert.Contains(notes[0].Directions, d => d.Kind == DirectionKinds.Wedge && d.Value == "crescendo");
        Assert.Contains("accent", notes[1].Articulations);
        Assert.Contains(notes[1].Directions, d => d.Kind == DirectionKinds.Wedge && d.Value == "stop");
        Assert.True(notes[2].Fermata);
        Assert.True(notes[3].IsGrace);
        Assert.True(notes[3].GraceSlash);
        Assert.Contains("trill-mark", notes[4].Ornaments);
        Assert.Equal(1, doc.Parts[0].Measures.Count);
    }

    [Fact]
    public async Task UnbalancedBracesAreParseError()
    {
        using var dir = new TempDir();
        var path = dir.Write("bad.ly", "\\relative c' {\n c4 d\n");
        var ex = await Assert.ThrowsAsync<ConversionException>(() => new LilyPondConverter().ConvertAsync(path, null));
        Assert.Equal(ConversionErrorCategory.ParseError, ex.Category);
        Assert.Equal("LilyPond", ex.ConverterName);
        Assert.Equal(1, ex.Line);
    }

    [Fact]
    public async Task InvalidDurationIsParseError()
    {
        using var dir = new TempDir();
        var path = dir.Write("bad.ly", "{ c'3 }");
        var ex = await Assert.ThrowsAsync<ConversionException>(() => new LilyPondConverter().ConvertAsync(path, null));
        Assert.Equal(ConversionErrorCategory.ParseError, ex.Category);
    }

    [Fact]
    public async Task MissingEnglishIncludeSelectsEnglishNoteNames()
    {
        using var dir = new TempDir();
        var path = dir.Write("en.ly", "\\include \"english.ly\"\n\\relative c'' { \\key a \\major gs4 es bf cs }");
        var doc = await new LilyPondConverter().ConvertAsync(path, null);
        var notes = doc.AllNotes().ToArray();
        Assert.Equal(new[] { 1, 1, -1, 1 }, notes.Select(n => n.Pitch!.Alter).ToArray());
        Assert.Equal("GEBC", string.Concat(notes.Select(n => n.Pitch!.Step)));
        Assert.Equal(3, doc.Parts[0].Measures[0].Attributes!.Fifths);
    }

    [Fact]
    public async Task SimultaneousVoicesInOneStaffAreKept()
    {
        var doc = await ConvertText("\\new Staff << \\new Voice { c''2 b'2 } \\new Voice { e'2 d'2 } >>");
        var notes = doc.Parts.Single().Measures.SelectMany(m => m.Notes).ToArray();
        Assert.Equal(4, notes.Length);
        Assert.Equal(new[] { 1, 1, 2, 2 }, notes.Select(n => n.Voice).ToArray());
        Assert.Equal(1920, notes[2].BackupBefore);
        Assert.Single(doc.Parts);
    }

    [Fact]
    public async Task PartialAppliesToEveryVoiceAndStaff()
    {
        var doc = await ConvertText(
            "\\score { \\new ChoirStaff << \\new Staff << { \\partial 4 c''4 d''1 } { e'4 f'1 } >> \\new Staff { \\clef bass g4 a1 } >> }");
        Assert.Equal(2, doc.Parts.Count);
        Assert.All(doc.Parts, p => Assert.Equal(2, p.Measures.Count));
        Assert.Equal(480, doc.Parts[0].Measures[0].Notes.Where(n => n.Voice == 1).Sum(n => n.Duration));
        Assert.Equal(480, doc.Parts[0].Measures[0].Notes.Where(n => n.Voice == 2).Sum(n => n.Duration));
    }

    [Fact]
    public async Task OnlyTheFirstBookIsConverted()
    {
        var doc = await ConvertText("\\book { \\score { { c'1 } } }\n\\book { \\score { { c'1 } } }");
        Assert.Single(doc.Parts);
        Assert.Contains(doc.Warnings, w => w.Contains("\\book"));
    }

    [Fact]
    public async Task SlursAreConverted()
    {
        var doc = await ConvertText("{ c'4( d') e'2 }");
        var notes = doc.AllNotes().ToArray();
        Assert.True(notes[0].SlurStart);
        Assert.True(notes[1].SlurStop);
        Assert.False(notes[2].SlurStart || notes[2].SlurStop);
    }

    [Fact]
    public async Task LyricsAreAlignedSkippingSlursTiesAndRests()
    {
        var doc = await ConvertText(
            "verse = \\lyricmode { Hap -- py birth -- day }\n" +
            "\\score { << \\new Voice = \"mel\" { c'4( d') e'~ e' r f'2 g'4 } \\new Lyrics \\lyricsto mel \\verse >> }");
        var notes = doc.AllNotes().ToArray();
        string Text(int i) => string.Join("|", notes[i].Lyrics.Select(l => $"{l.Number}:{l.Syllabic}:{l.Text}"));
        Assert.Equal("1:begin:Hap", Text(0));
        Assert.Equal("", Text(1));
        Assert.Equal("1:end:py", Text(2));
        Assert.Equal("", Text(3));
        Assert.Equal("", Text(4));
        Assert.Equal("1:begin:birth", Text(5));
        Assert.Equal("1:end:day", Text(6));
    }

    [Fact]
    public async Task AddLyricsVersesAreNumbered()
    {
        var doc = await ConvertText("{ c'4 d' e' f' } \\addlyrics { a b c d } \\addlyrics { w x y z }");
        var first = doc.AllNotes().First();
        Assert.Equal(new[] { 1, 2 }, first.Lyrics.Select(l => l.Number).ToArray());
    }

    [Fact]
    public async Task ResolvesIncludesRelativeToTheIncludingFile()
    {
        using var dir = new TempDir();
        dir.Write("defs.ily", "melody = { c'4 d'4 e'4 f'4 }");
        var main = dir.Write("main.ly", "\\include \"defs.ily\"\n{ \\melody }");
        var doc = await new LilyPondConverter().ConvertAsync(main, null);
        Assert.Equal(4, doc.AllNotes().Count());
    }

    [Fact]
    public async Task MissingAndCircularIncludesAreWarnings()
    {
        using var dir = new TempDir();
        dir.Write("a.ily", "\\include \"b.ily\"\nx = { d'1 }");
        dir.Write("b.ily", "\\include \"a.ily\"\n\\include \"nope.ily\"");
        var main = dir.Write("main.ly", "\\include \"a.ily\"\n{ c'1 \\x }");
        var logger = new InMemoryLogger();
        var doc = await new LilyPondConverter(logger).ConvertAsync(main, null);
        Assert.Equal(2, doc.AllNotes().Count());
        Assert.True(logger.Contains(LogLevel.Warning, "nope.ily"));
    }

    [Fact]
    public async Task ZeroLengthSkipsAndSchemeAssignmentsAreIgnored()
    {
        var doc = await ConvertText("flag = ##t\nstart = #(ly:make-moment 1 4)\n{ c'1 s1*0 d'1 }");
        Assert.Equal(2, doc.AllNotes().Count(n => !n.IsRest));
    }

    [Fact]
    public async Task UserMusicFunctionsConsumeTheirArguments()
    {
        var doc = await ConvertText(
            "dyn = #(define-music-function (parser location m) (ly:music?) #{ #m #})\n{ c'4 \\dyn d'4 e'2 }");
        Assert.Equal(3, doc.AllNotes().Count());
    }

    [Fact]
    public async Task RepeatUnfoldResolvesRelativePitchesOnce()
    {
        var doc = await ConvertText("\\relative c' { \\repeat unfold 2 { c4 d } e4 }");
        Assert.Equal(new[] { "C4", "D4", "C4", "D4", "E4" }, doc.AllNotes().Select(n => $"{n.Pitch!.Step}{n.Pitch.Octave}"));
    }

    [Fact]
    public async Task ChordRepeatKeepsRelativeReference()
    {
        var doc = await ConvertText("\\relative c' { <c e>4 q g' }");
        var last = doc.AllNotes().Last().Pitch!;
        Assert.Equal(("G", 4), (last.Step.ToString(), last.Octave));
    }

    [Fact]
    public async Task SimultaneousVoicesContinueRelativePitchSequentially()
    {
        var doc = await ConvertText("\\relative c'' { << { c4 } \\\\ { g4 } >> a }");
        var last = doc.AllNotes().Last().Pitch!;
        Assert.Equal(("A", 4), (last.Step.ToString(), last.Octave));
    }

    [Fact]
    public async Task MidiOnlyScoreIsNotConvertedAgain()
    {
        var doc = await ConvertText("\\score { \\new Staff { c'1 } \\layout { } }\n\\score { \\new Staff { c'1 } \\midi { } }");
        Assert.Single(doc.Parts);
    }

    [Fact]
    public async Task UnsupportedContextDoesNotSwallowTheFollowingStaff()
    {
        var doc = await ConvertText(
            "dyn = { s1 }\n\\score { << \\new Staff { c'1 } \\new Dynamics \\dyn \\new Staff { d'1 } >> }");
        Assert.Equal(2, doc.Parts.Count);
    }

    [Theory]
    [InlineData("a.ly", true)]
    [InlineData("a.ILY", true)]
    [InlineData("a.abc", false)]
    public void CanReadChecksExtension(string path, bool expected) =>
        Assert.Equal(expected, new LilyPondConverter().CanRead(path));
}
