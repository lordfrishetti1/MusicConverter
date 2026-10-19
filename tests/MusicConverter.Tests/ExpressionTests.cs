using System.Xml.Linq;
using MusicConverter.Converters;
using MusicConverter.MusicXml;
using Xunit;

namespace MusicConverter.Tests;

public class ExpressionTests
{
    [Fact]
    public async Task HumdrumArticulationsGraceDynamicsAndTempo()
    {
        using var dir = new TempDir();
        var path = dir.Write("e.krn",
            "**kern\t**dynam\n*clefG2\t*\n*M2/4\t*\n*MM96\t*\n=1\t=1\n" +
            "8qd\t.\n4c'\tmf\n4d;\t<\n=2\t=2\n4e(t\t.\n4f)\t[\n*-\t*-\n");
        var doc = await new HumdrumConverter().ConvertAsync(path, null);
        var notes = doc.AllNotes().ToList();
        Assert.True(notes[0].IsGrace);
        Assert.True(notes[0].GraceSlash);
        Assert.Contains("staccato", notes[1].Articulations);
        Assert.Contains(notes[1].Directions, d => d.Kind == DirectionKinds.Tempo && d.Value == "96");
        Assert.Contains(notes[1].Directions, d => d.Kind == DirectionKinds.Dynamics && d.Value == "mf");
        Assert.True(notes[2].Fermata);
        Assert.Contains(notes[2].Directions, d => d.Kind == DirectionKinds.Wedge && d.Value == "crescendo");
        Assert.True(notes[3].SlurStart);
        Assert.Contains("trill-mark", notes[3].Ornaments);
        Assert.True(notes[4].SlurStop);
        Assert.Contains(notes[4].Directions, d => d.Kind == DirectionKinds.Wedge && d.Value == "stop");
        Assert.Equal(2, doc.Parts[0].Measures.Count);
    }

    [Fact]
    public async Task MeiControlEventsAndArticulations()
    {
        using var dir = new TempDir();
        var path = dir.Write("e.mei", @"<?xml version=""1.0""?>
<mei xmlns=""http://www.music-encoding.org/ns/mei"" meiversion=""5.0""><music><body><mdiv><score>
<scoreDef meter.count=""4"" meter.unit=""4"" key.sig=""0""><staffGrp><staffDef n=""1"" clef.shape=""G"" clef.line=""2""/></staffGrp></scoreDef>
<section><measure n=""1""><staff n=""1""><layer n=""1"">
<note xml:id=""a"" pname=""c"" oct=""4"" dur=""4"" artic=""stacc""/>
<note xml:id=""b"" pname=""d"" oct=""4"" dur=""4""/>
<note grace=""acc"" pname=""f"" oct=""4"" dur=""8""/>
<note xml:id=""c"" pname=""e"" oct=""4"" dur=""2""/>
</layer></staff>
<dynam staff=""1"" tstamp=""1"">mf</dynam>
<hairpin staff=""1"" form=""cres"" startid=""#a"" endid=""#b""/>
<fermata staff=""1"" tstamp=""3""/>
<trill staff=""1"" startid=""#b""/>
<tempo staff=""1"" tstamp=""1"" midi.bpm=""100"">Allegro</tempo>
</measure></section></score></mdiv></body></music></mei>");
        var doc = await new MeiConverter().ConvertAsync(path, null);
        var notes = doc.AllNotes().ToList();
        Assert.Contains("staccato", notes[0].Articulations);
        Assert.Contains(notes[0].Directions, d => d.Kind == DirectionKinds.Dynamics && d.Value == "mf");
        Assert.Contains(notes[0].Directions, d => d.Kind == DirectionKinds.Wedge && d.Value == "crescendo");
        Assert.Contains(notes[0].Directions, d => d.Kind == DirectionKinds.Tempo && d.Value == "100");
        Assert.Contains(notes[1].Directions, d => d.Kind == DirectionKinds.Wedge && d.Value == "stop");
        Assert.Contains("trill-mark", notes[1].Ornaments);
        Assert.True(notes[2].IsGrace);
        Assert.True(notes[2].GraceSlash);
        Assert.True(notes[3].Fermata);
        Assert.Single(doc.Parts[0].Measures);
    }

    [Theory]
    [InlineData("3.1")]
    [InlineData("4.0")]
    public async Task SerializedXmlHasOrderedDirectionsGraceAndNotations(string version)
    {
        using var dir = new TempDir();
        var path = dir.Write("s.abc", "X:1\nM:2/4\nL:1/4\nQ:1/4=90\nK:C\n!f!{/g}!fermata!.!trill!C D|\n");
        var doc = await new AbcConverter().ConvertAsync(path, null);
        var root = XDocument.Parse(new MusicXmlSerializer().Serialize(doc, version)).Root!;
        var measure = root.Descendants("measure").First();
        var children = measure.Elements().Select(e => e.Name.LocalName).ToList();

        // the grace note precedes its principal note; directions are written right before the note they belong to
        Assert.Equal(new[] { "attributes", "note", "direction", "direction", "note", "note" }, children.ToArray());
        var grace = measure.Elements("note").First();
        Assert.NotNull(grace.Element("grace"));
        Assert.Equal("yes", (string?)grace.Element("grace")!.Attribute("slash"));
        Assert.Null(grace.Element("duration"));

        var main = measure.Elements("note").ElementAt(1);
        var notations = main.Element("notations")!;
        Assert.NotNull(notations.Element("fermata"));
        Assert.NotNull(notations.Element("ornaments")!.Element("trill-mark"));
        Assert.NotNull(notations.Element("articulations")!.Element("staccato"));
        Assert.Equal("90", (string?)measure.Descendants("per-minute").Single());
        Assert.NotNull(measure.Descendants("f").FirstOrDefault());
    }
}
