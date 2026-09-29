#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game.Structures;

/// <summary>Turns a replace plan into the report the tools reply with.</summary>
internal static class StructureReports
{
    internal const string DryRun = "dry_run";
    internal const string Scheduled = "scheduled";
    internal const string Refused = "refused";

    private static readonly List<string> Notes = new List<string>
    {
        "Materials follow the game's deconstruct rule; tool wear, welder fuel and power-tool battery are not charged.",
        "A real run holds the game tick, swaps every piece in one frame, checks with the tick held, then checks the " +
        "rooms once the game has re-evaluated them."
    };

    // What the run gives back per item, as the swapper delivers it.
    private static List<ItemAmount> GivenBack(StructureSwapPlan plan)
    {
        List<ItemAmount> items = new List<ItemAmount>();
        foreach (MaterialTotal total in plan.Totals)
        {
            if (total.GiveBack > 0 && plan.Items.TryGetValue(total.Item, out Item item))
            {
                items.Add(new ItemAmount(item, total.GiveBack));
            }
        }

        return items;
    }

    internal static StructureSwapReportView Of(StructureSwapPlan plan, string status, string? jobId)
    {
        StructureSwapRequest request = plan.Request;
        int limit = request.Arguments.Limit;
        UpgradeHeader header = new UpgradeHeader(request.Family.Tool, request.TargetLabel, status, jobId,
            new List<string>(Notes));
        UpgradeCounts counts = new UpgradeCounts(plan.Total, plan.Swaps.Count, plan.Kept.Count, plan.Unmatched.Count);
        StructureSwapLists lists = new StructureSwapLists(plan.Problems, Pieces(plan, limit), ByPrefab(plan),
            Skipped(plan.Kept, limit), Skipped(plan.Unmatched, limit));
        StructureSwapResources resources = new StructureSwapResources(
            plan.From != null ? GameLookup.ViewOf(plan.From) : null, Materials(plan), request.Arguments.Refund,
            plan.Air?.RoomViews() ?? new List<StructureRoomView>(), Refunds.Forecast(plan.Refunds, GivenBack(plan)));
        return new StructureSwapReportView(header, counts, lists, resources);
    }

    private static List<StructurePieceView> Pieces(StructureSwapPlan plan, int limit)
    {
        int count = System.Math.Min(plan.Swaps.Count, limit);
        List<StructurePieceView> pieces = new List<StructurePieceView>(count);
        for (int index = 0; index < count; index++)
        {
            PlannedStructureSwap swap = plan.Swaps[index];
            Structure old = swap.Old;
            StructureTargetView target = new StructureTargetView(swap.Target.PrefabName, old.CurrentBuildStateIndex,
                swap.FinalState);
            StructureBlockingView blocking = new StructureBlockingView(swap.Before.Air, swap.After.Air,
                swap.Before.Gravity, swap.After.Gravity, swap.ChangeName, swap.Stressed);
            pieces.Add(new StructurePieceView(GameLookup.ViewOf(old), GameLookup.ViewOf(old.Position),
                UpgradeReports.RotationOf(old.ThingTransformRotation), target, blocking, Faces(swap),
                RoomIds(plan, swap), Lines(plan, swap)));
        }

        return pieces;
    }

    private static List<StructureFaceView>? Faces(PlannedStructureSwap swap)
    {
        if (swap.Faces == null)
        {
            return null;
        }

        List<StructureFaceView> faces = new List<StructureFaceView>(swap.Faces.Count);
        foreach (WallFace face in swap.Faces)
        {
            faces.Add(new StructureFaceView(PositionOf(face.Face),
                new List<PositionView> { PositionOf(face.A), PositionOf(face.B) }, face.Pressure, face.Verdict));
        }

        return faces;
    }

    private static List<string> RoomIds(StructureSwapPlan plan, PlannedStructureSwap swap)
    {
        List<string> ids = new List<string>();
        foreach (GridPoint cell in swap.AffectedCells)
        {
            string? id = plan.Air?.RoomOf(cell);
            if (id != null && !ids.Contains(id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private static List<StructureMaterialLineView> Lines(StructureSwapPlan plan, PlannedStructureSwap swap)
    {
        List<StructureMaterialLineView> lines = new List<StructureMaterialLineView>(swap.Materials.Count);
        foreach (MaterialLine line in swap.Materials)
        {
            lines.Add(new StructureMaterialLineView(plan.Items[line.Item].PrefabName, line.Cost, line.Refund,
                plan.Request.Arguments.Refund));
        }

        return lines;
    }

    private static List<StructureMappingView> ByPrefab(StructureSwapPlan plan)
    {
        List<StructureMappingView> mappings = new List<StructureMappingView>();
        List<int> counts = new List<int>();
        List<PlannedStructureSwap> firsts = new List<PlannedStructureSwap>();
        foreach (PlannedStructureSwap swap in plan.Swaps)
        {
            int at = firsts.FindIndex(first =>
                first.Old.PrefabHash == swap.Old.PrefabHash && first.Target.PrefabHash == swap.Target.PrefabHash);
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

        for (int index = 0; index < firsts.Count; index++)
        {
            mappings.Add(new StructureMappingView(firsts[index].Old.PrefabName, firsts[index].Target.PrefabName,
                counts[index]));
        }

        return mappings;
    }

    private static List<StructureMaterialView> Materials(StructureSwapPlan plan)
    {
        List<StructureMaterialView> views = new List<StructureMaterialView>(plan.Totals.Count);
        foreach (MaterialTotal total in plan.Totals)
        {
            Item item = plan.Items[total.Item];
            ItemStock? stock = plan.StockOf(total.Item);
            List<UpgradeStackView> stacks = new List<UpgradeStackView>();
            foreach (Stackable stack in stock?.Stacks ?? new List<Stackable>())
            {
                Slot? slot = stack.ParentSlot;
                stacks.Add(new UpgradeStackView(new ThingId(stack.ReferenceId), stack.Quantity,
                    slot?.Parent != null ? new ThingId(slot.Parent.ReferenceId) : null, slot?.SlotIndex));
            }

            views.Add(new StructureMaterialView(item.PrefabName, item.DisplayName,
                new StructureMaterialCounts(total.Cost, total.Refund, total.Charge,
                    plan.Request.Arguments.Refund ? total.GiveBack : 0,
                    stock?.Available ?? 0), stacks));
        }

        return views;
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

    private static PositionView PositionOf(GridPoint point) => new PositionView(point.X / 10.0, point.Y / 10.0,
        point.Z / 10.0);
}
