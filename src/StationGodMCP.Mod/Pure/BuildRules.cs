#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;

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

    /// <summary>
    /// Whether the tool needs a source at all: one that builds takes coils or kits from it, one that refunds gives to
    /// it; a removal with refund false moves no item, so it needs no player and no from_id.
    /// </summary>
    internal static bool NeedsSource(bool builds, bool refund) => builds || refund;
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
    /// A wreck (HealthCondition.IsWreck): the game's broken state (Structure.IsBroken: damage at its maximum, or a build
    /// state below 0, the broken mesh the game swaps in and then heals, so health reads 100 %), a burst pipe or a burnt
    /// cable.
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

/// <summary>
/// A pipe network's pressure before and after a removal takes volume off it, and the weakest pipe left; when the
/// removal splits it (Parts above 1), one of the networks left: its volume, its pressure and its weakest pipe.
/// </summary>
internal sealed class NetworkSqueeze
{
    internal NetworkSqueeze(long network, double beforeKpa, double afterKpa, double removedL, double leftL,
        double? lowestKpa, int parts = 1)
    {
        Network = network;
        BeforeKpa = beforeKpa;
        AfterKpa = afterKpa;
        RemovedL = removedL;
        LeftL = leftL;
        LowestKpa = lowestKpa;
        Parts = parts;
    }

    /// <summary>How many networks the removal leaves; above 1, LeftL and AfterKpa are one of them.</summary>
    internal int Parts { get; }

    internal long Network { get; }

    internal double BeforeKpa { get; }

    internal double AfterKpa { get; }

    internal double RemovedL { get; }

    internal double LeftL { get; }

    /// <summary>The lowest MaxPressure of the pipes left; null when none is rated.</summary>
    internal double? LowestKpa { get; }
}

/// <summary>The allow flags of remove_structure.</summary>
internal sealed class RemovalAllowance
{
    internal RemovalAllowance(bool contents, bool breach, bool broken = false, bool burst = false)
    {
        Contents = contents;
        Breach = breach;
        Broken = broken;
        Burst = burst;
    }

    internal bool Contents { get; }

    internal bool Breach { get; }

    /// <summary>allow_broken: remove a broken piece as the game deconstructs one, which gives nothing back.</summary>
    internal bool Broken { get; }

    /// <summary>
    /// allow_burst: remove an in-line tank or passive vent although the network left is squeezed past its weakest
    /// pipe (would_burst), e.g. outdoors, where a burst into the atmosphere is acceptable; warned as will_burst.
    /// </summary>
    internal bool Burst { get; }
}

/// <summary>
/// A pipe the forecast pressure is expected to burst: one of the weakest pipes left that stands in a cell holding air
/// (the game damages only such a member, AtmosphericsNetwork.ScanStructuresAndEvaluate), with where its gas goes.
/// </summary>
internal sealed class BurstPipe
{
    /// <summary>What Where reads for a cell in no closed room.</summary>
    internal const string Outdoors = "outdoors";

    internal BurstPipe(long id, string prefab, double ratingKpa, string where, double outsideKpa)
    {
        Id = id;
        Prefab = prefab;
        RatingKpa = ratingKpa;
        Where = where;
        OutsideKpa = outsideKpa;
    }

    internal long Id { get; }

    internal string Prefab { get; }

    internal double RatingKpa { get; }

    /// <summary>The room id of the cell it leaks into, or "outdoors" (Outdoors).</summary>
    internal string Where { get; }

    /// <summary>The pressure of that cell now.</summary>
    internal double OutsideKpa { get; }
}

/// <summary>One gas a burst lets out, and how much.</summary>
internal sealed class ReleasedGas
{
    internal ReleasedGas(string gas, double mol)
    {
        Gas = gas;
        Mol = mol;
    }

    internal string Gas { get; }

    internal double Mol { get; }
}

/// <summary>
/// A network left over its weakest pipe that allow_burst lets the removal leave so: the squeeze, the gas it holds,
/// the pipes expected to burst and the gas expected out through them.
/// </summary>
internal sealed class BurstForecast
{
    internal BurstForecast(NetworkSqueeze squeeze, double holdsMol, List<BurstPipe> pipes, List<ReleasedGas> released)
    {
        Squeeze = squeeze;
        HoldsMol = holdsMol;
        Pipes = pipes;
        Released = released;
        foreach (ReleasedGas gas in released)
        {
            ReleasedMol += gas.Mol;
        }

        List<string> where = new List<string>();
        foreach (BurstPipe pipe in pipes)
        {
            if (!where.Contains(pipe.Where))
            {
                where.Add(pipe.Where);
            }
        }

        Where = where;
    }

    internal NetworkSqueeze Squeeze { get; }

    /// <summary>What the network left holds once the job is done: the most a burst can let out.</summary>
    internal double HoldsMol { get; }

    /// <summary>The weakest pipes left in cells that hold air; empty when none is (then none bursts).</summary>
    internal List<BurstPipe> Pipes { get; }

    /// <summary>The gas expected out, gas by gas (RemovalRule.ReleasedShare of what it holds).</summary>
    internal List<ReleasedGas> Released { get; }

    internal double ReleasedMol { get; }

    /// <summary>The distinct places of Pipes: room ids, or "outdoors".</summary>
    internal List<string> Where { get; }

    internal bool Bursts => Pipes.Count > 0;
}

/// <summary>
/// remove_structure's minimal safeguards, each naming its reason. Refused outright: being destroyed, indestructible,
/// rocket, the game's own refusal, a mounted device. Refused unless allowed: broken (allow_broken: the game cannot
/// repair a broken structure, only deconstruct it, and that gives nothing back; the game does not ask CanDeconstruct
/// on that path, so its refusal is not asked either), items in its slots or gas inside (allow_contents: items drop
/// where it stood, as a hand deconstruction does; a tank releases its gas there, other devices lose it), and joining
/// spaces whose pressures differ by at least BreachKpa (allow_breach), and squeezing a pipe network past its weakest
/// pipe (would_burst; allow_burst: will_burst). Allowed ones become warnings.
/// </summary>
internal static class RemovalRule
{
    /// <summary>A pressure difference that counts as a breach, in kPa.</summary>
    internal const double BreachKpa = 1.0;

    internal const double GasFloorMol = 0.001;

    /// <summary>Warning: allowed gas let out where it stood (a tank).</summary>
    internal const string GasReleased = "gas_released";

    /// <summary>
    /// Warning: allowed gas or liquid the game deletes with what is removed. Not gas_lost, which is the job status of
    /// a failed gas check that holds every later pipe job (pipes-28).
    /// </summary>
    internal const string ContentsDeleted = "contents_deleted";

    /// <summary>
    /// A pipe tool's contents refusal (holds_contents, contents_would_move) that allow_contents lifts, reworded as the
    /// warning it becomes: its "empty it first" advice is dropped and the allowance named.
    /// </summary>
    internal static string AllowedContents(string message)
    {
        string trimmed = message.TrimEnd();
        foreach (string advice in new[] { " Empty it first.", "; empty it first." })
        {
            if (trimmed.EndsWith(advice, StringComparison.Ordinal))
            {
                trimmed = trimmed.Substring(0, trimmed.Length - advice.Length);
                break;
            }
        }

        return trimmed.TrimEnd('.') + "; allow_contents is set, so it is removed anyway.";
    }

    /// <summary>
    /// A remove tool's would_split as remove_structure warns it: the remove tool's advice names arguments
    /// remove_structure does not take (allow_split, root), so it is dropped and the warning says the split goes ahead.
    /// </summary>
    internal static string SplitWarning(string message)
    {
        string trimmed = message.TrimEnd();
        const string advice = " Pass allow_split if that is meant.";
        if (trimmed.EndsWith(advice, StringComparison.Ordinal))
        {
            trimmed = trimmed.Substring(0, trimmed.Length - advice.Length);
        }

        return trimmed.Replace(" (pass root to name one).", ".").TrimEnd('.') +
               "; remove_structure only warns of a split and removes it anyway (plan_removal or remove_pipes with " +
               "root name the devices a root still reaches).";
    }

    internal const string BrokenWhat =
        "it is broken (the game's broken state, left by fire, pressure or other damage, a burst pipe or a burnt " +
        "cable; the game cannot repair it, only deconstruct it)";

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
            bool released = facts.GasFate == GasFate.Released;
            string fate = released
                ? "the game lets it out into the cell where it stood"
                : facts.GasWhere.Length == 0
                    ? "the game deletes it with the device"
                    : "the game deletes it with the pieces removed";
            findings.Add(Allowable(allow.Contents, "holds_gas", released ? GasReleased : ContentsDeleted,
                $"it holds {facts.GasMoles:0.###} mol of gas or liquid{facts.GasWhere}", "allow_contents", fate));
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

    /// <summary>
    /// A pipe network a removal takes volume from while its gas stays: the forecast pressure of what is left against
    /// the weakest pipe left. would_burst (the pipe tools' code, which they do not lift either) when it is over; null
    /// otherwise.
    /// </summary>
    internal static GuardFinding? Squeeze(NetworkSqueeze squeeze)
    {
        if (!squeeze.LowestKpa.HasValue || squeeze.AfterKpa <= squeeze.LowestKpa.Value)
        {
            return null;
        }

        double share = 100.0 * (1.0 - squeeze.LowestKpa.Value / squeeze.AfterKpa);
        string where = squeeze.Parts > 1
            ? string.Format(CultureInfo.InvariantCulture,
                "the game divides the gas among the {0} networks left, in the order this job removes the pieces, " +
                "and the {1:0.#} L one gets", squeeze.Parts, squeeze.LeftL)
            : string.Format(CultureInfo.InvariantCulture, "the game keeps the gas in the {0:0.#} L left",
                squeeze.LeftL);
        return new GuardFinding("would_burst", GuardLevel.Refusal, string.Format(CultureInfo.InvariantCulture,
            "removing it takes {0:0.#} L off pipe network {1} and {2}: " +
            "{3:0.#} kPa now, {4:0.#} kPa after, over the weakest pipe left (rated {5:0.#} kPa), which would burst; " +
            "take at least {6:0.#} % of its gas out first (move_gas), or pass allow_burst where a burst is " +
            "acceptable (outdoors)", squeeze.RemovedL, squeeze.Network,
            where, squeeze.BeforeKpa, squeeze.AfterKpa, squeeze.LowestKpa.Value,
            Math.Ceiling(share * 10.0) / 10.0));
    }

    /// <summary>
    /// The share of a burst network's gas expected out: a burst pipe leaks until the network is down to the pressure
    /// of the cell it leaks into (Pipe.LeakMix, every atmospheric tick), taken as the lowest such pressure among the
    /// pipes expected to burst (the most that can go). Nothing without such a pipe, or a network not above it.
    /// </summary>
    internal static double ReleasedShare(double afterKpa, IReadOnlyList<BurstPipe> pipes)
    {
        if (pipes.Count == 0 || afterKpa <= 0.0)
        {
            return 0.0;
        }

        double outside = double.MaxValue;
        foreach (BurstPipe pipe in pipes)
        {
            outside = Math.Min(outside, Math.Max(0.0, pipe.OutsideKpa));
        }

        return Math.Max(0.0, Math.Min(1.0, 1.0 - outside / afterKpa));
    }

    /// <summary>
    /// would_burst lifted by allow_burst: the warning will_burst, naming the forecast pressure, the rating, the pipes
    /// expected to burst with where each leaks (a room id, or outdoors), and the gas expected out.
    /// </summary>
    internal static GuardFinding WillBurst(BurstForecast forecast)
    {
        NetworkSqueeze squeeze = forecast.Squeeze;
        string left = squeeze.Parts > 1
            ? string.Format(CultureInfo.InvariantCulture, "the {0:0.#} L network left of pipe network {1} ({2} " +
                "networks left)", squeeze.LeftL, squeeze.Network, squeeze.Parts)
            : string.Format(CultureInfo.InvariantCulture, "pipe network {0} ({1:0.#} L left)", squeeze.Network,
                squeeze.LeftL);
        string what = string.Format(CultureInfo.InvariantCulture,
            "removing it squeezes {0} to {1:0.#} kPa (now {2:0.#} kPa), over its weakest pipe (rated {3:0.#} kPa); " +
            "allow_burst is set, so it goes ahead", left, squeeze.AfterKpa, squeeze.BeforeKpa,
            squeeze.LowestKpa ?? 0.0);
        if (!forecast.Bursts)
        {
            return new GuardFinding("will_burst", GuardLevel.Warning,
                what + ", but none of its weakest pipes stands in a cell that holds air, and the game damages only " +
                "such a pipe: none is expected to burst, and nothing to leak");
        }

        List<string> pipes = new List<string>(forecast.Pipes.Count);
        foreach (BurstPipe pipe in forecast.Pipes)
        {
            pipes.Add(string.Format(CultureInfo.InvariantCulture, "{0} {1} ({2}, {3:0.#} kPa there)", pipe.Prefab,
                pipe.Id, PlaceOf(pipe.Where), pipe.OutsideKpa));
        }

        List<string> gases = new List<string>(forecast.Released.Count);
        foreach (ReleasedGas gas in forecast.Released)
        {
            gases.Add(string.Format(CultureInfo.InvariantCulture, "{0} {1:0.###} mol", gas.Gas, gas.Mol));
        }

        List<string> places = forecast.Where.ConvertAll(PlaceOf);
        return new GuardFinding("will_burst", GuardLevel.Warning, string.Format(CultureInfo.InvariantCulture,
            "{0}: expected to burst: {1} (the game damages one of them, the most worn, until it bursts); it then " +
            "leaks until the network is down to the pressure where it leaks: about {2:0.###} mol of the {3:0.###} mol " +
            "it holds ({4}) into {5}",
            what, string.Join(", ", pipes), forecast.ReleasedMol, forecast.HoldsMol,
            gases.Count > 0 ? string.Join(", ", gases) : "no gas", string.Join(" or ", places)));
    }

    private static string PlaceOf(string where) => where == BurstPipe.Outdoors ? where : "room " + where;

    /// <summary>
    /// A pipe network holding gas or liquid that a removal splits (an in-line tank or passive vent between its pipes):
    /// the game divides the contents among the networks left by volume, which moves them, so it is refused as the pipe
    /// tools refuse it (contents_would_move), unless allow_contents (a warning then). Null for no split or no contents.
    /// </summary>
    internal static GuardFinding? Divided(long network, double moles, IReadOnlyList<double> partMoles,
        RemovalAllowance allow)
    {
        if (partMoles.Count < 2 || moles < GasFloorMol)
        {
            return null;
        }

        List<string> shares = new List<string>(partMoles.Count);
        foreach (double part in partMoles)
        {
            shares.Add(part.ToString("0.###", CultureInfo.InvariantCulture));
        }

        string what = string.Format(CultureInfo.InvariantCulture,
            "removing it splits pipe network {0}, which holds {1:0.###} mol, into {2} networks", network, moles,
            partMoles.Count);
        string consequence = $"the game divides the contents among them by volume: {string.Join(" + ", shares)} mol";
        return allow.Contents
            ? new GuardFinding("contents_would_move", GuardLevel.Warning,
                $"{what}; allow_contents is set, so {consequence}")
            : new GuardFinding("contents_would_move", GuardLevel.Refusal,
                $"{what}; {consequence}. Empty it first, or pass allow_contents to remove it anyway");
    }

    /// <summary>
    /// A refund holder (from_id) the request removes, or one held inside something it removes: the refund would be
    /// destroyed with it.
    /// </summary>
    internal static string HolderRemoved(string holder, string? removedContainer) =>
        (removedContainer == null
            ? $"from_id {holder} is removed by this request"
            : $"from_id {holder} is inside {removedContainer}, which this request removes") +
        ", so the refund would be destroyed with it; pass another from_id, or refund_to ground or none.";

    /// <summary>A container refund_to names that the request removes, or that is inside something it removes.</summary>
    internal static string ContainerRemoved(string container, string? removedContainer) =>
        (removedContainer == null
            ? $"refund_to container {container} is removed by this request"
            : $"refund_to container {container} is inside {removedContainer}, which this request removes") +
        ", so the refund would be destroyed with it; name another container in refund_to.";

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
