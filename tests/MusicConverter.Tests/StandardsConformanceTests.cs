using MusicConverter.Abstractions;
using MusicConverter.Converters;
using Xunit;

namespace MusicConverter.Tests;

public class StandardsConformanceTests
{
    private static async Task<MusicXml.MusicXmlDocument> Run(IMusicFormatConverter converter, string fileName, string text)
    {
        using var dir = new TempDir();
        return await converter.ConvertAsync(dir.Write(fileName, text), new ListProgress());
    }

    [Fact]
    public async Task Abc_MultiMeasureRestEmitsOneMeasurePerBar()
    {
        var doc = await Run(new AbcConverter(), "t.abc", "X:1\nT:t\nM:4/4\nL:1/4\nK:C\nZ3|C4|\n");
        var measures = doc.Parts[0].Measures;
        Assert.Equal(4, measures.Count);
        Assert.All(measures.Take(3), m => Assert.True(Assert.Single(m.Notes).IsRest));
    }

    [Fact]
    public async Task Abc_ExplicitKeyAccidentals()
    {
        var doc = await Run(new AbcConverter(), "t.abc", "X:1\nT:t\nM:4/4\nL:1/4\nK:D exp _b _e ^f\nB E F D|\n");
        Assert.Equal(new[] { -1, -1, 1, 0 }, doc.AllNotes().Select(n => n.Pitch!.Alter).ToArray());
    }

    [Fact]
    public async Task Abc_QuintupletInCompoundTimeUsesThree()
    {
        var doc = await Run(new AbcConverter(), "t.abc", "X:1\nT:t\nM:6/8\nL:1/8\nK:C\n(5CDEFG z|\n");
        var tuplet = doc.AllNotes().First();
        Assert.Equal(5, tuplet.TupletActual);
        Assert.Equal(3, tuplet.TupletNormal);
    }

    [Fact]
    public async Task Nwc_ClefOctaveShiftTransposesPitches()
    {
        const string head = "!NoteWorthyComposer(2.0)\n|AddStaff\n";
        var plain = await Run(new NwcTxtConverter(), "a.nwctxt", head + "|Clef|Type:Treble\n|Note|Dur:4th|Pos:0\n");
        var down = await Run(new NwcTxtConverter(), "b.nwctxt", head + "|Clef|Type:Treble|OctaveShift:Octave Down\n|Note|Dur:4th|Pos:0\n");
        Assert.Equal(plain.AllNotes().First().Pitch!.Octave - 1, down.AllNotes().First().Pitch!.Octave);
    }

    [Fact]
    public async Task Mei_ChildMeterSigKeySigAndAccid()
    {
        const string mei = """
            <mei xmlns="http://www.music-encoding.org/ns/mei"><music><body><mdiv><score>
            <scoreDef><meterSig count="3" unit="4"/><keySig sig="1f"/>
              <staffGrp><staffDef n="1" lines="5" clef.shape="G" clef.line="2"/></staffGrp></scoreDef>
            <section><measure n="1"><staff n="1"><layer n="1">
              <note xml:id="a" pname="f" oct="4" dur="4"><accid accid="s"/></note>
              <note pname="b" oct="4" dur="4"/>
              <note pname="c" oct="5" dur="4"/>
            </layer></staff></measure></section></score></mdiv></body></music></mei>
            """;
        var doc = await Run(new MeiConverter(), "t.mei", mei);
        var m = doc.Parts[0].Measures[0];
        Assert.Equal(3, m.Attributes!.Beats);
        Assert.Equal(-1, m.Attributes.Fifths);
        Assert.Equal(1, m.Notes[0].Pitch!.Alter);
        Assert.Equal(-1, m.Notes[1].Pitch!.Alter);
    }

    [Fact]
    public async Task MusiXTeX_NoteGlyphWithoutPitchIsNotFatal()
    {
        const string tex = "\\startpiece\\notes\\uptext{\\metron{\\qu}{60}}\\enotes\n\\notes\\qu c\\enotes\\bar\n\\endpiece\n";
        var doc = await Run(new MusiXTeXConverter(), "t.tex", tex);
        var note = Assert.Single(doc.AllNotes());
        Assert.Equal('C', note.Pitch!.Step);
        Assert.Equal(4, note.Pitch.Octave);
    }
}
