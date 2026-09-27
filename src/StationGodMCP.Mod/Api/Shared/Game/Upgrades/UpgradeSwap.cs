#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game.Upgrades;

/// <summary>
/// Swaps every planned piece in one main-thread call, while the game tick is held, so no power, atmospherics or logic
/// tick sees a network half rebuilt. Each piece goes the way the game's own merge placement replaces a piece
/// (MultiMergeConstructor.Construct: Constructor.SpawnConstruct of a CreateStructureInstance, OnServer.Destroy of the
/// old piece), in an order that never leaves a gap: the replacement is built first (Thing.Create registers it on the
/// grid in the old piece's cells and joins the network through the neighbours, Cable.OnRegistered or
/// Pipe.OnRegistered), put into the old piece's network if placing it made a network of its own, then the old piece
/// leaves the network and is destroyed. Coils are taken as the kit takes them (Stackable.OnUseItem). The first failure
/// stops the loop; the log says which pieces were swapped and which piece it stopped at.
/// </summary>
internal static class UpgradeSwap
{
    internal static UpgradeSwapLog Run(UpgradePlan plan, Dictionary<long, List<SmallGrid>> replacements)
    {
        UpgradeSwapLog log = new UpgradeSwapLog();
        Dictionary<int, int> used = new Dictionary<int, int>();
        List<ItemAmount> refund = new List<ItemAmount>();
        foreach (PlannedSwap swap in plan.Swaps)
        {
            if (log.StoppedAt != null)
            {
                foreach (OldPiece old in swap.Olds)
                {
                    log.NotSwapped.Add(new ThingId(old.Piece.ReferenceId));
                }

                continue;
            }

            PieceSwap one = new PieceSwap(plan.Request.Family, swap, StockFor(plan, swap.Target));
            one.Run(log, used);
            if (one.Completed || one.Replacements.Count > 0)
            {
                replacements[swap.GroupId] = one.Replacements;
            }

            if (one.Completed)
            {
                refund.AddRange(swap.Refund);
            }
        }

        UsedOf(plan, used, log);
        if (plan.Request.Refund && plan.From != null)
        {
            DeliverRefund(plan.From, refund, log);
        }

        return log;
    }

    private static void DeliverRefund(Thing from, List<ItemAmount> refund, UpgradeSwapLog log)
    {
        try
        {
            Refunds.Deliver(from, refund, log.Refunded);
        }
        catch (Exception exception)
        {
            // OnServer.CreateOrStack and WearableItem.TryCollect after every swap is done: the swap stands, the
            // refund stops where it failed.
            StationGodMod.LogWarning($"upgrade refund failed: {exception}");
            log.RefundError = new ErrorView("refund_failed", exception.Message);
        }
    }

    private static ItemStock StockFor(UpgradePlan plan, Kit kit)
    {
        foreach (ItemStock stock in plan.Stocks)
        {
            if (stock.Item.PrefabHash == kit.Item.PrefabHash)
            {
                return stock;
            }
        }

        return ItemStock.Empty(kit.Item);
    }

    private static void UsedOf(UpgradePlan plan, Dictionary<int, int> used, UpgradeSwapLog log)
    {
        foreach (ItemStock stock in plan.Stocks)
        {
            if (used.TryGetValue(stock.Item.PrefabHash, out int quantity))
            {
                log.Used.Add(new UpgradeAmountView(stock.Item.PrefabName, quantity));
            }
        }
    }
}

/// <summary>One piece's swap, keeping what it has done so far so a failure can say exactly where it stopped.</summary>
internal sealed class PieceSwap
{
    private readonly UpgradeFamily _family;
    private readonly PlannedSwap _swap;
    private readonly ItemStock _stock;
    private bool _oldGone;

    internal PieceSwap(UpgradeFamily family, PlannedSwap swap, ItemStock stock)
    {
        _family = family;
        _swap = swap;
        _stock = stock;
    }

    /// <summary>What was built so far, in the order of the planned parts.</summary>
    internal List<SmallGrid> Replacements { get; } = new List<SmallGrid>();

    /// <summary>Every replacement stands and every old piece is gone.</summary>
    internal bool Completed => _oldGone;

    internal void Run(UpgradeSwapLog log, Dictionary<int, int> used)
    {
        try
        {
            Swap();
            int taken = _swap.Cost > 0 ? _stock.Take(_swap.Cost) : 0;
            if (taken > 0)
            {
                used[_stock.Item.PrefabHash] =
                    (used.TryGetValue(_stock.Item.PrefabHash, out int sum) ? sum : 0) + taken;
            }

            Record(log);

            if (taken < _swap.Cost)
            {
                Stop(log, "coils_short",
                    $"Only {taken} of {_swap.Cost} {_stock.Item.DisplayName} could be taken for this piece; it " +
                    "was swapped and the run stopped after it.");
            }
        }
        catch (SwapStopped stopped)
        {
            Stop(log, stopped.Code, stopped.Message);
        }
        catch (Exception exception)
        {
            // Any game call of the swap (Thing.Create, network Add or Remove, OnServer.Destroy) failing: the run stops
            // here and reports what this piece had reached.
            StationGodMod.LogWarning($"upgrade swap of {_swap.Old.ReferenceId} failed: {exception}");
            Stop(log, "swap_failed", exception.Message);
        }
    }

    // Each old piece against every new piece standing in its cells (a split's singles, a merge's long piece); a
    // removal lists its piece as removed.
    private void Record(UpgradeSwapLog log)
    {
        foreach (OldPiece old in _swap.Olds)
        {
            if (Replacements.Count == 0)
            {
                log.Removed.Add(new ThingId(old.Piece.ReferenceId));
                continue;
            }

            for (int index = 0; index < Replacements.Count && index < _swap.Parts.Count; index++)
            {
                if (Covers(_swap.Parts[index], old.Live))
                {
                    log.Swapped.Add(new UpgradeSwappedView(new ThingId(old.Piece.ReferenceId),
                        new ThingId(Replacements[index].ReferenceId), old.Piece.PrefabName,
                        Replacements[index].PrefabName));
                }
            }
        }
    }

    private static bool Covers(Twin part, PieceModel old)
    {
        foreach (GridCell cell in old.Cells)
        {
            if (part.Model.Occupies(cell))
            {
                return true;
            }
        }

        return false;
    }

    // Every part is built first (a split long straight's singles, one per cell; a merge's long pieces), each
    // overwriting the old pieces' slots in its cells; then the parts join the network, then each old piece leaves and
    // is destroyed. A removal builds nothing: its piece leaves the network (a pipe's contents stay in it) and is
    // destroyed.
    private void Swap()
    {
        GridController world = GridController.World;
        IReferencable? network = _family.NetworkOf(_swap.Old);
        foreach (Twin part in _swap.Parts)
        {
            CreateStructureInstance instance = new CreateStructureInstance(part.Prefab, _swap.Old)
            {
                LocalGrid = world.WorldToLocal(part.Position),
                LocalRotation = part.Rotation
            };
            SmallGrid? built = Constructor.SpawnConstruct(instance) as SmallGrid;
            if (built == null)
            {
                throw new SwapStopped("create_failed", $"The game built no {part.Prefab.PrefabName}.");
            }

            Replacements.Add(built);
            RequireInPlace(part, built);
        }

        if (network != null)
        {
            foreach (SmallGrid replacement in Replacements)
            {
                _family.Join(replacement, network);
            }
        }

        foreach (OldPiece old in _swap.Olds)
        {
            IReferencable? own = _family.NetworkOf(old.Piece);
            if (own != null)
            {
                _family.Leave(old.Piece, Replacements, own);
            }

            OnServer.Destroy(old.Piece);
        }

        _oldGone = true;
    }

    private void RequireInPlace(Twin part, SmallGrid built)
    {
        GridController world = GridController.World;
        foreach (GridCell cell in part.Model.Cells)
        {
            SmallCell? small = world.GetSmallCell(PieceShapes.Grid(cell));
            if (small == null || !_family.Holds(small, built))
            {
                throw new SwapStopped("placement_mismatch",
                    $"{built.PrefabName} {built.ReferenceId} is not in cell {cell} where " +
                    $"{_swap.Old.PrefabName} was; the old piece is left in place.");
            }
        }
    }

    private void Stop(UpgradeSwapLog log, string code, string message)
    {
        ThingId? replacement = Replacements.Count > 0 ? new ThingId(Replacements[0].ReferenceId) : (ThingId?)null;
        log.StoppedAt = new UpgradeStopView(new ThingId(_swap.Old.ReferenceId), new ErrorView(code, message),
            Replacements.Count == 0 && !_oldGone, replacement);
    }

    private sealed class SwapStopped : Exception
    {
        internal SwapStopped(string code, string message) : base(message)
        {
            Code = code;
        }

        internal string Code { get; }
    }
}
