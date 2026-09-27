#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game.Structures;

/// <summary>
/// Swaps every planned piece in one main-thread call while the game tick is held, so no atmospherics or room tick
/// sees a slot empty. Each piece: the old piece is marked as being destroyed (Thing.BeingDestroyed, which is what
/// lets Cell.AddStructural hand its slot to a newcomer; otherwise the newcomer is refused and destroyed), the new
/// piece is built from the old one (Constructor.SpawnConstruct of a CreateStructureInstance: position, rotation,
/// owner and colour), raised to its final build state as the authoring tool does (CurrentBuildStateIndex, which
/// updates the air state and flags the change for clients, then UpdateStateVisualizer), and only when it holds every
/// slot the old piece held and blocks what the old piece blocked is the old piece destroyed (OnServer.Destroy) and
/// the materials taken. The first failure stops the loop; the log says which pieces were swapped and where it stopped.
/// </summary>
internal static class StructureSwapper
{
    internal static StructureSwapLogView Run(StructureSwapPlan plan, Dictionary<long, Structure> replacements)
    {
        StructureSwapLogView log = new StructureSwapLogView();
        Dictionary<int, int> used = new Dictionary<int, int>();
        List<ItemAmount> refund = new List<ItemAmount>();
        foreach (PlannedStructureSwap swap in plan.Swaps)
        {
            if (log.StoppedAt != null)
            {
                log.NotSwapped.Add(new ThingId(swap.OldId));
                continue;
            }

            OneStructureSwap one = new OneStructureSwap(swap);
            one.Run(log);
            if (one.Built != null && one.OldGone)
            {
                replacements[swap.OldId] = one.Built;
                TryCharge(plan, swap, one.Built, used, log);
                foreach (MaterialLine line in swap.Materials)
                {
                    if (line.GiveBack > 0)
                    {
                        refund.Add(new ItemAmount(plan.Items[line.Item], line.GiveBack));
                    }
                }
            }
        }

        foreach (KeyValuePair<int, int> item in used)
        {
            log.Used.Add(new UpgradeAmountView(plan.Items[item.Key].PrefabName, item.Value));
        }

        if (plan.Request.Arguments.Refund && plan.From != null && refund.Count > 0)
        {
            DeliverRefund(plan.From, refund, log);
        }

        return log;
    }

    private static void TryCharge(StructureSwapPlan plan, PlannedStructureSwap swap, Structure built,
        Dictionary<int, int> used, StructureSwapLogView log)
    {
        try
        {
            Charge(plan, swap, built, used, log);
        }
        catch (Exception exception)
        {
            // Stackable.OnUseItem after the piece is swapped: the swap stands, the run stops after it.
            StationGodMod.LogWarning($"replace charge for {swap.OldId} failed: {exception}");
            if (log.StoppedAt == null)
            {
                log.StoppedAt = new StructureStopView(new ThingId(swap.OldId),
                    new ErrorView("materials_failed", exception.Message), false, false,
                    new ThingId(built.ReferenceId));
            }
        }
    }

    // Each item the swap needs beyond what the old piece returns, taken as a kit's placement takes it. A shortfall
    // (the stock changed since the check) leaves the swap standing and stops the run after it.
    private static void Charge(StructureSwapPlan plan, PlannedStructureSwap swap, Structure built,
        Dictionary<int, int> used, StructureSwapLogView log)
    {
        foreach (MaterialLine line in swap.Materials)
        {
            if (line.Charge <= 0)
            {
                continue;
            }

            ItemStock? stock = plan.StockOf(line.Item);
            int taken = stock?.Take(line.Charge) ?? 0;
            used[line.Item] = (used.TryGetValue(line.Item, out int sum) ? sum : 0) + taken;
            if (taken < line.Charge && log.StoppedAt == null)
            {
                log.StoppedAt = new StructureStopView(new ThingId(swap.OldId), new ErrorView("materials_short",
                        $"Only {taken} of {line.Charge} {plan.Items[line.Item].DisplayName} could be taken for this " +
                        "piece; it was swapped and the run stopped after it."), false, false,
                    new ThingId(built.ReferenceId));
            }
        }
    }

    private static void DeliverRefund(Thing from, List<ItemAmount> refund, StructureSwapLogView log)
    {
        try
        {
            Refunds.Deliver(from, refund, log.Refunded);
        }
        catch (Exception exception)
        {
            // OnServer.CreateOrStack and WearableItem.TryCollect after every swap is done: the swap stands, the
            // refund stops where it failed.
            StationGodMod.LogWarning($"replace refund failed: {exception}");
            log.RefundError = new ErrorView("refund_failed", exception.Message);
        }
    }
}

/// <summary>
/// One piece's swap and what it reached, so a failure can be undone or reported exactly. It never leaves a hole:
/// until the new piece holds every slot and blocks what the old one blocked, the old piece still exists (only marked
/// as being destroyed), and a failure takes the new piece away and gives the old one its slots back.
/// </summary>
internal sealed class OneStructureSwap
{
    private readonly PlannedStructureSwap _swap;

    internal OneStructureSwap(PlannedStructureSwap swap)
    {
        _swap = swap;
    }

    internal Structure? Built { get; private set; }

    internal bool OldGone { get; private set; }

    internal void Run(StructureSwapLogView log)
    {
        Structure old = _swap.Old;
        try
        {
            old.BeingDestroyed = true;
            Built = Constructor.SpawnConstruct(new CreateStructureInstance(_swap.Target, old));
            if (Built == null || !StructureSlots.HoldsAll(_swap.Slots, Built))
            {
                RollBack(log, Built == null ? "create_failed" : "placement_mismatch",
                    Built == null
                        ? $"The game built no {_swap.Target.PrefabName}."
                        : $"{Built.PrefabName} {Built.ReferenceId} did not take every slot of {old.PrefabName}.");
                return;
            }

            Built.CurrentBuildStateIndex = Built.BuildStates.Count - 1;
            Built.UpdateStateVisualizer();
            if (!StructureSlots.HoldsAll(_swap.Slots, Built) || OpensWhatOldBlocked(Built))
            {
                RollBack(log, "not_sealed",
                    $"{Built.PrefabName} {Built.ReferenceId} at its final state does not hold every slot or does not " +
                    $"block what {old.PrefabName} blocked.");
                return;
            }

            Finish(log);
        }
        catch (Exception exception)
        {
            // Any game call of the swap (Thing.Create, the build state, OnServer.Destroy) failing part way.
            StationGodMod.LogWarning($"replace swap of {_swap.OldId} failed: {exception}");
            Recover(log, exception);
        }
    }

    private void Finish(StructureSwapLogView log)
    {
        OnServer.Destroy(_swap.Old);
        OldGone = true;
        log.Swapped.Add(new UpgradeSwappedView(new ThingId(_swap.OldId), new ThingId(Built!.ReferenceId),
            _swap.Old.PrefabName, Built.PrefabName));
    }

    // After an exception: a new piece that stands in every slot and seals as the old one did is kept (the old one is
    // destroyed and the run stops); anything less is rolled back.
    private void Recover(StructureSwapLogView log, Exception exception)
    {
        try
        {
            if (Built == null)
            {
                Built = NewcomerInSlots();
            }

            if (OldGone)
            {
                Stop(log, "swap_failed", exception.Message, false, false);
                return;
            }

            if (Built != null && !Built.IsBeingDestroyed && StructureSlots.HoldsAll(_swap.Slots, Built) &&
                !OpensWhatOldBlocked(Built))
            {
                Finish(log);
                Stop(log, "swap_failed",
                    $"{exception.Message} The new piece stands in every slot and was kept; the old one was removed.",
                    false, false);
                return;
            }

            RollBack(log, "swap_failed", exception.Message);
        }
        catch (Exception again)
        {
            StationGodMod.LogWarning($"replace recovery of {_swap.OldId} failed: {again}");
            Stop(log, "rollback_failed",
                $"{exception.Message} Then restoring the old piece failed: {again.Message}. Check " +
                $"{_swap.Old.PrefabName} {_swap.OldId} in game.", false, Built != null);
        }
    }

    // The new piece goes; the old one is no longer being destroyed and gets back every slot it lost (the new piece
    // is marked as being destroyed first, so registration hands the slot back, and its own deregistration at the end
    // of the frame then leaves the slot alone, GridController.RemoveGridStructure).
    private void RollBack(StructureSwapLogView log, string code, string message)
    {
        bool built = Built != null;
        if (Built != null && !Built.IsBeingDestroyed)
        {
            OnServer.Destroy(Built);
        }

        _swap.Old.BeingDestroyed = false;
        bool restored = StructureSlots.Restore(_swap.Old, _swap.Slots);
        Built = null;
        if (!restored)
        {
            Stop(log, "rollback_failed",
                $"{message} Restoring {_swap.Old.PrefabName} {_swap.OldId} into its slots failed: check its faces " +
                $"({DescribeSlots()}) in game.", false, built);
            return;
        }

        Stop(log, code, message + " The old piece was restored; nothing changed there.", true, built);
    }

    private bool OpensWhatOldBlocked(Structure built) =>
        (_swap.Before.Air && built.CanAirPass) || (_swap.Before.Gravity && built.CanGravityPass);

    // What a failed SpawnConstruct left in the old piece's slots: a piece of the target prefab that is not the old one.
    private Structure? NewcomerInSlots()
    {
        foreach (StructureSlot slot in _swap.Slots)
        {
            Structure? holder = StructureSlots.Holder(slot);
            if (holder != null && holder != _swap.Old && holder.PrefabHash == _swap.Target.PrefabHash)
            {
                return holder;
            }
        }

        return null;
    }

    private void Stop(StructureSwapLogView log, string code, string message, bool intact, bool rolledBack)
    {
        ThingId? replacement = Built != null && OldGone ? new ThingId(Built.ReferenceId) : (ThingId?)null;
        log.StoppedAt = new StructureStopView(new ThingId(_swap.OldId), new ErrorView(code, message), intact,
            rolledBack, replacement);
    }

    private string DescribeSlots()
    {
        List<string> parts = new List<string>(_swap.Slots.Count);
        foreach (StructureSlot slot in _swap.Slots)
        {
            parts.Add(slot.ToString());
        }

        return string.Join("; ", parts);
    }
}
