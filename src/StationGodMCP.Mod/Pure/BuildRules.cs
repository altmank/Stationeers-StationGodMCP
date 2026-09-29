#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// How a placement asks to be turned: Euler quarter turns (Quaternion.Euler order), a facing with an optional up, a
/// cell face for pieces placed on faces (walls: the piece sits on that face and faces into the cell), or nothing
/// (the prefab's own orientation, as the cursor starts).
/// </summary>
internal abstract class RotationSpec
{
    private RotationSpec()
    {
    }

    internal static RotationSpec Default { get; } = new None();

    /// <summary>The rotation asked for, or why there is none.</summary>
    internal abstract CubeRotation? Resolve(out string? error);

    /// <summary>The default up for a facing: +y, or +z when the facing is vertical.</summary>
    internal static GridStep DefaultUp(GridStep forward) =>
        forward.IsVertical ? GridStep.All[4] : GridStep.All[2];

    internal sealed class None : RotationSpec
    {
        internal override CubeRotation? Resolve(out string? error)
        {
            error = null;
            return CubeRotation.Identity;
        }
    }

    internal sealed class Euler : RotationSpec
    {
        internal Euler(int xTurns, int yTurns, int zTurns)
        {
            XTurns = xTurns;
            YTurns = yTurns;
            ZTurns = zTurns;
        }

        internal int XTurns { get; }

        internal int YTurns { get; }

        internal int ZTurns { get; }

        internal override CubeRotation? Resolve(out string? error)
        {
            error = null;
            return CubeRotation.FromEuler(XTurns, YTurns, ZTurns);
        }
    }

    internal sealed class Facing : RotationSpec
    {
        internal Facing(GridStep forward, GridStep? up)
        {
            Forward = forward;
            Up = up;
        }

        internal GridStep Forward { get; }

        internal GridStep? Up { get; }

        internal override CubeRotation? Resolve(out string? error)
        {
            GridStep up = Up ?? DefaultUp(Forward);
            CubeRotation? rotation = CubeRotation.FromFacing(Forward, up);
            error = rotation == null ? $"up {up.Name} is on the same axis as facing {Forward.Name}." : null;
            return rotation;
        }
    }

    /// <summary>A piece on a cell's face looks into the cell: its forward is the face's opposite.</summary>
    internal sealed class OnFace : RotationSpec
    {
        internal OnFace(GridStep face, GridStep? up)
        {
            Face = face;
            Up = up;
        }

        internal GridStep Face { get; }

        internal GridStep? Up { get; }

        internal override CubeRotation? Resolve(out string? error) =>
            new Facing(Face.Opposite, Up).Resolve(out error);
    }
}

/// <summary>Which build state a placement stops at: the finished piece, the kit's first stage, or an index.</summary>
internal abstract class BuildStatePick
{
    private BuildStatePick()
    {
    }

    internal static BuildStatePick Finished { get; } = new Last();

    internal static BuildStatePick First { get; } = new Index(0);

    /// <summary>The state index for a prefab with this many states; null (and why) when there is none.</summary>
    internal abstract int? Resolve(int stateCount, out string? error);

    internal sealed class Last : BuildStatePick
    {
        internal override int? Resolve(int stateCount, out string? error)
        {
            error = stateCount > 0 ? null : "it has no build states";
            return stateCount > 0 ? stateCount - 1 : (int?)null;
        }
    }

    internal sealed class Index : BuildStatePick
    {
        internal Index(int value)
        {
            Value = value;
        }

        internal int Value { get; }

        internal override int? Resolve(int stateCount, out string? error)
        {
            bool fits = Value >= 0 && Value < stateCount;
            error = fits ? null : $"build state {Value} is not between 0 and {stateCount - 1}";
            return fits ? Value : (int?)null;
        }
    }
}

/// <summary>The cheap guards of place_structure that need no game: free placement and the cursor's turn axes.</summary>
internal static class PlacementRule
{
    /// <summary>Why free placement is refused; null when it is allowed (asked for in a creative world) or not asked.</summary>
    internal static string? FreeRefusal(bool free, bool creative) =>
        free && !creative
            ? "free placement is only for creative worlds; take the materials from an inventory instead"
            : null;

    /// <summary>
    /// Whether the construction cursor can turn a grid-placed prefab to this rotation from its start, turning only
    /// about the axes it allows (RotationAxis), 90 degrees at a time, or 180 about x for a mounted piece.
    /// </summary>
    internal static bool CursorCanTurn(CubeRotation rotation, bool x, bool y, bool z, bool mounted) =>
        CubeRotation.Reachable(x ? (mounted ? 2 : 1) : 0, y ? 1 : 0, z ? 1 : 0).Contains(rotation);
}

/// <summary>Where a run's coils come from and go back to, when the game has no local player to hold them.</summary>
internal static class SourceRule
{
    /// <summary>The read-only removal planner: nothing is taken or given, its refund is an estimate.</summary>
    internal const string PlanRemoval = "plan_removal";

    /// <summary>
    /// How a missing local player (a dedicated server) with no from_id counts: a problem for a tool that takes or gives
    /// items, only a warning for plan_removal, which prices the refund and moves nothing.
    /// </summary>
    internal static GuardLevel NoLocalPlayer(string tool) =>
        tool == PlanRemoval ? GuardLevel.Warning : GuardLevel.Refusal;
}

/// <summary>Whether removing a piece is refused, warned about or allowed, and why.</summary>
internal enum GuardLevel
{
    Refusal,
    Warning
}

/// <summary>One finding of a guard: a code, a level and the reason in words.</summary>
internal sealed class GuardFinding
{
    internal GuardFinding(string code, GuardLevel level, string message)
    {
        Code = code;
        Level = level;
        Message = message;
    }

    internal string Code { get; }

    internal GuardLevel Level { get; }

    internal string Message { get; }
}

/// <summary>What happens to gas a removed device holds: a tank lets it out where it stood; others lose it.</summary>
internal enum GasFate
{
    None,
    Released,
    Lost
}

/// <summary>What the game says about a piece before it is removed.</summary>
internal sealed class RemovalFacts
{
    internal bool BeingDestroyed { get; set; }

    internal bool Indestructible { get; set; }

    internal bool Rocket { get; set; }

    /// <summary>
    /// The game's broken state (Structure.IsBroken: damage at its maximum, or a build state below 0, the broken mesh
    /// the game swaps in and then heals, so health reads 100 %).
    /// </summary>
    internal bool Broken { get; set; }

    /// <summary>The game's own refusal to deconstruct it (Structure.CanDeconstruct); null when it allows it.</summary>
    internal string? GameRefusal { get; set; }

    /// <summary>A device mounted on it, as the game names it; null when none.</summary>
    internal string? Mounted { get; set; }

    /// <summary>Items in its slots, described ("3 items: ...").</summary>
    internal List<string> Items { get; } = new List<string>();

    internal double GasMoles { get; set; }

    internal GasFate GasFate { get; set; }

    /// <summary>Where the gas is when it is not inside the piece itself (the pipe network it is the last of); else empty.</summary>
    internal string GasWhere { get; set; } = string.Empty;

    /// <summary>The largest pressure difference, in kPa, among the spaces removing it would join; null if none.</summary>
    internal double? BreachKpa { get; set; }

    internal string BreachWhere { get; set; } = string.Empty;
}

/// <summary>The allow flags of remove_structure.</summary>
internal sealed class RemovalAllowance
{
    internal RemovalAllowance(bool contents, bool breach, bool broken = false)
    {
        Contents = contents;
        Breach = breach;
        Broken = broken;
    }

    internal bool Contents { get; }

    internal bool Breach { get; }

    /// <summary>allow_broken: remove a broken piece as the game deconstructs one, which gives nothing back.</summary>
    internal bool Broken { get; }
}

/// <summary>
/// remove_structure's minimal safeguards, each naming its reason. Refused outright: being destroyed, indestructible,
/// rocket, the game's own refusal, a mounted device. Refused unless allowed: broken (allow_broken: the game cannot
/// repair a broken structure, only deconstruct it, and that gives nothing back; the game does not ask CanDeconstruct
/// on that path, so its refusal is not asked either), items in its slots or gas inside (allow_contents: items drop
/// where it stood, as a hand deconstruction does; a tank releases its gas there, other devices lose it), and joining
/// spaces whose pressures differ by at least BreachKpa (allow_breach). Allowed ones become warnings.
/// </summary>
internal static class RemovalRule
{
    /// <summary>A pressure difference that counts as a breach, in kPa.</summary>
    internal const double BreachKpa = 1.0;

    internal const double GasFloorMol = 0.001;

    internal const string BrokenWhat =
        "it is broken (the game's broken state, left by fire, pressure or other damage; the game cannot repair it, " +
        "only deconstruct it)";

    internal const string BrokenConsequence =
        "it goes as the game deconstructs a broken thing, which gives nothing back";

    internal static List<GuardFinding> Judge(RemovalFacts facts, RemovalAllowance allow)
    {
        List<GuardFinding> findings = new List<GuardFinding>();
        Refuse(findings, facts.BeingDestroyed, "being_destroyed", "it is already being destroyed");
        Refuse(findings, facts.Indestructible, "indestructible", "it is indestructible");
        Refuse(findings, facts.Rocket, "rocket", "it is part of a rocket");
        if (facts.Broken)
        {
            findings.Add(Allowable(allow.Broken, "broken", "broken_removed", BrokenWhat, "allow_broken",
                BrokenConsequence));
        }

        Refuse(findings, facts.GameRefusal != null && !(facts.Broken && allow.Broken), "game_refuses",
            $"the game refuses to deconstruct it: {facts.GameRefusal}");
        Refuse(findings, facts.Mounted != null, "has_mounted",
            $"{facts.Mounted} is mounted on it or stands on it, with nothing else to rest on; remove that first");
        if (facts.Items.Count > 0)
        {
            findings.Add(Allowable(allow.Contents, "holds_items", "items_dropped",
                $"it holds {string.Join(", ", facts.Items)}",
                "allow_contents", "they drop where it stood"));
        }

        if (facts.GasMoles >= GasFloorMol)
        {
            string fate = facts.GasFate == GasFate.Released
                ? "the game lets it out into the cell where it stood"
                : "the game deletes it with the device";
            findings.Add(Allowable(allow.Contents, "holds_gas", "gas_" + (facts.GasFate == GasFate.Released ?
                    "released" : "lost"), $"it holds {facts.GasMoles:0.###} mol of gas or liquid{facts.GasWhere}",
                "allow_contents",
                fate));
        }

        if (facts.BreachKpa.HasValue && facts.BreachKpa.Value >= BreachKpa)
        {
            findings.Add(Allowable(allow.Breach, "would_breach", "breach",
                $"removing it joins spaces whose pressures differ by {facts.BreachKpa.Value:0.#} kPa " +
                $"({facts.BreachWhere})", "allow_breach", "the air will rush through"));
        }

        return findings;
    }

    /// <summary>The largest difference among the pressures of the spaces a removal joins; null for fewer than two.</summary>
    internal static double? Spread(IReadOnlyList<double> pressuresKpa)
    {
        if (pressuresKpa.Count < 2)
        {
            return null;
        }

        double low = double.MaxValue;
        double high = double.MinValue;
        foreach (double pressure in pressuresKpa)
        {
            low = Math.Min(low, pressure);
            high = Math.Max(high, pressure);
        }

        return high - low;
    }

    internal static bool Refused(IEnumerable<GuardFinding> findings)
    {
        foreach (GuardFinding finding in findings)
        {
            if (finding.Level == GuardLevel.Refusal)
            {
                return true;
            }
        }

        return false;
    }

    private static void Refuse(List<GuardFinding> findings, bool when, string code, string message)
    {
        if (when)
        {
            findings.Add(new GuardFinding(code, GuardLevel.Refusal, message));
        }
    }

    private static GuardFinding Allowable(bool allowed, string refusalCode, string warningCode, string what,
        string flag, string consequence) =>
        allowed
            ? new GuardFinding(warningCode, GuardLevel.Warning, $"{what}; {flag} is set, so {consequence}")
            : new GuardFinding(refusalCode, GuardLevel.Refusal, $"{what}; pass {flag} to remove it anyway ({consequence})");
}
