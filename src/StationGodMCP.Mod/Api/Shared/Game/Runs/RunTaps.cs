#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game.Runs;

/// <summary>
/// The tap check on a run (Taps). After the forecast, the networks the run ends up on are known: a run tip left with
/// an open end that stops next to, or one free cell short of, a piece of another network gets not_joined naming that
/// piece (a saved plan that ended one cell short of its trunk built a separate network). With join_to, the run must
/// end up on that network: not_joined when it does not, naming the nearest of its pieces and every run cell beside
/// one. join_trunk adds the missing tap instead: the best near miss of any tip to join_to (fewest free cells, then
/// straight on; the main run's last tip first), laid as more run cells into the target piece's cell, which the
/// layout turns into a junction; the first of up to three that plans ready and joins is kept (tap_added).
/// </summary>
internal static class RunTaps
{
    internal const string NotJoined = "not_joined";
    internal const string TapAdded = "tap_added";
    private const int MaximumTries = 3;
    private const int MaximumListed = 8;

    internal static void Check(RunPlan plan)
    {
        RunBuild? build = plan.Request.Build;
        if (build == null || plan.Forecast == null || plan.Layout == null)
        {
            return;
        }

        HashSet<long> joined = JoinedNetworks(plan);
        ThingId? target = plan.Request.Options.Targets.JoinTo;
        if (target.HasValue && !joined.Contains(target.Value.Value))
        {
            TargetMissed(plan, build, joined, target.Value.Value);
            return;
        }

        int listed = 0;
        foreach (RunTip tip in build.Shape.Tips)
        {
            if (listed >= MaximumListed || !OpenAt(plan.Layout, tip.Cell))
            {
                continue;
            }

            List<NearMiss> misses = Taps.FromTip(tip.Cell, tip.Behind, PieceOf(plan, joined, null), Occupied(plan));
            if (misses.Count == 0)
            {
                continue;
            }

            NearMiss miss = misses[0];
            listed++;
            plan.Warnings.Add(new LayoutIssue(NotJoined,
                $"The run end at {Metres(tip.Cell)} stops {Gap(miss)} {Describe(plan, miss.Piece)} without joining " +
                $"it (the run's own open end there points {OpenStep(plan.Layout, tip.Cell)}). If the run should " +
                "join that network, " +
                $"add the tap ({TapText(miss)}; join_to with join_trunk: true adds it); if the networks are meant " +
                "to stay apart, ignore this.", tip.Cell, miss.Piece));
        }
    }

    /// <summary>
    /// The plan, or when it does not reach join_to, the plan of the run with the best tap added (planned again by
    /// planOnce, without join_trunk). Leaves the plan as it is, with a note, when no tap reaches the target.
    /// </summary>
    internal static RunPlan Tapped(RunRequest request, RunPlan plan, Func<RunRequest, RunPlan> planOnce)
    {
        RunTargets targets = request.Options.Targets;
        RunBuild? build = request.Build;
        if (build == null)
        {
            return plan;
        }

        if (!targets.JoinTo.HasValue)
        {
            plan.Problem("join_trunk_needs_target",
                "join_trunk needs join_to: the network the tap should reach (a network id, or {reference_id, port} " +
                "of a device on it).");
            return plan;
        }

        long target = targets.JoinTo.Value.Value;
        if (plan.Forecast == null || JoinedNetworks(plan).Contains(target))
        {
            return plan;
        }

        List<NearMiss> candidates = new List<NearMiss>();
        HashSet<long> joined = JoinedNetworks(plan);
        foreach (RunTip tip in TipsLastFirst(build.Shape))
        {
            candidates.AddRange(Taps.FromTip(tip.Cell, tip.Behind, PieceOf(plan, joined, target), Occupied(plan)));
        }

        // Fewest free cells, then straight on, then the tips' order (a stable sort: List.Sort is not).
        Dictionary<NearMiss, int> order = new Dictionary<NearMiss, int>();
        for (int index = 0; index < candidates.Count; index++)
        {
            order[candidates[index]] = index;
        }

        candidates.Sort((a, b) => a.Gap != b.Gap ? a.Gap.CompareTo(b.Gap)
            : a.Outward != b.Outward ? (a.Outward ? -1 : 1)
            : order[a].CompareTo(order[b]));
        RunOptions options = request.Options.WithTargets(targets.WithoutTrunk());
        for (int index = 0; index < candidates.Count && index < MaximumTries; index++)
        {
            NearMiss miss = candidates[index];
            RunShape? shape = build.Shape.WithTap(miss.From, miss.Tap, out string? _);
            if (shape == null)
            {
                continue;
            }

            RunPlan tapped = planOnce(request.With(shape, options));
            if (tapped.Ready && tapped.Forecast != null && JoinedNetworks(tapped).Contains(target))
            {
                tapped.Tap = miss;
                tapped.Warnings.RemoveAll(static warning => warning.Code == NotJoined);
                tapped.Warnings.Add(new LayoutIssue(TapAdded,
                    $"join_trunk: the run stopped {Gap(miss)} {Describe(plan, miss.Piece)}; the tap " +
                    $"({TapText(miss)}) is added, so the run joins network {target}.", miss.From, miss.Piece));
                return tapped;
            }
        }

        plan.Warnings.Add(new LayoutIssue("no_tap",
            candidates.Count == 0
                ? $"join_trunk: no run end is next to, or one cell short of, a piece of network {target}; route the " +
                  "run to it (plan with to: {network_id}) instead."
                : $"join_trunk: none of the {Math.Min(candidates.Count, MaximumTries)} taps tried towards network " +
                  $"{target} plans ready; the run is left as given.", null, target));
        return plan;
    }

    // Each network the run's new and changed pieces end up on after the edit (every network before it they join).
    // The networks the run ends up on: those its networks after hold pieces of, and those a device port it takes
    // over was on (a run that replaces removed pieces at a port joins what the port was joined to, though every piece
    // of that network goes).
    private static HashSet<long> JoinedNetworks(RunPlan plan)
    {
        HashSet<long> networks = new HashSet<long>();
        RunForecast forecast = plan.Forecast!;
        foreach (PlannedCell cell in plan.Cells)
        {
            if (forecast.NodeOf.TryGetValue(cell.ForecastId, out long node) &&
                forecast.ComponentOf.TryGetValue(node, out int index))
            {
                ForecastNetwork after = forecast.Result.Networks[index];
                networks.UnionWith(after.NetworksBefore);
                foreach (ForecastPort port in after.Ports)
                {
                    if (port.NetworkBefore.HasValue)
                    {
                        networks.Add(port.NetworkBefore.Value);
                    }
                }
            }
        }

        foreach (LayoutCell cell in plan.KeptCells)
        {
            if (cell.Existing != null && plan.Things.TryGetValue(cell.Existing.Id, out SmallGrid piece))
            {
                IReferencable? network = plan.Request.Kind.Family.NetworkOf(piece);
                if (network != null)
                {
                    networks.Add(network.ReferenceId);
                }
            }
        }

        return networks;
    }

    private static void TargetMissed(RunPlan plan, RunBuild build, HashSet<long> joined, long target)
    {
        Func<GridCell, long?> pieceAt = PieceOf(plan, joined, target);
        NearMiss? best = null;
        foreach (RunTip tip in TipsLastFirst(build.Shape))
        {
            List<NearMiss> misses = Taps.FromTip(tip.Cell, tip.Behind, pieceAt, Occupied(plan));
            if (misses.Count > 0 && (best == null || misses[0].Gap < best.Gap))
            {
                best = misses[0];
            }
        }

        List<NearMiss> beside = Taps.Beside(build.Cells, pieceAt);
        List<string> cells = new List<string>();
        for (int index = 0; index < beside.Count && index < MaximumListed; index++)
        {
            cells.Add($"{Metres(beside[index].From)} beside {beside[index].Piece}");
        }

        string near = best != null
            ? $" The nearest: the run end at {Metres(best.From)} stops {Gap(best)} {Describe(plan, best.Piece)} " +
              $"(tap: {TapText(best)}; join_trunk: true adds it)."
            : " No run end is within one free cell of it.";
        string along = cells.Count > 0
            ? $" Run cells next to it without joining: {string.Join(", ", cells)}."
            : string.Empty;
        plan.Warnings.Add(new LayoutIssue(NotJoined,
            $"join_to: the run does not join network {target}; it would stay a network of its own or join another." +
            near + along, best?.From, best?.Piece ?? target));
    }

    // The main run's last tip first (where a route to a trunk ends), then its first, then each branch's.
    private static List<RunTip> TipsLastFirst(RunShape shape)
    {
        List<RunTip> tips = new List<RunTip>(shape.Tips);
        if (tips.Count > 1)
        {
            RunTip last = tips[1];
            tips[1] = tips[0];
            tips[0] = last;
        }

        return tips;
    }

    private static bool OpenAt(RunLayout layout, GridCell cell)
    {
        foreach (LayoutCell laid in layout.Cells)
        {
            if (laid.Cell.Equals(cell))
            {
                return laid.OpenEnd.HasValue;
            }
        }

        return false;
    }

    private static string OpenStep(RunLayout layout, GridCell cell)
    {
        foreach (LayoutCell laid in layout.Cells)
        {
            if (laid.Cell.Equals(cell) && laid.OpenEnd.HasValue)
            {
                return laid.OpenEnd.Value.Name;
            }
        }

        return "nowhere";
    }

    // A piece of the kind standing in the cell that the run does not join (of the target network when one is named).
    private static Func<GridCell, long?> PieceOf(RunPlan plan, HashSet<long> joined, long? only)
    {
        HashSet<long> ignored = plan.IgnoredIds();
        RunKind kind = plan.Request.Kind;
        UpgradeFamily family = kind.Family;
        return cell =>
        {
            SmallGrid? piece = PieceIn(kind, cell);
            if (piece == null || ignored.Contains(piece.ReferenceId))
            {
                return null;
            }

            IReferencable? network = family.NetworkOf(piece);
            if (network == null || joined.Contains(network.ReferenceId) ||
                (only.HasValue && network.ReferenceId != only.Value))
            {
                return null;
            }

            return piece.ReferenceId;
        };
    }

    private static Func<GridCell, bool> Occupied(RunPlan plan)
    {
        HashSet<long> ignored = plan.IgnoredIds();
        RunKind kind = plan.Request.Kind;
        return cell =>
        {
            SmallGrid? piece = PieceIn(kind, cell);
            return piece != null && !ignored.Contains(piece.ReferenceId);
        };
    }

    private static SmallGrid? PieceIn(RunKind kind, GridCell cell)
    {
        SmallCell? small = GridController.World.GetSmallCell(PieceShapes.Grid(cell));
        SmallGrid? piece = small != null ? kind.SlotOf(small) : null;
        return piece != null && !piece.IsBeingDestroyed ? piece : null;
    }

    private static string Describe(RunPlan plan, long id)
    {
        Thing? thing = Thing.Find(id);
        if (!(thing is SmallGrid piece))
        {
            return $"piece {id}";
        }

        IReferencable? network = plan.Request.Kind.Family.NetworkOf(piece);
        return $"{piece.PrefabName} {id} of network " +
               (network != null ? network.ReferenceId.ToString(CultureInfo.InvariantCulture) : "none");
    }

    private static string Gap(NearMiss miss) =>
        miss.Gap == 0 ? "next to" : $"{miss.Gap} free cell short of";

    private static string TapText(NearMiss miss)
    {
        List<string> cells = miss.Tap.ConvertAll(Metres);
        return $"cells {string.Join(" -> ", cells)} from {Metres(miss.From)}";
    }

    private static string Metres(GridCell cell)
    {
        UnityEngine.Vector3 centre = PieceShapes.CentreOf(cell);
        return string.Format(CultureInfo.InvariantCulture, "({0:0.##}, {1:0.##}, {2:0.##})", centre.x, centre.y,
            centre.z);
    }
}
