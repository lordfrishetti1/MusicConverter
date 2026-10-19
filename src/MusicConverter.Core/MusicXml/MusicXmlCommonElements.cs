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
        note.Articulations.Count > 0 || note.Ornaments.Count > 0;

    /// <summary>Adds slur, ornament, articulation and fermata children (ties are written by the caller).</summary>
    public static void AddNotationExtras(XElement notations, MusicXmlNote note)
    {
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
