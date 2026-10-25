#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Pure;

/// <summary>
/// How an agent's ghost looks in Lattice's drawing beyond its tint: a preset (plan, the default; solid; wire; pulse;
/// quiet: no markers), x-ray (see-through walls, frames and terrain: on by default at full strength out to 120 m, its
/// strength, range and, with Lattice 0.11, the hidden part's own opacity the caller's), each marker on or off (ports, open ends, flow arrows, waits links; all shown but
/// on quiet), whether its plan had problems (a red pulse), and any Lattice style on top (style, the whole Lattice
/// Style as JSON). Kept on the ghost as one attachment (Encoded), so every StationGod viewer draws it so; StationGod's
/// own board drawing shows tint and see-through only. Options never combine silently: x-ray strength or range with
/// x-ray off is refused (Change).
/// </summary>
internal sealed class GhostLook
{
    internal const string Plan = "plan";

    internal const string Quiet = "quiet";

    internal static readonly string[] Presets = { Plan, "solid", "wire", "pulse", Quiet };

    internal static readonly string[] Markers = { "ports", "open_ends", "flow", "waits" };

    /// <summary>The strength and range of x-ray when the caller names none: the strongest Lattice draws today.</summary>
    internal const double DefaultStrength = 1.0;

    internal const double DefaultRange = 120.0;

    internal const double MaximumRange = 1000.0;

    internal static readonly GhostLook Plain = new GhostLook(Plan, true, DefaultStrength, DefaultRange, null,
        new Dictionary<string, bool>(), false, null);

    private readonly Dictionary<string, bool> _markers;

    private GhostLook(string preset, bool xray, double strength, double range, double? opacity,
        Dictionary<string, bool> markers, bool problem, JObject? style)
    {
        Preset = preset;
        XRay = xray;
        Strength = strength;
        Range = range;
        Opacity = opacity;
        _markers = markers;
        Problem = problem;
        Style = style;
    }

    /// <summary>One of Presets.</summary>
    internal string Preset { get; }

    /// <summary>Shows through walls, frames and terrain (the default).</summary>
    internal bool XRay { get; }

    /// <summary>X-ray strength, 0 to 1.</summary>
    internal double Strength { get; }

    /// <summary>How far x-ray reaches, in metres.</summary>
    internal double Range { get; }

    /// <summary>The hidden part's own opacity, 0 to 1 (Lattice 0.11); null draws it from the fill as Lattice does.</summary>
    internal double? Opacity { get; }

    /// <summary>Its plan had problems (it would not build as placed): drawn with a red pulse.</summary>
    internal bool Problem { get; }

    /// <summary>Lattice style properties on top of the rest (the Style JSON of catalogue topic lattice); null for none.</summary>
    internal JObject? Style { get; }

    /// <summary>Whether a marker (one of Markers) is drawn: as set, else shown unless the preset is quiet.</summary>
    internal bool Shows(string marker) => _markers.TryGetValue(marker, out bool shown) ? shown : Preset != Quiet;

    internal bool IsPlain => Encoded() == null;

    internal GhostLook WithProblem(bool problem) => new GhostLook(Preset, XRay, Strength, Range, Opacity, _markers, problem,
        Style);

    /// <summary>A preset by name, any case; null for a name that is none.</summary>
    internal static string? PresetOf(string? given)
    {
        string? name = given?.Trim().ToLowerInvariant();
        return name != null && Array.IndexOf(Presets, name) >= 0 ? name : null;
    }

    /// <summary>
    /// This look with what a call gives changed and the rest kept (show ghost places from Plain; write ghost restyles a
    /// ghost's own look). Null with the problems when an option is wrong or two contradict each other.
    /// </summary>
    internal GhostLook? Change(GhostLookChange change, List<OptionProblem> problems)
    {
        int before = problems.Count;
        string preset = Preset;
        if (change.Look != null)
        {
            if (PresetOf(change.Look) is { } named)
            {
                preset = named;
            }
            else
            {
                problems.Add(new OptionProblem("look", "is plan, solid, wire, pulse or quiet",
                    NearestNames.Of(change.Look, Presets)));
            }
        }

        bool xray = change.XRay ?? XRay;
        if (change.XRay == false && (change.Strength != null || change.Range != null || change.Opacity != null))
        {
            problems.Add(new OptionProblem(
                change.Strength != null ? "xray_strength" : change.Range != null ? "xray_range" : "xray_opacity",
                "sets x-ray, which xray false turns off: leave one out"));
        }

        double? opacity = change.Opacity ?? Opacity;
        if (change.Opacity is { } o && (double.IsNaN(o) || o < 0 || o > 1))
        {
            problems.Add(new OptionProblem("xray_opacity", "is from 0 to 1"));
        }

        double strength = change.Strength ?? Strength;
        if (change.Strength is { } s && (double.IsNaN(s) || s < 0 || s > 1))
        {
            problems.Add(new OptionProblem("xray_strength", "is from 0 to 1"));
        }

        double range = change.Range ?? Range;
        if (change.Range is { } r && (double.IsNaN(r) || r <= 0 || r > MaximumRange))
        {
            problems.Add(new OptionProblem("xray_range", $"is metres above 0, at most {MaximumRange:0}"));
        }

        Dictionary<string, bool> markers = new Dictionary<string, bool>(_markers, StringComparer.Ordinal);
        if (change.Look != null && change.Markers == null)
        {
            // A new preset decides the markers it does not set itself.
            markers.Clear();
        }

        if (change.Markers is { } given)
        {
            ReadMarkers(given, markers, problems);
        }

        JObject? style = Style;
        if (change.Style is { } added)
        {
            // A given style replaces the ghost's own; {} clears it.
            if (added is JObject properties)
            {
                style = properties.Count > 0 ? (JObject)properties.DeepClone() : null;
            }
            else
            {
                problems.Add(new OptionProblem("style",
                    "is an object of Lattice style properties (help topic lattice); {} clears it"));
            }
        }

        return problems.Count == before
            ? new GhostLook(preset, xray, Math.Round(strength, 2), Math.Round(range, 1),
                opacity is { } own ? Math.Round(own, 2) : (double?)null, markers, Problem, style)
            : null;
    }

    // markers: true / false for all four, or {ports, open_ends, flow, waits} each true or false.
    private static void ReadMarkers(JToken given, Dictionary<string, bool> markers, List<OptionProblem> problems)
    {
        if (given.Type == JTokenType.Boolean)
        {
            foreach (string marker in Markers)
            {
                markers[marker] = (bool)given;
            }

            return;
        }

        if (!(given is JObject named))
        {
            problems.Add(new OptionProblem("markers", "is true, false, or {ports, open_ends, flow, waits} each true or false"));
            return;
        }

        foreach (KeyValuePair<string, JToken?> marker in named)
        {
            if (Array.IndexOf(Markers, marker.Key) < 0)
            {
                problems.Add(new OptionProblem("markers." + marker.Key, "is not a marker: ports, open_ends, flow or waits",
                    NearestNames.Of(marker.Key, Markers)));
            }
            else if (marker.Value?.Type == JTokenType.Boolean)
            {
                markers[marker.Key] = (bool)marker.Value;
            }
            else
            {
                problems.Add(new OptionProblem("markers." + marker.Key, "is true or false"));
            }
        }
    }

    /// <summary>
    /// The look as the ghost keeps it: JSON of what differs from Plain, keys in a fixed order so equal looks are equal
    /// text; null for Plain.
    /// </summary>
    internal string? Encoded()
    {
        JObject encoded = new JObject();
        if (Preset != Plan)
        {
            encoded["look"] = Preset;
        }

        if (!XRay)
        {
            encoded["xray"] = false;
        }
        else if (Strength != DefaultStrength || Range != DefaultRange || Opacity != null)
        {
            JObject xray = new JObject { ["strength"] = Strength, ["range"] = Range };
            if (Opacity is { } opacity)
            {
                xray["opacity"] = opacity;
            }

            encoded["xray"] = xray;
        }

        JObject markers = new JObject();
        foreach (string marker in Markers)
        {
            if (_markers.TryGetValue(marker, out bool shown) && shown != (Preset != Quiet))
            {
                markers[marker] = shown;
            }
        }

        if (markers.Count > 0)
        {
            encoded["markers"] = markers;
        }

        if (Problem)
        {
            encoded["problem"] = true;
        }

        if (Style != null)
        {
            encoded["style"] = Sorted(Style);
        }

        return encoded.Count > 0 ? encoded.ToString(Formatting.None) : null;
    }

    /// <summary>A look a ghost keeps, read back; Plain for text that is none.</summary>
    internal static GhostLook Decode(string? encoded)
    {
        if (string.IsNullOrEmpty(encoded))
        {
            return Plain;
        }

        JObject read;
        try
        {
            read = JObject.Parse(encoded!);
        }
        catch (JsonReaderException)
        {
            return Plain;
        }

        string preset = PresetOf((string?)read["look"]) ?? Plan;
        bool xray = read["xray"]?.Type != JTokenType.Boolean || (bool)read["xray"]!;
        double strength = read["xray"] is JObject x && x["strength"] is JValue sv ? (double)sv : DefaultStrength;
        double range = read["xray"] is JObject y && y["range"] is JValue rv ? (double)rv : DefaultRange;
        double? opacity = read["xray"] is JObject z && z["opacity"] is JValue ov ? (double)ov : (double?)null;
        Dictionary<string, bool> markers = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (read["markers"] is JObject listed)
        {
            foreach (KeyValuePair<string, JToken?> marker in listed)
            {
                if (marker.Value?.Type == JTokenType.Boolean)
                {
                    markers[marker.Key] = (bool)marker.Value;
                }
            }
        }

        return new GhostLook(preset, xray, strength, range, opacity, markers, read["problem"]?.Type == JTokenType.Boolean &&
            (bool)read["problem"]!, read["style"] as JObject);
    }

    /// <summary>The look of a ghost placed before 2.12 (attachments look, xray "off" and plan "problem").</summary>
    internal static GhostLook Legacy(string? preset, bool xrayOff, bool problem) =>
        new GhostLook(PresetOf(preset) ?? Plan, !xrayOff, DefaultStrength, DefaultRange, null,
            new Dictionary<string, bool>(), problem, null);

    /// <summary>The look as find ghost reports it: every setting, defaults included.</summary>
    internal JObject Report()
    {
        JObject markers = new JObject();
        foreach (string marker in Markers)
        {
            markers[marker] = Shows(marker);
        }

        JObject report = new JObject
        {
            ["look"] = Preset,
            ["xray"] = XRay,
        };
        if (XRay)
        {
            report["xray_strength"] = Strength;
            report["xray_range"] = Range;
            report["xray_opacity"] = Opacity;
        }

        report["markers"] = markers;
        report["problem"] = Problem;
        if (Style != null)
        {
            report["style"] = Style.DeepClone();
        }

        return report;
    }

    private static JObject Sorted(JObject given)
    {
        List<string> names = new List<string>();
        foreach (KeyValuePair<string, JToken?> property in given)
        {
            names.Add(property.Key);
        }

        names.Sort(StringComparer.Ordinal);
        JObject sorted = new JObject();
        foreach (string name in names)
        {
            sorted[name] = given[name] is JObject inner ? Sorted(inner) : given[name]!.DeepClone();
        }

        return sorted;
    }
}

/// <summary>What a call asks of a ghost's look: each null when not given.</summary>
internal sealed class GhostLookChange
{
    internal GhostLookChange(string? look, bool? xray, double? strength, double? range, JToken? markers, JToken? style,
        double? opacity = null)
    {
        Opacity = opacity;
        Look = look;
        XRay = xray;
        Strength = strength;
        Range = range;
        Markers = markers;
        Style = style;
    }

    internal string? Look { get; }

    internal bool? XRay { get; }

    internal double? Strength { get; }

    internal double? Range { get; }

    internal double? Opacity { get; }

    internal JToken? Markers { get; }

    internal JToken? Style { get; }

    internal bool Any => Look != null || XRay != null || Strength != null || Range != null || Opacity != null || Markers != null ||
                         Style != null;
}
