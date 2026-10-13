#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Chutes;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Shaping;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>Moves items only on the host, whose game runs the simulation.</summary>
internal static class HostOnly
{
    internal static void Require()
    {
        if (NetworkManager.IsClient || !GameManager.RunSimulation)
        {
            throw ApiErrors.Refused("not_host", "This game is a multiplayer client; only the host moves items.");
        }
    }
}

/// <summary>
/// What a deposit into a store names: things by id (each whole, or a part of its stack), or a filter over the world's
/// items, at most AtMost of them, nearest the player first.
/// </summary>
internal sealed class DepositAsk
{
    private DepositAsk(List<DepositEntry>? entries, ItemFilter? filter, int atMost)
    {
        Entries = entries;
        Filter = filter;
        AtMost = atMost;
    }

    /// <summary>The things by id; null for the filter form.</summary>
    internal List<DepositEntry>? Entries { get; }

    internal ItemFilter? Filter { get; }

    internal int AtMost { get; }

    internal static DepositAsk ById(List<DepositEntry> entries) => new DepositAsk(entries, null, entries.Count);

    internal static DepositAsk Filtered(ItemFilter filter, int atMost) => new DepositAsk(null, filter, atMost);
}

/// <summary>
/// How many things a deposit takes when at_most is left out (D-A3-10): a vault 256, a silo 64. A filter that keeps
/// more is cut there and the cut is noted (Truncations), as the most at_most allows.
/// </summary>
internal static class DepositLimits
{
    internal const int Vault = ReplyDefaults.VaultDepositItems;
    internal const int Silo = ReplyDefaults.SiloDepositItems;
    internal const int Most = 1000;

    internal static void NoteCut(int taken, int matched) =>
        Truncations.Capped("items.results", taken, matched, "at_most", Most);
}

/// <summary>The holder a move out of a store makes the items into: anything with slots but a vault or silo.</summary>
internal static class StoreHolders
{
    internal static Thing Require(Thing holder)
    {
        if (holder.IsBeingDestroyed)
        {
            throw ApiErrors.ThingNotFound(new ThingId(holder.ReferenceId));
        }

        if (holder is Silo || IngotVaults.IsVault(holder) || IngotVaults.IsRemote(holder))
        {
            throw ApiErrors.Refused("invalid_destination",
                "A silo's or vault's slots are its import, export and display slots; name the holder that should get " +
                "the items.");
        }

        return holder.Slots != null && holder.Slots.Count > 0
            ? holder
            : throw ApiErrors.Refused("no_slots", $"{Names.Of(holder)} has no slots.");
    }
}

/// <summary>
/// Into an Ingot Vault's store, from wherever the items are (a player at any depth, a container, the ground), as a
/// move: what leaves the source is what the store gains, nothing is made or lost, and no slot, chute or door is
/// involved. Needs IngotVault.
///
/// The vault's own import (CODE, IngotVault 1.2.0): an item reaches the Import slot only when the slot's class is None
/// or the item's SlotType (DeviceImport.TryChuteImport), is kept only when it is an Ingot or an Ore (Ice is an Ore;
/// OnChildEnterInventory), and otherwise is ejected. CollectResource then adds an ingot's CreatedReagentMixture times
/// its Quantity to the vault's ReagentMixture, or an ore's stack Quantity to _storedOresAndIces[prefab hash] (flagging
/// NetworkUpdateFlags 0x100), destroys the item (OnServer.Destroy) and refreshes the display. This does the same
/// bookkeeping with the same calls, and for part of a stack takes the part off the source the way the trade window
/// trims a stack (RemoveQuantity; Consumable.Quantity for an ingot's grams) instead of destroying it. act false checks
/// every item and changes nothing.
/// </summary>
internal static class VaultDeposits
{
    internal static VaultDepositView Deposit(ThingId vault, DepositAsk ask, VaultMaterial kind, bool act)
    {
        HostOnly.Require();
        IngotVaults.RequireLoaded();
        VaultTarget target = VaultTarget.Resolve(vault);
        DepositPicks picks = Picks(ask, kind);
        target.RequirePowered();
        VaultStore store = target.Store();
        VaultLedger ledger = new VaultLedger(store);
        BatchBuilder batch = new BatchBuilder(picks.Entries.Count);
        HashSet<long> seen = new HashSet<long>();
        List<DepositPlan> plans = new List<DepositPlan>(picks.Entries.Count);
        for (int index = 0; index < picks.Entries.Count; index++)
        {
            DepositEntry entry = picks.Entries[index];
            if (!DepositPlan.TryPlan(entry, target, seen, out DepositPlan? plan, out ApiException? refusal))
            {
                batch.Failed(new NotDepositedView(index, entry.Id, entry.PrefabName, refusal!));
                continue;
            }

            plan!.Record(ledger);
            plans.Add(plan);
            batch.Succeeded(plan.View(index));
        }

        if (act && plans.Count > 0)
        {
            foreach (DepositPlan plan in plans)
            {
                plan.Apply(store);
            }

            store.Refresh();
        }

        return new VaultDepositView(target.View(), picks.Matched, picks.Skipped, picks.Truncated, batch.Build(),
            ledger.Views(readBack: act));
    }

    private static DepositPicks Picks(DepositAsk ask, VaultMaterial kind)
    {
        if (ask.Entries != null)
        {
            return DepositPicks.ById(ask.Entries);
        }

        DepositPicks picks = DepositPicks.Nearest(ask.Filter!, kind, ask.AtMost);
        DepositLimits.NoteCut(picks.Entries.Count, picks.Taken);
        return picks;
    }
}

/// <summary>
/// An amount of one stored thing out of an Ingot Vault's store, made straight into a holder's slots, as a move: what
/// the store loses is what the holder gains. No vend queue, export slot or door is involved, so nothing is dropped in
/// front of the vault. Needs IngotVault.
///
/// The vault's own vend (CODE, IngotVault 1.2.0): TryVendSelected takes min(stock, stack size) off the store (an
/// ingot's reagent Set to what is left; an ore entry removed when 0.01 or less is left, NetworkUpdateFlags 0x100) and
/// queues it; OnServerTick makes the item (the ingot GetPrefabHashForReagent names, or the ore's prefab) with
/// Quantity = the amount, at most the prefab's MaxQuantity per item, into the Export slot. This takes the store down the
/// same way (VaultStock.Take) and makes the items with OnServer.Create into the holder's slots, as a fabricator's
/// output is made, topping up matching stacks first (Delivery). The ground is used only when allowGround.
/// </summary>
internal static class VaultWithdrawals
{
    internal static VaultWithdrawView Withdraw(ThingId vault, string? prefabName, int? prefabHash, string? reagent,
        double quantity, Thing holder, bool allowGround, bool act)
    {
        HostOnly.Require();
        IngotVaults.RequireLoaded();
        VaultTarget target = VaultTarget.Resolve(vault);
        VaultStore store = target.Store();
        VaultStock stock = WithdrawStock.Find(store, prefabName, prefabHash, reagent);
        Item? prefab = stock.ItemPrefab;
        if (prefab == null)
        {
            throw ApiErrors.Refused("prefab_missing", $"No item prefab {stock.PrefabHash} exists to make.");
        }

        RequireAmount(store, stock, prefab, quantity);
        StoreHolders.Require(holder);
        target.RequirePowered();
        Delivery delivery = Delivery.Survey(holder, new SlotChoice.Auto(), prefab);
        StackPlan plan = StackPlacement.Plan(quantity, Delivery.FullStack(prefab), delivery.Rooms,
            delivery.Empty.Count, allowGround);
        if (!plan.Fits)
        {
            throw ApiErrors.Refused("no_room",
                $"{Names.Of(holder)} takes {VaultAmount.Of(plan.Placed)} of {quantity} {Names.Of(prefab)}; free a " +
                "slot, name another holder, or pass allow: [\"ground\"] for the rest. Nothing was changed.");
        }

        VaultLedger ledger = new VaultLedger(store);
        ledger.Record(stock, -quantity);
        List<PlacedView> placed = act
            ? delivery.Deliver(plan, prefab, new VaultSource(store, stock), quantity)
            : delivery.Preview(plan);
        return new VaultWithdrawView(target.View(), GameLookup.ViewOf(holder), quantity, placed,
            ledger.Views(readBack: act));
    }

    private static void RequireAmount(VaultStore store, VaultStock stock, Item prefab, double quantity)
    {
        double amount = stock.AmountIn(store);
        if (quantity > amount + VaultStore.Trace)
        {
            throw ApiErrors.Refused("not_enough_stock",
                $"The vault holds {VaultAmount.Of(amount)} {Names.Of(prefab)}; cannot take {quantity}.");
        }

        if (stock.CountsWhole && Math.Abs(quantity - Math.Round(quantity)) > VaultStore.Trace)
        {
            throw ApiErrors.InvalidArgument($"{Names.Of(prefab)} counts whole items; quantity {quantity} is not whole.");
        }
    }
}

/// <summary>
/// Stored amounts from one Ingot Vault's store straight into another's, as the vaults' own bookkeeping: an ingot line's
/// reagent grams off one ReagentMixture onto the other, an ore or ice line's count off one _storedOresAndIces onto the
/// other. No item is made, no slot, chute, export or locker is involved, so nothing sits in an atmosphere on the way
/// (ices cannot melt). Needs IngotVault.
///
/// The vault's own sides (CODE, IngotVaultFix StructureIngotVault): TryVendSelected takes a line off the store and an
/// ore line flags MarkVaultStateChanged (NetworkUpdateFlags 256); CollectResource/ResourceImport.Store adds an ingot's
/// reagents to the ReagentMixture (no flag) or an ore's count to the dictionary (flag 256), then UpdateDisplay, which
/// also resizes the logic slots. VaultStock.Take and Add do the same on each side, and both vaults are refreshed
/// through PreviousContentsShow (UpdateDisplay). Nothing in the game reads flag 256 for a DeviceImportExport (only
/// Structure's 64 is built into its updates), and a joined client receives the store only in SerializeOnJoin
/// (Thing writes the ReagentMixture, the vault its ore dictionary): a transfer reaches clients exactly as the vault's
/// own imports and vends do. An ice's gas is not kept by the store (the import counts the stack and destroys it), so a
/// transfer moves counts only and loses nothing more. asks null moves every line.
/// </summary>
internal static class VaultTransfers
{
    internal static VaultTransferView Transfer(ThingId from, ThingId to, List<TransferAsk>? asks, bool act)
    {
        HostOnly.Require();
        IngotVaults.RequireLoaded();
        if (from.Equals(to))
        {
            throw ApiErrors.Refused("same_vault", "source and to name the same vault; nothing to move.");
        }

        VaultTarget source = VaultTarget.Resolve(from);
        VaultTarget target = VaultTarget.Resolve(to);
        if (source.Vault == target.Vault)
        {
            throw ApiErrors.Refused("same_vault",
                $"{from} and {to} both reach {Names.Of(source.Vault)} ({source.Vault.ReferenceId}); nothing to move.");
        }

        source.RequirePowered();
        target.RequirePowered();
        VaultStore sourceStore = source.Store();
        VaultStore targetStore = target.Store();
        List<VaultStock> stocks = sourceStore.Lines();
        List<TransferOutcome> outcomes = VaultTransferRule.Plan(LinesOf(stocks, sourceStore, targetStore), asks);
        BatchBuilder batch = new BatchBuilder(outcomes.Count);
        int partial = 0;
        bool moved = false;
        foreach (TransferOutcome outcome in outcomes)
        {
            switch (outcome)
            {
                case TransferOutcome.Move move:
                    VaultStock stock = stocks[move.Line];
                    batch.Succeeded(act
                        ? Moved(move, stock, sourceStore, targetStore)
                        : View(move, stock, move.SourceAfter, move.TargetBefore, move.TargetAfter));
                    moved = true;
                    partial += move.Partial ? 1 : 0;
                    break;
                case TransferOutcome.Refusal refusal:
                    batch.Failed(new NotTransferredView(refusal.Index, refusal.Asked,
                        new ErrorView(refusal.Code, refusal.Message)));
                    break;
            }
        }

        if (act && moved)
        {
            sourceStore.Refresh();
            targetStore.Refresh();
        }

        return new VaultTransferView(source.View(), target.View(), batch.Build(), partial);
    }

    private static List<TransferLine> LinesOf(List<VaultStock> stocks, VaultStore source, VaultStore target)
    {
        List<TransferLine> lines = new List<TransferLine>(stocks.Count);
        foreach (VaultStock stock in stocks)
        {
            VaultItemView view = stock.View();
            string? reagentDisplay = stock is VaultStock.IngotStock ingot ? ingot.Reagent.DisplayName : null;
            lines.Add(new TransferLine(view.PrefabName, stock.PrefabHash, view.Reagent, reagentDisplay,
                stock.CountsWhole, stock.AmountIn(source), stock.AmountIn(target)));
        }

        return lines;
    }

    // Take off the source as a vend does, add to the target as an import does, read both back.
    private static TransferredView Moved(TransferOutcome.Move move, VaultStock stock, VaultStore source,
        VaultStore target)
    {
        double targetBefore = stock.AmountIn(target);
        stock.Take(source, move.Quantity);
        try
        {
            stock.Add(target, move.Quantity);
        }
        catch (Exception)
        {
            stock.Add(source, move.Quantity);
            throw;
        }

        return View(move, stock, stock.AmountIn(source), targetBefore, stock.AmountIn(target));
    }

    private static TransferredView View(TransferOutcome.Move move, VaultStock stock, double sourceAfter,
        double targetBefore, double targetAfter) =>
        new TransferredView(move.Index, stock.View(), move.Requested, move.Quantity, move.Partial,
            new TransferSideView(move.SourceBefore, sourceAfter), new TransferSideView(targetBefore, targetAfter));
}

/// <summary>
/// Into an SDB Silo's store from wherever the things are (a player at any depth, a container, the ground), each as one
/// entry made as the silo's import makes it (SiloSaver: the thing's save data and everything it holds, then the things
/// destroyed), at the back of the store. No chute, import slot or door is involved. For part of a stack the part is
/// stored and taken off the source stack.
///
/// What the silo takes (CODE): a thing reaches the import slot from a chute (DeviceImport.TryChuteImport, which takes
/// any occupant) or from the input trigger, which takes an Item whose SlotType is the import slot's class, or any Item
/// when that class is None (DeviceImport.OnInputTriggerEnter, DeviceImport.cs:381); this applies the trigger's rule.
/// The silo refuses an import when it holds 600 entries (CanBeginImport, Silo.cs:77); this counts an import the silo
/// is saving as taken.
/// </summary>
internal static class SiloDeposits
{
    internal static SiloDepositView Deposit(ThingId siloId, DepositAsk ask, bool allowInUse, bool act)
    {
        HostOnly.Require();
        SiloStore store = SiloStore.Of(Silos.Resolve(siloId));
        store.RequirePowered();
        SiloDepositList list = ListOf(ask, store.Silo, allowInUse);
        int room = SiloRules.Room(store.Entries.Count, store.Importing);
        BatchBuilder batch = new BatchBuilder(list.Picks.Count);
        List<SiloDepositPlan> plans = new List<SiloDepositPlan>(list.Picks.Count);
        HashSet<long> named = new HashSet<long>();
        for (int index = 0; index < list.Picks.Count; index++)
        {
            SiloDepositPick pick = list.Picks[index];
            ApiException? refusal = SiloDepositPlan.TryPlan(pick, store.Silo, named, plans, allowInUse,
                out SiloDepositPlan? plan);
            if (refusal == null && plans.Count >= room)
            {
                refusal = ApiErrors.Refused("silo_full",
                    $"{Names.Of(store.Silo)} holds {store.Entries.Count} of {SiloRules.Capacity} entries" +
                    (store.Importing ? " and is saving an import" : string.Empty) +
                    $"; {room} more fit, and this deposit already takes them.");
            }

            if (refusal != null)
            {
                batch.Failed(new NotDepositedView(index, pick.Id, list.PrefabOf(index), refusal));
                continue;
            }

            batch.Succeeded(plan!.View(index, store.Entries.Count + plans.Count));
            plans.Add(plan);
        }

        int before = store.Entries.Count;
        if (act && plans.Count > 0)
        {
            Store(store, plans);
        }

        int after = act ? SiloStore.Of(store.Silo).Entries.Count : before + plans.Count;
        return new SiloDepositView(store.View(), list.Matched, list.Skipped, list.Truncated, batch.Build(),
            new SiloCountView(before, after));
    }

    private static SiloDepositList ListOf(DepositAsk ask, Silo silo, bool allowInUse)
    {
        if (ask.Entries != null)
        {
            List<SiloDepositPick> picks = new List<SiloDepositPick>(ask.Entries.Count);
            foreach (DepositEntry entry in ask.Entries)
            {
                picks.Add(new SiloDepositPick(entry.Id, entry.Quantity));
            }

            return SiloDepositList.ById(picks);
        }

        SiloDepositList list = SiloDepositList.Nearest(ask.Filter!, ask.AtMost, silo, allowInUse);
        DepositLimits.NoteCut(list.Picks.Count, list.Taken);
        return list;
    }

    // Each plan becomes an entry at the back; the store is written even when a game call fails part way, so every
    // thing already destroyed is in it.
    private static void Store(SiloStore store, List<SiloDepositPlan> plans)
    {
        List<StoredThings> entries = new List<StoredThings>(store.Entries);
        try
        {
            foreach (SiloDepositPlan plan in plans)
            {
                entries.Add(plan.Apply());
            }
        }
        finally
        {
            store.Replace(entries);
        }
    }
}

/// <summary>
/// Items of one prefab out of an SDB Silo's store straight into a holder's slots, with the silo's own bookkeeping, as
/// a move: what the store loses is what the holder gains. No export slot or door is involved, so nothing is dropped in
/// front of the silo.
///
/// Entries are taken front to back, the silo's export order (Silo.BeginNextExport, Silo.cs:211). A stack entry holding
/// nothing that does not spoil is pooled: the amount is made as items into the holder as a vault's are made
/// (Delivery: matching stacks topped up first, then new full stacks into empty slots), and the store goes down entry
/// by entry, the last one keeping the rest (its StackableSaveData or ConsumableSaveData Quantity). Any other entry (a
/// thing that is not a stack, holds things, or is food) is loaded whole, contents and all, into an empty slot as the
/// silo's export loads it (SiloLoader), food rotten as the export makes it. After the store changes the silo refreshes
/// as after its own export (SiloStore.Replace). What a failed game call did not make stays in the store.
/// </summary>
internal static class SiloWithdrawals
{
    internal static SiloWithdrawView Withdraw(ThingId siloId, SiloPrefabChoice prefab, double quantity, Thing holder,
        bool allowGround, bool act)
    {
        HostOnly.Require();
        SiloStore store = SiloStore.Of(Silos.Resolve(siloId));
        List<SiloEntry> entries = store.Describe();
        SiloPick.Picked pick = SiloPicks.Choose(store, entries, prefab, quantity);
        SiloEntry first = entries[pick.FirstIndex];
        DynamicThing? template = first.Template;
        if (template == null)
        {
            throw ApiErrors.Refused("prefab_missing",
                $"This game has no prefab {first.PrefabName} to make the stored thing from.");
        }

        if (template is Stackable && Math.Abs(quantity - Math.Round(quantity)) > SiloPick.Trace)
        {
            throw ApiErrors.InvalidArgument($"{Names.Of(template)} counts whole items; quantity {quantity} is not whole.");
        }

        StoreHolders.Require(holder);
        store.RequirePowered();
        RequireDispenseKept(store, first);
        Delivery delivery = Delivery.Survey(holder, new SlotChoice.Auto(), template);
        SiloPlacement placement = SiloPlacement.Plan(pick.Pooled,
            template is Item item ? Delivery.FullStack(item) : 1.0, delivery.Rooms, delivery.Empty.Count, pick.Whole,
            allowGround);
        if (!placement.Fits)
        {
            throw ApiErrors.Refused("no_room",
                $"{Names.Of(holder)} has no room for all {quantity} {Names.Of(template)} " +
                $"({placement.Pooled.Unplaced + placement.Whole.Unplaced} left over; a thing stored with contents or " +
                "a stored food takes an empty slot of its own). Free a slot, name another holder, or pass " +
                "allow: [\"ground\"] for the rest. Nothing was changed.");
        }

        double before = SiloPicks.Total(entries, prefab);
        int countBefore = store.Entries.Count;
        SiloWithdrawal withdrawal = new SiloWithdrawal(store, entries, pick, delivery, placement, holder);
        List<PlacedView> placed = act ? withdrawal.Run(template) : withdrawal.Preview();
        SiloStore after = act ? SiloStore.Of(store.Silo) : store;
        double stockAfter = act ? SiloPicks.Total(after.Describe(), prefab) : before - quantity;
        int countAfter = act ? after.Entries.Count : countBefore - SiloPicks.Leaving(pick);
        return new SiloWithdrawView(store.View(), GameLookup.ViewOf(holder), quantity, withdrawal.Taken(), placed,
            new SiloStockView(first.PrefabName, first.DisplayName, before, -quantity, stockAfter),
            new SiloCountView(countBefore, countAfter));
    }

    // DispenseSlot is an index into the store: taking that entry, or one before it, would make the IC's export pick
    // another entry. The silo clears it on its next export (BeginNextExport) or when it points past the end.
    private static void RequireDispenseKept(SiloStore store, SiloEntry first)
    {
        int dispense = store.DispenseSlot;
        if (SiloRules.ShiftsDispenseSlot(dispense, first.Index))
        {
            throw ApiErrors.Refused("silo_busy",
                $"An IC asked {Names.Of(store.Silo)} to dispense entry {dispense} (DispenseSlot), and this would take " +
                $"entry {first.Index}, at or before it, so the dispense would get another entry. Let the silo export " +
                "it first (it does when on, powered and its export is free). Nothing was changed.");
        }
    }
}
