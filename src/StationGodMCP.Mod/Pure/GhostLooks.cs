#nullable enable

using System;

namespace StationGodMCP.Pure;

/// <summary>
/// How an agent's ghost looks in Lattice's drawing beyond its tint: a preset (plan, the default: Lattice's look with
/// StationGod's see-through, ports and open ends; solid; wire; pulse; quiet: no see-through, ports or open ends),
/// see-through off (xray false), and whether its plan had problems (a red pulse). Kept on the ghost (Lattice
/// attachments), so every StationGod viewer draws it so; StationGod's own board drawing shows tint and see-through only.
/// </summary>
internal sealed class GhostLook
{
    internal const string Plan = "plan";

    internal static readonly string[] Presets = { Plan, "solid", "wire", "pulse", "quiet" };

    internal static readonly GhostLook Plain = new GhostLook(Plan, xray: true, problem: false);

    internal GhostLook(string preset, bool xray, bool problem)
    {
        Preset = preset;
        XRay = xray;
        Problem = problem;
    }

    /// <summary>One of Presets.</summary>
    internal string Preset { get; }

    /// <summary>Shows through walls, frames and terrain (the default).</summary>
    internal bool XRay { get; }

    /// <summary>Its plan had problems (it would not build as placed): drawn with a red pulse.</summary>
    internal bool Problem { get; }

    internal GhostLook WithProblem(bool problem) => new GhostLook(Preset, XRay, problem);

    /// <summary>A preset by name, any case; null for a name that is none.</summary>
    internal static string? PresetOf(string? given)
    {
        string? name = given?.Trim().ToLowerInvariant();
        return name != null && Array.IndexOf(Presets, name) >= 0 ? name : null;
    }
}
