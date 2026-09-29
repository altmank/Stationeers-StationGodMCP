#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game.Runs;

/// <summary>What a run has built so far: each built piece by its forecast id, and the old pieces it replaced.</summary>
internal sealed class RunOutcome
{
    internal RunLogView Log { get; } = new RunLogView();

    /// <summary>Forecast id (negative for a new piece, the old id for a change) to what stands there now.</summary>
    internal Dictionary<long, SmallGrid> Built { get; } = new Dictionary<long, SmallGrid>();

    /// <summary>Old pieces a change replaced, waiting for Unity to destroy them.</summary>
    internal List<SmallGrid> Replaced { get; } = new List<SmallGrid>();
}

/// <summary>
/// The two steps of a confirmed run, each in one frame while the game tick is held. Removals first: each piece is
/// destroyed as a player's deconstruction does (OnServer.Destroy; Cable.OnDestroy and Pipe.OnDestroy rebuild the
/// networks from its neighbours), except that where the forecast keeps its network whole (nothing splits), the piece
/// leaves the network first (UpgradeFamily.Leave), so the network keeps its id and a pipe network its contents.
/// Then, once Unity has destroyed them, the build: every changed piece is replaced as the kit's merge does (the new
/// piece built into the old one's cell, put into its network, the old one leaves and is destroyed), then every new
/// piece is built as a coil's placement builds it (Constructor.SpawnConstruct; Cable.OnRegistered and
/// Pipe.OnRegistered join and merge the networks around it). Each cell's coils are taken before it is built; the
/// first failure stops the build and the log says where. After each piece the gas its merges queued is applied
/// (JobGas.Settle), so no merge copies a network whose gas is still on its way.
/// </summary>
internal static class RunBuilder
{
    internal static void Remove(RunPlan plan, RunOutcome outcome)
    {
        UpgradeFamily family = plan.Request.Kind.Family;
        HashSet<long> rebuilt = Rebuilt(plan.Forecast!);
        foreach (PlannedRemoval removal in plan.Removals)
        {
            if (removal.Assumed)
            {
                continue;
            }

            try
            {
                IReferencable? network = family.NetworkOf(removal.Piece);
                if (network != null && !rebuilt.Contains(network.ReferenceId))
                {
                    family.Leave(removal.Piece, new List<SmallGrid>(), network);
                }

                OnServer.Destroy(removal.Piece);
                outcome.Log.Removed.Add(new ThingId(removal.Piece.ReferenceId));
            }
            catch (Exception exception)
            {
                // OnServer.Destroy or a network's Remove failing: stop here, nothing is built.
                StationGodMod.LogWarning($"run removal of {removal.Piece.ReferenceId} failed: {exception}");
                outcome.Log.StoppedAt = new ErrorView("remove_failed",
                    $"Removing {removal.Piece.PrefabName} {removal.Piece.ReferenceId} failed: {exception.Message}");
                return;
            }
        }
    }

    internal static void Build(RunPlan plan, RunOutcome outcome, JobGas gas)
    {
        Dictionary<int, ItemStock> stocks = new Dictionary<int, ItemStock>();
        foreach (ItemStock stock in plan.Stocks)
        {
            stocks[stock.Item.PrefabHash] = stock;
        }

        Dictionary<int, int> used = new Dictionary<int, int>();
        List<ItemAmount> refund = new List<ItemAmount>();
        foreach (PlannedRemoval removal in plan.Removals)
        {
            if (!removal.Assumed)
            {
                refund.AddRange(removal.Refund);
            }
        }

        List<PlannedCell> ordered = plan.Cells.FindAll(static cell => cell.IsChange);
        ordered.AddRange(plan.Cells.FindAll(static cell => !cell.IsChange));
        foreach (PlannedCell cell in ordered)
        {
            if (outcome.Log.StoppedAt != null)
            {
                break;
            }

            BuildOne(plan, cell, stocks, used, refund, outcome, gas);
        }

        foreach (ItemStock stock in plan.Stocks)
        {
            if (used.TryGetValue(stock.Item.PrefabHash, out int quantity))
            {
                outcome.Log.Used.Add(new UpgradeAmountView(stock.Item.PrefabName, quantity));
            }
        }

        if (plan.Request.Options.Refund && plan.From != null && refund.Count > 0)
        {
            try
            {
                Refunds.Deliver(plan.From, refund, outcome.Log.Refunded);
            }
            catch (Exception exception)
            {
                // OnServer.CreateOrStack after the build: the build stands, the refund stops where it failed.
                StationGodMod.LogWarning($"run refund failed: {exception}");
                outcome.Log.RefundError = new ErrorView("refund_failed", exception.Message);
            }
        }
    }

    private static void BuildOne(RunPlan plan, PlannedCell cell, Dictionary<int, ItemStock> stocks,
        Dictionary<int, int> used, List<ItemAmount> refund, RunOutcome outcome, JobGas gas)
    {
        try
        {
            if (cell.Cost > 0)
            {
                ItemStock stock = stocks.TryGetValue(cell.Kit.Item.PrefabHash, out ItemStock found)
                    ? found
                    : ItemStock.Empty(cell.Kit.Item);
                int taken = stock.Take(cell.Cost);
                used[cell.Kit.Item.PrefabHash] = (used.TryGetValue(cell.Kit.Item.PrefabHash, out int sum) ? sum : 0) +
                                                 taken;
                if (taken < cell.Cost)
                {
                    refund.Add(new ItemAmount(cell.Kit.Item, taken));
                    outcome.Log.StoppedAt = new ErrorView("coils_short",
                        $"Only {taken} of {cell.Cost} {cell.Kit.Item.DisplayName} could be taken for cell " +
                        $"{cell.Cell}; the build stopped before it (what was taken is given back).");
                    return;
                }
            }

            SmallGrid built = cell.IsChange ? Change(plan, cell) : Place(plan, cell);
            outcome.Built[cell.ForecastId] = built;
            // The gas this piece's merges queued lands before the next piece can merge the survivor away (JobGas).
            gas.Settle();
            string part = RunShape.BuiltPart(plan.Request.Build?.Shape, cell.Cell, cell.IsChange);
            outcome.Log.AddCreated(part, new ThingId(built.ReferenceId));
            if (cell.IsChange)
            {
                outcome.Replaced.Add(cell.Existing!);
                outcome.Log.Changed.Add(new UpgradeSwappedView(new ThingId(cell.Existing!.ReferenceId),
                    new ThingId(built.ReferenceId), cell.Existing.PrefabName, built.PrefabName));
                refund.AddRange(cell.Refund);
            }
            else
            {
                outcome.Log.Placed.Add(GameLookup.ViewOf(built));
            }
        }
        catch (Exception exception)
        {
            // Thing.Create, a network's Add or Remove, or OnServer.Destroy failing: the build stops at this cell.
            StationGodMod.LogWarning($"run build at {cell.Cell} failed: {exception}");
            outcome.Log.StoppedAt = new ErrorView("build_failed", $"Cell {cell.Cell}: {exception.Message}");
        }
    }

    private static SmallGrid Place(RunPlan plan, PlannedCell cell)
    {
        GridController world = GridController.World;
        ulong owner = cell.Look?.Owner ?? (plan.From is Human human ? human.OwnerClientId : 0UL);
        CreateStructureInstance instance = new CreateStructureInstance(cell.Choice.Prefab,
            world.WorldToLocal(PieceShapes.CentreOf(cell.Cell)), cell.Choice.Rotation, owner,
            cell.Look?.Colour ?? -1);
        SmallGrid built = Spawn(instance, cell);
        RequireInPlace(plan, cell, built);
        return built;
    }

    // The kit's merge: the new piece takes the old one's cell (owner and colour kept), joins its network, the old one
    // leaves (its devices registered through the new one) and is destroyed.
    private static SmallGrid Change(RunPlan plan, PlannedCell cell)
    {
        UpgradeFamily family = plan.Request.Kind.Family;
        SmallGrid old = cell.Existing!;
        IReferencable? network = family.NetworkOf(old);
        CreateStructureInstance instance = new CreateStructureInstance(cell.Choice.Prefab, old)
        {
            LocalGrid = GridController.World.WorldToLocal(PieceShapes.CentreOf(cell.Cell)),
            LocalRotation = cell.Choice.Rotation
        };
        SmallGrid built = Spawn(instance, cell);
        RequireInPlace(plan, cell, built);
        if (family.NetworkOf(built) == null && network != null)
        {
            family.Join(built, network);
        }

        IReferencable? own = family.NetworkOf(old);
        if (own != null)
        {
            family.Leave(old, new List<SmallGrid> { built }, own);
        }

        OnServer.Destroy(old);
        return built;
    }

    private static SmallGrid Spawn(CreateStructureInstance instance, PlannedCell cell)
    {
        SmallGrid? built = Constructor.SpawnConstruct(instance) as SmallGrid;
        if (built == null)
        {
            throw new InvalidOperationException($"The game built no {cell.Choice.Prefab.PrefabName}.");
        }

        return built;
    }

    private static void RequireInPlace(RunPlan plan, PlannedCell cell, SmallGrid built)
    {
        SmallCell? small = GridController.World.GetSmallCell(PieceShapes.Grid(cell.Cell));
        if (small == null || !plan.Request.Kind.Family.Holds(small, built))
        {
            throw new InvalidOperationException(
                $"{built.PrefabName} {built.ReferenceId} is not registered in cell {cell.Cell}.");
        }
    }

    // Networks the forecast splits or empties: the game rebuilds those from the removed pieces' neighbours.
    private static HashSet<long> Rebuilt(RunForecast forecast)
    {
        HashSet<long> rebuilt = new HashSet<long>(forecast.Result.Gone);
        foreach (ForecastSplit split in forecast.Result.Splits)
        {
            rebuilt.Add(split.Network);
        }

        return rebuilt;
    }
}
