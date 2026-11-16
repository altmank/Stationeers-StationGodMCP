#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

// A stand search and a press run once per call, off the game's frame loop: LINQ is allowed here.
#pragma warning disable RS0030

namespace StationGodMCP.Pure;

/// <summary>What the player's feet may stand on: any floor, or none required.</summary>
internal enum FloorRule
{
    Required,
    Any
}

/// <summary>move player's stand option: how near, how clear, and on what.</summary>
internal sealed record StandOptions(double ReachM, double ClearM, FloorRule Floor, bool On)
{
    /// <summary>The game's reach: CursorManager.MaxInteractDistance.</summary>
    internal const double MaxReachM = 3.0;

    internal const double DefaultReachM = 2.5;

    internal const double DefaultClearM = 0.1;

    internal static StandOptions Default { get; } = new(DefaultReachM, DefaultClearM, FloorRule.Required, false);
}

/// <summary>One thing the player's body must stay clear of: its id as the refusal names it, and its body.</summary>
internal sealed record StandObstacle(string Id, Box3 Body);

/// <summary>What a ray down from the feet hit within reach of them: a structure (its id) or the terrain.</summary>
internal sealed record StandFloor(string Name);

/// <summary>Why candidate points were turned down, and how many: overlaps &lt;id&gt;, no_floor, out_of_reach, line_blocked.</summary>
internal sealed record StandRejection(string Reason, int Count);

/// <summary>The answer of a stand search: a Stand, or NoStand with why no point passed.</summary>
internal abstract record StandResult
{
    private StandResult()
    {
    }

    /// <summary>A point that passed every check.</summary>
    internal sealed record Stand(Vec3 Feet, double DistanceM, double EyeToTargetM, string Floor, int Tried,
        IReadOnlyList<StandRejection> Rejected) : StandResult;

    /// <summary>No candidate passed; each reason with its count.</summary>
    internal sealed record NoStand(int Tried, IReadOnlyList<StandRejection> Rejected) : StandResult
    {
        internal string Describe() =>
            Tried == 0
                ? "no candidate point was tried"
                : $"{Tried} points tried; " + string.Join(", ", Rejected.Select(r => $"{r.Reason}: {r.Count}"));
    }
}

/// <summary>
/// Where move player's stand puts the player: feet on rings around the target's body at its base height (or, with on,
/// across the top of its footprint), each tried for clearance of every body, a floor, reach and a clear line, and the
/// passing point nearest the player wins. The world is read through two functions so the search runs on fakes.
/// </summary>
internal static class StandSearch
{
    /// <summary>The eye's height above the feet. A standing player's camera sits near the head; the in-game run measures it.</summary>
    internal const double EyeHeightM = 1.6;

    /// <summary>How far below the feet a floor may lie and still hold the player.</summary>
    internal const double FloorDropM = 0.3;

    /// <summary>A body whose top is no higher than this above the feet is the floor, not something to stand clear of.</summary>
    internal const double FloorThickM = 0.15;

    private const int Directions = 16;

    private const double Step = 0.25;

    private const double Touch = 0.005;

    /// <param name="target">The target's body.</param>
    /// <param name="from">Where the player is now: the nearest passing point to it wins.</param>
    /// <param name="obstacles">Every body to stay clear of, the target's own among them unless on is set.</param>
    /// <param name="floorBelow">The floor under a feet point within FloorDropM, or null.</param>
    /// <param name="lineClear">Whether nothing blocks the line from the eye to a point on the target.</param>
    internal static StandResult Find(Box3 target, Vec3 from, StandOptions options, IReadOnlyList<StandObstacle> obstacles,
        Func<Vec3, StandFloor?> floorBelow, Func<Vec3, Vec3, bool> lineClear)
    {
        Dictionary<string, int> rejected = new();
        int tried = 0;
        StandResult.Stand? best = null;
        foreach (Vec3 feet in Candidates(target, options))
        {
            tried++;
            string? reason = Rejection(feet, target, options, obstacles, floorBelow, lineClear, out StandFloor? floor,
                out double eyeToTarget);
            if (reason != null)
            {
                rejected[reason] = rejected.GetValueOrDefault(reason) + 1;
                continue;
            }

            double distance = (feet - from).Length;
            if (best == null || distance < best.DistanceM - 1e-9)
            {
                best = new StandResult.Stand(feet, distance, eyeToTarget, floor?.Name ?? "none", 0,
                    Array.Empty<StandRejection>());
            }
        }

        List<StandRejection> counts = rejected.Select(pair => new StandRejection(pair.Key, pair.Value))
            .OrderByDescending(r => r.Count).ThenBy(r => r.Reason, StringComparer.Ordinal).ToList();
        return best == null
            ? new StandResult.NoStand(tried, counts)
            : best with { Tried = tried, Rejected = counts };
    }

    /// <summary>The player's body at a feet point with clear_m added to its sides and top (never below the feet).</summary>
    internal static Box3 BodyWithClearance(Vec3 feet, double clearM)
    {
        Box3 body = Landing.PlayerAt(feet);
        return new Box3(new Vec3(body.Min.X - clearM, body.Min.Y, body.Min.Z - clearM),
            new Vec3(body.Max.X + clearM, body.Max.Y + clearM, body.Max.Z + clearM));
    }

    /// <summary>The point of the box nearest a point.</summary>
    internal static Vec3 Nearest(Box3 box, Vec3 point) =>
        new(Math.Clamp(point.X, box.Min.X, box.Max.X), Math.Clamp(point.Y, box.Min.Y, box.Max.Y),
            Math.Clamp(point.Z, box.Min.Z, box.Max.Z));

    private static string? Rejection(Vec3 feet, Box3 target, StandOptions options, IReadOnlyList<StandObstacle> obstacles,
        Func<Vec3, StandFloor?> floorBelow, Func<Vec3, Vec3, bool> lineClear, out StandFloor? floor, out double eyeToTarget)
    {
        floor = null;
        eyeToTarget = 0;
        Box3 body = BodyWithClearance(feet, options.ClearM);
        foreach (StandObstacle obstacle in obstacles)
        {
            if (obstacle.Body.Max.Y <= feet.Y + FloorThickM)
            {
                continue;
            }

            if (body.Penetration(obstacle.Body) > Touch)
            {
                return "overlaps " + obstacle.Id;
            }
        }

        if (options.Floor == FloorRule.Required)
        {
            floor = floorBelow(feet);
            if (floor == null)
            {
                return "no_floor";
            }
        }

        Vec3 eye = new(feet.X, feet.Y + EyeHeightM, feet.Z);
        Vec3 near = Nearest(target, eye);
        eyeToTarget = (near - eye).Length;
        if (eyeToTarget > options.ReachM)
        {
            return "out_of_reach";
        }

        return lineClear(eye, near) ? null : "line_blocked";
    }

    private static IEnumerable<Vec3> Candidates(Box3 target, StandOptions options)
    {
        Vec3 centre = target.Centre;
        if (options.On)
        {
            double top = target.Max.Y;
            for (double x = target.Min.X; x <= target.Max.X + 1e-9; x += Step)
            {
                for (double z = target.Min.Z; z <= target.Max.Z + 1e-9; z += Step)
                {
                    yield return new Vec3(x, top, z);
                }
            }

            yield return new Vec3(centre.X, top, centre.Z);
            yield break;
        }

        double steps = Math.Ceiling((options.ReachM + Landing.PlayerHalfWidthM * 2) / Step);
        for (int d = 0; d < Directions; d++)
        {
            double angle = 2 * Math.PI * d / Directions;
            Vec3 direction = new(Math.Sin(angle), 0, Math.Cos(angle));
            double side = ToSide(target, direction);
            for (int k = 0; k <= steps; k++)
            {
                double along = side + k * Step;
                yield return new Vec3(centre.X + direction.X * along, target.Min.Y, centre.Z + direction.Z * along);
            }
        }
    }

    private static double ToSide(Box3 body, Vec3 direction)
    {
        double alongX = Math.Abs(direction.X) > 1e-6 ? body.Size.X / 2.0 / Math.Abs(direction.X) : double.MaxValue;
        double alongZ = Math.Abs(direction.Z) > 1e-6 ? body.Size.Z / 2.0 / Math.Abs(direction.Z) : double.MaxValue;
        return Math.Min(alongX, alongZ);
    }
}

/// <summary>The press rule's answer to one press: go on to the real press, or refused for a reason.</summary>
internal abstract record PressDecision
{
    private PressDecision()
    {
    }

    /// <summary>Within reach and the game's own dry run accepts: the real press may go ahead.</summary>
    internal sealed record WouldAccept : PressDecision;

    /// <summary>The press is refused: out of reach, or not accepted with the game's own text.</summary>
    internal sealed record WouldRefuse(PressRefusal Kind, string Message) : PressDecision;
}

internal enum PressRefusal
{
    OutOfRange,
    NotAccepted
}

/// <summary>What the game's dry run (InteractWith with doAction false) answered.</summary>
internal sealed record DryPress(bool Accepted, string? Text);

/// <summary>The rule for write interact: reach first (unless forced), then the game's own answer to the dry press.</summary>
internal static class PressRule
{
    internal static PressDecision Decide(double eyeToThingM, bool force, double reachM, Func<DryPress> dryRun)
    {
        if (!force && eyeToThingM > reachM)
        {
            return new PressDecision.WouldRefuse(PressRefusal.OutOfRange,
                $"The player's eye is {eyeToThingM.ToString("0.0", CultureInfo.InvariantCulture)} m from the thing; " +
                $"a press reaches {reachM.ToString("0.#", CultureInfo.InvariantCulture)} m. move player {{to, stand}} " +
                "puts the player in reach; force: true presses anyway.");
        }

        DryPress dry = dryRun();
        return dry.Accepted
            ? new PressDecision.WouldAccept()
            : new PressDecision.WouldRefuse(PressRefusal.NotAccepted,
                string.IsNullOrWhiteSpace(dry.Text) ? "The game refused the press and gave no reason." : dry.Text.Trim());
    }
}

/// <summary>One interactable of a thing as inspect lists it.</summary>
internal sealed record InteractableRow(int Index, string Name, string Action, int? State, int? States);

/// <summary>Which interactable a name or an index means: found, missing (with the names present) or ambiguous.</summary>
internal abstract record InteractableChoice
{
    private InteractableChoice()
    {
    }

    internal sealed record Found(int Index) : InteractableChoice;

    internal sealed record Missing(string Wanted, IReadOnlyList<string> Names) : InteractableChoice;

    internal sealed record Ambiguous(string Wanted, IReadOnlyList<int> Indexes) : InteractableChoice;

    /// <summary>An integer is an index; a string is a name (or an action name), ignoring case.</summary>
    internal static InteractableChoice Of(IReadOnlyList<InteractableRow> rows, string wanted)
    {
        string text = wanted.Trim();
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int index))
        {
            return index < rows.Count ? new Found(index) : new Missing(text, NamesOf(rows));
        }

        List<int> byName = rows.Where(r => string.Equals(r.Name, text, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Index).ToList();
        if (byName.Count == 0)
        {
            byName = rows.Where(r => string.Equals(r.Action, text, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Index).ToList();
        }

        return byName.Count switch
        {
            0 => new Missing(text, NamesOf(rows)),
            1 => new Found(byName[0]),
            _ => new Ambiguous(text, byName)
        };
    }

    private static IReadOnlyList<string> NamesOf(IReadOnlyList<InteractableRow> rows) =>
        rows.Select(r => $"{r.Index}: {r.Name}").ToList();
}
