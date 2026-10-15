#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using UnityEngine;
using StationGodMCP.Pure.Profiling;
using StationGodMCP.Pure.Scheduling;

namespace StationGodMCP.Api.Shared.Game.Upgrades;

/// <summary>Turns a plan into the report the tools reply with.</summary>
internal static class UpgradeReports
{
    internal const string DryRun = "dry_run";
    internal const string Scheduled = "scheduled";
    internal const string Refused = "refused";

    /// <summary>How many pieces' views one step of Building describes.</summary>
    internal const int PiecesAStep = 8;

    /// <summary>The plan's report; limit, when given, is how many entries each list shows instead of the request's.</summary>
    [Profiled]
    internal static UpgradeReportView Of(UpgradePlan plan, string status, string? jobId, int? limit = null)
    {
        Answer<UpgradeReportView> report = new Answer<UpgradeReportView>();
        foreach (object? _ in Building(plan, status, jobId, limit, report))
        {
        }

        return report.Value;
    }

    /// <summary>
    /// The plan's report as work over frames (A3): the pieces' views a batch a step, each other part a step of its own.
    /// </summary>
    internal static IEnumerable<object?> Building(UpgradePlan plan, string status, string? jobId, int? limit,
        Answer<UpgradeReportView> answer)
    {
        UpgradeRequest request = plan.Request;
        int shown = limit ?? request.ListLimit;
        SwapGoal goal = request.Goal;
        UpgradeHeader header = new UpgradeHeader(goal.Tool, goal.Target, status, jobId,
            request.IncludeNotes ? goal.Notes(request.Family) : null);
        UpgradeCounts counts = new UpgradeCounts(plan.Total, plan.Swaps.Count, plan.Kept.Count, plan.Unmatched.Count);
        List<UpgradePieceView> pieces = new List<UpgradePieceView>(System.Math.Min(plan.Swaps.Count, shown));
        for (int from = 0; from < pieces.Capacity; from += PiecesAStep)
        {
            Pieces(plan, from, System.Math.Min(from + PiecesAStep, pieces.Capacity), pieces);
            yield return null;
        }

        UpgradeLists lists = new UpgradeLists(plan.Problems, pieces, ByPrefab(plan), Skipped(plan.Kept, shown),
            Skipped(plan.Unmatched, shown), plan.Warnings);
        yield return null;
        List<object> networks = new List<object>(plan.Networks.Count);
        foreach (NetworkRecord record in plan.Networks)
        {
            // As the plan's resources list it: without its devices.
            Answer<object> report = new Answer<object>();
            foreach (object? step in record.Reporting(report))
            {
                yield return step;
            }

            networks.Add(report.Value is IListsDevices listing ? listing.WithoutDevices() : report.Value);
            yield return null;
        }

        UpgradeResources resources = new UpgradeResources(
            plan.From != null ? GameLookup.ViewOf(plan.From) : null, Coils(plan), request.Refund, RefundTotals(plan),
            networks, Refunds.Forecast(plan.Refunds, RefundItems(plan)));
        yield return null;
        answer.Value = new UpgradeReportView(header, counts, lists, resources, plan.Links?.View(),
            DeadEnds(plan, shown), Loops(plan), Redundant(plan, shown), Riding(plan, shown));
    }

    private static UpgradeRidingList? Riding(UpgradePlan plan, int limit)
    {
        if (plan.Riding == null)
        {
            return null;
        }

        int count = System.Math.Min(plan.Riding.Count, limit);
        List<UpgradeRidingView> views = new List<UpgradeRidingView>(count);
        for (int index = 0; index < count; index++)
        {
            RidingChute riding = plan.Riding[index];
            views.Add(new UpgradeRidingView(new ThingId(riding.Piece.ReferenceId), GameLookup.ViewOf(riding.Item),
                riding.Held));
        }

        return new UpgradeRidingList(plan.Riding.Count, views);
    }

    private static UpgradeRedundancyView? Redundant(UpgradePlan plan, int limit)
    {
        RedundancyRecord? record = plan.Redundancy;
        if (record == null)
        {
            return null;
        }

        Dictionary<string, int> byReason = new Dictionary<string, int>();
        List<UpgradeRedundantKeptView> kept = new List<UpgradeRedundantKeptView>();
        foreach (KeptPiece piece in record.Result.Kept)
        {
            byReason[piece.Reason] = (byReason.TryGetValue(piece.Reason, out int count) ? count : 0) + 1;
            if (kept.Count < limit && record.Things.TryGetValue(piece.Id, out SmallGrid thing))
            {
                kept.Add(new UpgradeRedundantKeptView(GameLookup.ViewOf(thing), GameLookup.ViewOf(thing.Position),
                    piece.Reason, piece.Devices.ConvertAll(id => Named(record, id)), piece.Pieces));
            }
        }

        return new UpgradeRedundancyView(record.Candidates, record.Result.Removed.ConvertAll(id => new ThingId(id)),
            record.Roots.ConvertAll(id => Named(record, id)), byReason, kept, record.Result.Kept.Count);
    }

    private static ThingView Named(RedundancyRecord record, long id) =>
        record.Things.TryGetValue(id, out SmallGrid thing) ? GameLookup.ViewOf(thing)
        : GameLookup.TryFindThing(new ThingId(id), out Thing found) ? GameLookup.ViewOf(found)
        : new ThingView(new ThingId(id), null, null);

    private static List<UpgradeLoopView>? Loops(UpgradePlan plan)
    {
        if (plan.Loops == null)
        {
            return null;
        }

        List<UpgradeLoopView> views = new List<UpgradeLoopView>(plan.Loops.Count);
        foreach (LoopRecord record in plan.Loops)
        {
            List<UpgradeLoopPieceView> pieces = new List<UpgradeLoopPieceView>(record.Pieces.Count);
            foreach (SmallGrid piece in record.Pieces)
            {
                pieces.Add(new UpgradeLoopPieceView(GameLookup.ViewOf(piece), GameLookup.ViewOf(piece.Position),
                    record.Loop.Cut.Contains(piece.ReferenceId)));
            }

            views.Add(new UpgradeLoopView(record.Loop.Index, pieces, record.Loop.Cut.ConvertAll(id => new ThingId(id)),
                record.Loop.Spared, record.Loop.Unbroken));
        }

        return views;
    }

    internal static RotationView RotationOf(Quaternion rotation)
    {
        Vector3 euler = rotation.eulerAngles;
        return new RotationView(euler.x, euler.y, euler.z);
    }

    // The views of the plan's swaps from up to (not including) until, added to pieces.
    [Profiled]
    private static void Pieces(UpgradePlan plan, int from, int until, List<UpgradePieceView> pieces)
    {
        UpgradeFamily family = plan.Request.Family;
        for (int index = from; index < until; index++)
        {
            PlannedSwap swap = plan.Swaps[index];
            IReferencable? network = family.NetworkOf(swap.Old);
            UpgradeTargetView target = new UpgradeTargetView(swap.First?.Prefab.PrefabName,
                RotationOf(swap.First?.Rotation ?? swap.OldRotation), swap.KeepsRotation, swap.Cost);
            UpgradeCleanView? ends = swap.Detail != null ? CleanViewOf(plan, swap, swap.Detail) : null;
            pieces.Add(new UpgradePieceView(GameLookup.ViewOf(swap.Old), GameLookup.ViewOf(swap.Old.Position),
                RotationOf(swap.OldRotation), target, network != null ? new ThingId(network.ReferenceId) : null, ends));
        }
    }

    private static UpgradeCleanView CleanViewOf(UpgradePlan plan, PlannedSwap swap, CleanDetail detail)
    {
        List<ThingId>? merged = null;
        if (swap.Olds.Count > 1)
        {
            merged = new List<ThingId>(swap.Olds.Count);
            foreach (OldPiece old in swap.Olds)
            {
                merged.Add(new ThingId(old.Piece.ReferenceId));
            }
        }

        return new UpgradeCleanView(detail.Operation, detail.Ends, detail.ConnectedEnds, swap.Parts.Count,
            new UpgradeCleanExtras(detail.Round, merged, RefundEach(plan, swap), detail.Why));
    }

    // Only a goal that finds dead ends reports them; the upgrade tools' replies keep their shape.
    private static UpgradeDeadEnds? DeadEnds(UpgradePlan plan, int limit)
    {
        if (!plan.Request.Goal.ReportsEnds)
        {
            return null;
        }

        int count = System.Math.Min(plan.DeadEnds.Count, limit);
        List<UpgradeDeadEndView> views = new List<UpgradeDeadEndView>(count);
        for (int index = 0; index < count; index++)
        {
            DeadEndPiece piece = plan.DeadEnds[index];
            views.Add(new UpgradeDeadEndView(GameLookup.ViewOf(piece.Thing), GameLookup.ViewOf(piece.Thing.Position),
                piece.Reason, piece.Ends, piece.Connected, piece.StoppedBy));
        }

        return new UpgradeDeadEnds(plan.DeadEnds.Count, views);
    }

    private static List<object> ByPrefab(UpgradePlan plan)
    {
        List<PlannedSwap> firsts = new List<PlannedSwap>();
        List<int> counts = new List<int>();
        foreach (PlannedSwap swap in plan.Swaps)
        {
            if (swap.First == null)
            {
                continue;
            }

            int at = firsts.FindIndex(first =>
                first.Old.PrefabHash == swap.Old.PrefabHash && first.First!.Prefab == swap.First.Prefab);
            if (at < 0)
            {
                firsts.Add(swap);
                counts.Add(1);
            }
            else
            {
                counts[at]++;
            }
        }

        List<object> mappings = new List<object>(firsts.Count);
        for (int index = 0; index < firsts.Count; index++)
        {
            PlannedSwap first = firsts[index];
            Structure source = Prefab.Find(first.Old.PrefabHash) is Structure prefab && prefab != null
                ? prefab
                : first.Old;
            Structure target = first.First!.Prefab;
            UpgradeMappingCount count = new UpgradeMappingCount(first.Old.PrefabName, target.PrefabName,
                counts[index], first.Cost, RefundEach(plan, first));
            mappings.Add(plan.Request.Family.Mapping(count, source, target));
        }

        return mappings;
    }

    // What one swap gives back; 0 with refund off, when nothing comes back.
    private static int RefundEach(UpgradePlan plan, PlannedSwap swap)
    {
        int total = 0;
        foreach (ItemAmount amount in swap.Refund)
        {
            total += amount.Quantity;
        }

        return RefundShown.Count(plan.Request.Refund, total);
    }

    private static List<UpgradeSkippedView> Skipped(List<SkippedPiece> pieces, int limit)
    {
        int count = System.Math.Min(pieces.Count, limit);
        List<UpgradeSkippedView> views = new List<UpgradeSkippedView>(count);
        for (int index = 0; index < count; index++)
        {
            SkippedPiece piece = pieces[index];
            views.Add(new UpgradeSkippedView(GameLookup.ViewOf(piece.Thing), piece.Reason, piece.Message));
        }

        return views;
    }

    private static List<UpgradeCoilView> Coils(UpgradePlan plan)
    {
        List<UpgradeCoilView> coils = new List<UpgradeCoilView>(plan.Stocks.Count);
        foreach (ItemStock stock in plan.Stocks)
        {
            List<UpgradeStackView> stacks = new List<UpgradeStackView>(stock.Stacks.Count);
            foreach (HeldItem stack in stock.Stacks)
            {
                Slot? slot = stack.Item.ParentSlot;
                stacks.Add(new UpgradeStackView(new ThingId(stack.Item.ReferenceId), stack.Quantity,
                    slot?.Parent != null ? new ThingId(slot.Parent.ReferenceId) : null, slot?.SlotIndex));
            }

            coils.Add(new UpgradeCoilView(stock.Item.PrefabName, stock.Item.DisplayName, stock.Needed,
                stock.Available, stacks, PaySources.PaidBy(stock)));
        }

        return coils;
    }

    private static List<ItemAmount> RefundItems(UpgradePlan plan)
    {
        List<ItemAmount> items = new List<ItemAmount>();
        plan.Swaps.ForEach(swap => items.AddRange(swap.Refund));
        return items;
    }

    private static List<UpgradeAmountView> RefundTotals(UpgradePlan plan)
    {
        List<string?> names = new List<string?>();
        List<int> totals = new List<int>();
        foreach (PlannedSwap swap in plan.Swaps)
        {
            foreach (ItemAmount amount in swap.Refund)
            {
                int at = names.IndexOf(amount.Prefab.PrefabName);
                if (at < 0)
                {
                    names.Add(amount.Prefab.PrefabName);
                    totals.Add(amount.Quantity);
                }
                else
                {
                    totals[at] += amount.Quantity;
                }
            }
        }

        List<UpgradeAmountView> views = new List<UpgradeAmountView>(names.Count);
        for (int index = 0; index < names.Count; index++)
        {
            views.Add(new UpgradeAmountView(names[index], totals[index]));
        }

        return views;
    }

}
