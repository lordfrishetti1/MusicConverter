using System.Xml.Linq;

namespace MusicConverter.MusicXml.V3_1;

/// <summary>MusicXML 3.1 score-partwise writer.</summary>
public sealed class MusicXmlV31Writer : IMusicXmlVersionWriter
{
    public const string VersionString = "3.1";

    public string Version => VersionString;

    public IReadOnlyDictionary<string, string> DeprecatedFeatures { get; } = new Dictionary<string, string>
    {
        ["legacy.encoder-element"] = "The <encoder> element is deprecated; use <software> or <supports> instead.",
        ["legacy.voice-less-notes"] = "Notes without an explicit <voice> element are deprecated in multi-voice scores."
    };

    public XDocument Write(MusicXmlDocument document)
    {
        var root = new XElement("score-partwise", new XAttribute("version", VersionString));

        if (!string.IsNullOrWhiteSpace(document.Title))
            root.Add(new XElement("work", new XElement("work-title", document.Title)));

        var identification = new XElement("identification");
        if (!string.IsNullOrWhiteSpace(document.Composer))
            identification.Add(new XElement("creator", new XAttribute("type", "composer"), document.Composer));
        identification.Add(new XElement("encoding", new XElement("software", document.Software)));
        root.Add(identification);

        var partList = new XElement("part-list");
        foreach (var part in document.Parts)
        {
            partList.Add(new XElement("score-part", new XAttribute("id", part.Id),
                new XElement("part-name", part.Name)));
        }
        root.Add(partList);

        foreach (var part in document.Parts)
        {
            var partElement = new XElement("part", new XAttribute("id", part.Id));
            foreach (var measure in part.Measures)
                partElement.Add(WriteMeasure(measure));
            root.Add(partElement);
        }

        return new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("score-partwise", "-//Recordare//DTD MusicXML 3.1 Partwise//EN",
                "http://www.musicxml.org/dtds/partwise.dtd", null),
            root);
    }

    private static XElement WriteMeasure(MusicXmlMeasure measure)
    {
        var element = new XElement("measure", new XAttribute("number", measure.Number));

        if (measure.Attributes is { } a)
        {
            var attributes = new XElement("attributes");
            if (a.Divisions is int divisions) attributes.Add(new XElement("divisions", divisions));
            if (a.Fifths is int fifths) attributes.Add(new XElement("key", new XElement("fifths", fifths)));
            if (a.Beats is int beats && a.BeatType is int beatType)
                attributes.Add(new XElement("time", new XElement("beats", beats), new XElement("beat-type", beatType)));
            if (a.ClefSign is { } sign)
            {
                var clef = new XElement("clef", new XElement("sign", sign));
                if (a.ClefLine is int line) clef.Add(new XElement("line", line));
                attributes.Add(clef);
            }
            if (attributes.HasElements) element.Add(attributes);
        }

        if (MusicXmlCommonElements.LeftBarline(measure) is { } leftBarline) element.Add(leftBarline);

        foreach (var note in measure.Notes)
        {
            if (note.BackupBefore > 0)
                element.Add(new XElement("backup", new XElement("duration", note.BackupBefore)));
            if (!note.IsChord)
            {
                foreach (var direction in note.Directions)
                    if (MusicXmlCommonElements.Direction(direction, note.Voice) is { } directionElement) element.Add(directionElement);
            }
            element.Add(WriteNote(note));
        }

        if (MusicXmlCommonElements.RightBarline(measure) is { } rightBarline) element.Add(rightBarline);
        return element;
    }

    private static XElement WriteNote(MusicXmlNote note)
    {
        var element = new XElement("note");
        if (note.IsGrace)
            element.Add(note.GraceSlash ? new XElement("grace", new XAttribute("slash", "yes")) : new XElement("grace"));
        if (note.IsChord) element.Add(new XElement("chord"));

        if (note.IsRest || note.Pitch is null)
        {
            element.Add(new XElement("rest"));
        }
        else
        {
            var pitch = new XElement("pitch", new XElement("step", note.Pitch.Step.ToString()));
            if (note.Pitch.Alter != 0) pitch.Add(new XElement("alter", note.Pitch.Alter));
            pitch.Add(new XElement("octave", note.Pitch.Octave));
            element.Add(pitch);
        }

        if (!note.IsGrace) element.Add(new XElement("duration", note.Duration));
        if (note.TieStop) element.Add(new XElement("tie", new XAttribute("type", "stop")));
        if (note.TieStart) element.Add(new XElement("tie", new XAttribute("type", "start")));
        element.Add(new XElement("voice", note.Voice));
        element.Add(new XElement("type", note.Type));
        for (var i = 0; i < note.Dots; i++) element.Add(new XElement("dot"));

        if (note.TupletActual is int actual && note.TupletNormal is int normal)
        {
            element.Add(new XElement("time-modification",
                new XElement("actual-notes", actual), new XElement("normal-notes", normal)));
        }

        if (MusicXmlCommonElements.HasNotations(note))
        {
            var notations = new XElement("notations");
            if (note.TieStop) notations.Add(new XElement("tied", new XAttribute("type", "stop")));
            if (note.TieStart) notations.Add(new XElement("tied", new XAttribute("type", "start")));
            MusicXmlCommonElements.AddNotationExtras(notations, note);
            element.Add(notations);
        }

        foreach (var lyric in note.Lyrics)
        {
            element.Add(new XElement("lyric", new XAttribute("number", lyric.Number),
                new XElement("syllabic", lyric.Syllabic), new XElement("text", lyric.Text)));
        }

        return element;
    }
}
