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
/// leaves the network first (UpgradeFamily.Leave), so the network keeps its id and a pipe network its contents. A
/// chute never leaves first (NetworkLeaveRule): Chute.OnDestroy rebuilds its neighbours' networks whatever was done.
/// Then, once Unity has destroyed them, the build: every changed piece is replaced as the kit's merge does (the new
/// piece built into the old one's cell, put into its network, the old one leaves and is destroyed), then every new
/// piece is built as a coil's placement builds it (Constructor.SpawnConstruct; Cable.OnRegistered and
/// Pipe.OnRegistered join and merge the networks around it). Each cell's coils are taken before it is built; the
/// first failure stops the build and the log says where. After each piece the gas its merges queued is applied
/// (JobGas.Settle), so no merge copies a network whose gas is still on its way.
/// A split long straight that is the last pipe of its network (Forecast.Carried) is not removed first: the game's
/// removal of a network's last pipe deletes its contents. It is swapped in the build instead, as split_long_straights
/// swaps one: its singles are built over it first, their network is merged into its network as the game merges two
/// networks a piece joins (AtmosphericsNetwork.Merge: their gas is added), then it leaves and is destroyed, so the
/// network keeps its id and its contents; the run's new pieces are then built outward from the singles.
/// </summary>
internal static class RunBuilder
{
    /// <summary>A removal the build swaps for its singles instead of the removal step taking it away.</summary>
    internal static bool SwappedInBuild(RunPlan plan, PlannedRemoval removal) =>
        removal.Split && !removal.Assumed && removal.Network != null && plan.Request.Kind.Family is PipeFamily &&
        plan.Forecast != null && plan.Forecast.Result.Carried.Contains(removal.Network.ReferenceId);

    internal static void Remove(RunPlan plan, RunOutcome outcome)
    {
        UpgradeFamily family = plan.Request.Kind.Family;
        HashSet<long> rebuilt = Rebuilt(plan.Forecast!, family);
        foreach (PlannedRemoval removal in plan.Removals)
        {
            if (removal.Assumed || SwappedInBuild(plan, removal))
            {
                continue;
            }

            try
            {
                IReferencable? network = family.NetworkOf(removal.Piece);
                if (network != null &&
                    NetworkLeaveRule.LeavesFirst(family.OnDestroyRebuilds, !rebuilt.Contains(network.ReferenceId)))
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
            // A swapped long's refund is added once its swap is done (SwapSplit).
            if (!removal.Assumed && !SwappedInBuild(plan, removal))
            {
                refund.AddRange(removal.Refund);
            }
        }

        HashSet<SmallGrid> swapped = new HashSet<SmallGrid>();
        foreach (PlannedRemoval removal in plan.Removals)
        {
            if (outcome.Log.StoppedAt == null && SwappedInBuild(plan, removal))
            {
                swapped.Add(removal.Piece);
                SwapSplit(plan, removal, stocks, used, refund, outcome, gas);
            }
        }

        // The changed pieces first; then the new pieces grow outward from them and from a swapped long's singles, so
        // each joins the network standing there and none stands alone first to take that network over in a merge
        // (GrowthOrder).
        List<PlannedCell> ordered = plan.Cells.FindAll(static cell => cell.IsChange);
        List<PieceModel> standing = ordered.ConvertAll(static cell => cell.Model);
        standing.AddRange(plan.Cells
            .FindAll(cell => cell.SplitFrom != null && swapped.Contains(cell.SplitFrom))
            .ConvertAll(static cell => cell.Model));
        ordered.AddRange(GrowthOrder.From(standing,
            plan.Cells.FindAll(cell => !cell.IsChange && (cell.SplitFrom == null || !swapped.Contains(cell.SplitFrom))),
            static cell => cell.Model));
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

        if (plan.Request.Options.Refund && plan.Refunds != null && refund.Count > 0)
        {
            try
            {
                Refunds.Deliver(plan.Refunds, refund, outcome.Log.Refunded);
            }
            catch (Exception exception)
            {
                // OnServer.CreateOrStack after the build: the build stands, the refund stops where it failed.
                StationGodMod.LogWarning($"run refund failed: {exception}");
                outcome.Log.RefundError = new ErrorView("refund_failed", exception.Message);
            }
        }
    }

    // The long straight's singles are built over it (each overwriting its slot in that cell), then their network is
    // merged into its network and it leaves and is destroyed: the gas moves with the game's own merge, never through
    // a network with no pipe. A failure leaves the long standing (the log says where).
    private static void SwapSplit(RunPlan plan, PlannedRemoval removal, Dictionary<int, ItemStock> stocks,
        Dictionary<int, int> used, List<ItemAmount> refund, RunOutcome outcome, JobGas gas)
    {
        SmallGrid old = removal.Piece;
        List<SmallGrid> singles = new List<SmallGrid>();
        foreach (PlannedCell cell in plan.Cells.FindAll(cell => !cell.IsChange && cell.SplitFrom == old))
        {
            SmallGrid? built = BuildOne(plan, cell, stocks, used, refund, outcome, gas, old);
            if (built == null)
            {
                return;
            }

            singles.Add(built);
        }

        try
        {
            // The singles' network goes into the long's, which keeps its id as split_long_straights keeps it (the
            // forecast's networks_after names it), unless the singles joined pieces that stood before: then the
            // long's network goes into theirs, the network every client puts the singles in (PipeFamily.Unite).
            UpgradeFamily family = plan.Request.Kind.Family;
            IReferencable? kept = family.NetworkOf(old);
            IReferencable? theirs = singles.Count > 0 ? family.NetworkOf(singles[0]) : null;
            if (kept != null && theirs != null && kept != theirs)
            {
                PipeFamily.Unite(kept, theirs, singles);
                gas.Settle();
            }

            IReferencable? own = family.NetworkOf(old);
            if (own != null)
            {
                family.Leave(old, singles, own);
            }

            OnServer.Destroy(old);
            outcome.Replaced.Add(old);
            outcome.Log.Removed.Add(new ThingId(old.ReferenceId));
            refund.AddRange(removal.Refund);
        }
        catch (Exception exception)
        {
            // A network's Merge, Remove or OnServer.Destroy failing: the build stops with the long still standing.
            StationGodMod.LogWarning($"run split of {old.ReferenceId} failed: {exception}");
            outcome.Log.StoppedAt = new ErrorView("build_failed",
                $"Replacing {old.PrefabName} {old.ReferenceId} by its singles failed: {exception.Message}");
        }
    }

    private static SmallGrid? BuildOne(RunPlan plan, PlannedCell cell, Dictionary<int, ItemStock> stocks,
        Dictionary<int, int> used, List<ItemAmount> refund, RunOutcome outcome, JobGas gas, SmallGrid? over = null)
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
                        $"Only {taken} of {cell.Cost} {Names.Of(cell.Kit.Item)} could be taken for cell " +
                        $"{cell.Cell}; the build stopped before it (what was taken is given back).");
                    return null;
                }
            }

            SmallGrid built = cell.IsChange ? Change(plan, cell, gas) : Place(plan, cell, over);
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
                outcome.Log.PlacedPieces.Add(GameLookup.ViewOf(built));
            }

            return built;
        }
        catch (Exception exception)
        {
            // Thing.Create, a network's Add or Remove, or OnServer.Destroy failing: the build stops at this cell.
            StationGodMod.LogWarning($"run build at {cell.Cell} failed: {exception}");
            outcome.Log.StoppedAt = new ErrorView("build_failed", $"Cell {cell.Cell}: {exception.Message}");
            return null;
        }
    }

    // A new piece as a coil places it; over a piece still standing there (a split long straight's cell), built into
    // its slot as the kit's merge builds one, taking its owner and colour.
    private static SmallGrid Place(RunPlan plan, PlannedCell cell, SmallGrid? over)
    {
        GridController world = GridController.World;
        ulong owner = cell.Look?.Owner ?? (plan.From is Human human ? human.OwnerClientId : 0UL);
        CreateStructureInstance instance = over != null
            ? new CreateStructureInstance(cell.Choice.Prefab, over)
            {
                LocalGrid = world.WorldToLocal(PieceShapes.CentreOf(cell.Cell)),
                LocalRotation = cell.Choice.Rotation
            }
            : new CreateStructureInstance(cell.Choice.Prefab, world.WorldToLocal(PieceShapes.CentreOf(cell.Cell)),
                cell.Choice.Rotation, owner, cell.Look?.Colour ?? -1);
        SmallGrid built = Spawn(instance, cell);
        RequireInPlace(plan, cell, built);
        return built;
    }

    // The kit's merge: the new piece takes the old one's cell (owner and colour kept), joins its network, the old one
    // leaves (its devices registered through the new one) and is destroyed.
    // A new pipe with no connected neighbour but the old piece in its own cell (the only pipe of its network) is given
    // a network of its own when it registers (Pipe.OnRegistered); that network is merged into the old one's as
    // SwapSplit merges a swapped long's singles, so the old network keeps its id and its gas, instead of losing both
    // with its last pipe (pipes-23). A new piece that joined another network standing there keeps that one, which
    // takes the old network and its gas in (PipeFamily.Unite).
    private static SmallGrid Change(RunPlan plan, PlannedCell cell, JobGas gas)
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
        IReferencable? kept = family.NetworkOf(old) ?? network;
        IReferencable? theirs = family.NetworkOf(built);
        if (theirs == null && kept != null)
        {
            family.Join(built, kept);
        }
        else if (family is PipeFamily && kept != null && theirs != null && kept != theirs)
        {
            PipeFamily.Unite(kept, theirs, new List<SmallGrid> { built });
            gas.Settle();
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

    // Networks the forecast splits or empties: the game rebuilds those from the removed pieces' neighbours. A network
    // only a split long's singles carry on is emptied too, except a pipe network's, whose long the build swaps.
    internal static HashSet<long> Rebuilt(RunForecast forecast, UpgradeFamily family)
    {
        HashSet<long> rebuilt = new HashSet<long>(forecast.Result.Gone);
        if (!(family is PipeFamily))
        {
            rebuilt.UnionWith(forecast.Result.Carried);
        }

        foreach (ForecastSplit split in forecast.Result.Splits)
        {
            rebuilt.Add(split.Network);
        }

        return rebuilt;
    }
}
