using System.Globalization;
using System.Xml.Linq;

namespace MusicConverter.MusicXml;

/// <summary>Element builders for constructs whose MusicXML form is identical in the supported versions.</summary>
internal static class MusicXmlCommonElements
{
    private static readonly HashSet<string> KnownDynamics = new(StringComparer.Ordinal)
    {
        "p", "pp", "ppp", "pppp", "ppppp", "pppppp", "f", "ff", "fff", "ffff", "fffff", "ffffff",
        "mp", "mf", "sf", "sfp", "sfpp", "fp", "rf", "rfz", "sfz", "sffz", "fz", "n", "pf", "sfzp"
    };

    private static readonly Dictionary<string, double> BeatUnitQuarters = new(StringComparer.Ordinal)
    {
        ["whole"] = 4, ["half"] = 2, ["quarter"] = 1, ["eighth"] = 0.5, ["16th"] = 0.25
    };

    public static bool HasNotations(MusicXmlNote note) =>
        note.TieStart || note.TieStop || note.SlurStart || note.SlurStop || note.Fermata ||
        note.Articulations.Count > 0 || note.Ornaments.Count > 0 || note.TupletStart || note.TupletStop;

    /// <summary>Left barline carrying a forward repeat and/or the start of a volta bracket, or null.</summary>
    public static XElement? LeftBarline(MusicXmlMeasure measure)
    {
        if (!measure.RepeatStart && !(measure.EndingStart && measure.EndingNumbers != null)) return null;
        var barline = new XElement("barline", new XAttribute("location", "left"));
        if (measure.EndingStart && measure.EndingNumbers != null)
            barline.Add(new XElement("ending", new XAttribute("number", measure.EndingNumbers), new XAttribute("type", "start")));
        if (measure.RepeatStart) barline.Add(new XElement("repeat", new XAttribute("direction", "forward")));
        return barline;
    }

    /// <summary>Right barline carrying a backward repeat and/or the end of a volta bracket, or null.</summary>
    public static XElement? RightBarline(MusicXmlMeasure measure)
    {
        if (!measure.RepeatEnd && !(measure.EndingStop && measure.EndingNumbers != null)) return null;
        var barline = new XElement("barline", new XAttribute("location", "right"));
        if (measure.EndingStop && measure.EndingNumbers != null)
            barline.Add(new XElement("ending", new XAttribute("number", measure.EndingNumbers),
                new XAttribute("type", measure.RepeatEnd ? "stop" : "discontinue")));
        if (measure.RepeatEnd)
        {
            var repeat = new XElement("repeat", new XAttribute("direction", "backward"));
            if (measure.RepeatTimes > 2) repeat.Add(new XAttribute("times", measure.RepeatTimes));
            barline.Add(repeat);
        }
        return barline;
    }

    /// <summary>Adds tuplet, slur, ornament, articulation and fermata children (ties are written by the caller).</summary>
    public static void AddNotationExtras(XElement notations, MusicXmlNote note)
    {
        if (note.TupletStop) notations.Add(new XElement("tuplet", new XAttribute("type", "stop"), new XAttribute("number", 1)));
        if (note.TupletStart)
            notations.Add(new XElement("tuplet", new XAttribute("type", "start"), new XAttribute("number", 1), new XAttribute("bracket", "yes")));
        if (note.SlurStop) notations.Add(new XElement("slur", new XAttribute("type", "stop"), new XAttribute("number", 1)));
        if (note.SlurStart) notations.Add(new XElement("slur", new XAttribute("type", "start"), new XAttribute("number", 1)));
        if (note.Ornaments.Count > 0)
            notations.Add(new XElement("ornaments", note.Ornaments.Select(o => new XElement(o))));
        if (note.Articulations.Count > 0)
            notations.Add(new XElement("articulations", note.Articulations.Select(a => new XElement(a))));
        if (note.Fermata) notations.Add(new XElement("fermata"));
    }

    public static XElement? Direction(MusicXmlDirection direction, int voice)
    {
        XElement type;
        XElement? sound = null;
        var placement = "above";

        switch (direction.Kind)
        {
            case DirectionKinds.Dynamics:
                placement = "below";
                type = new XElement("dynamics", KnownDynamics.Contains(direction.Value)
                    ? new XElement(direction.Value)
                    : new XElement("other-dynamics", direction.Value));
                break;
            case DirectionKinds.Wedge:
                placement = "below";
                type = new XElement("wedge", new XAttribute("type", direction.Value), new XAttribute("number", 1));
                break;
            case DirectionKinds.Tempo:
            {
                var unit = direction.Unit ?? "quarter";
                if (!BeatUnitQuarters.TryGetValue(unit, out var quarters)) { unit = "quarter"; quarters = 1; }
                if (!double.TryParse(direction.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var perMinute) || perMinute <= 0)
                    return null;
                type = new XElement("metronome",
                    new XElement("beat-unit", unit),
                    new XElement("per-minute", perMinute.ToString("0.##", CultureInfo.InvariantCulture)));
                sound = new XElement("sound", new XAttribute("tempo",
                    (perMinute * quarters).ToString("0.##", CultureInfo.InvariantCulture)));
                break;
            }
            case DirectionKinds.Words:
                if (string.IsNullOrWhiteSpace(direction.Value)) return null;
                type = new XElement("words", direction.Value);
                break;
            case DirectionKinds.Pedal:
                placement = "below";
                type = new XElement("pedal", new XAttribute("type", direction.Value), new XAttribute("line", "yes"));
                break;
            default:
                return null;
        }

        var element = new XElement("direction", new XAttribute("placement", placement),
            new XElement("direction-type", type), new XElement("voice", voice));
        if (sound != null) element.Add(sound);
        return element;
    }
}
