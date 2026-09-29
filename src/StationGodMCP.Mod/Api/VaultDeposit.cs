#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Objects.Pipes;
using Objects.Items;
using Reagents;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api;

/// <summary>
/// vault_deposit: put ingots, ores and ices straight into an Ingot Vault's store, from wherever they are (a player at
/// any depth, a container, the ground), as a move: what leaves the source is what the store gains, nothing is made or
/// lost, and no slot, chute or door is involved. Writes; host only; needs IngotVault.
///
/// The vault's own import (CODE, IngotVault 1.2.0): an item reaches the Import slot only when the slot's class is None
/// or the item's SlotType (DeviceImport.TryChuteImport), is kept only when it is an Ingot or an Ore (Ice is an Ore;
/// OnChildEnterInventory), and otherwise is ejected. CollectResource then adds an ingot's CreatedReagentMixture times
/// its Quantity to the vault's ReagentMixture, or an ore's stack Quantity to _storedOresAndIces[prefab hash] (flagging
/// NetworkUpdateFlags 0x100), destroys the item (OnServer.Destroy) and refreshes the display. This does the same
/// bookkeeping with the same calls, and for part of a stack takes the part off the source the way the trade window
/// trims a stack (RemoveQuantity; Consumable.Quantity for an ingot's grams) instead of destroying it.
/// </summary>
internal static class VaultDepositApi
{
    internal const int MaximumItems = 256;
    internal const int MaximumLimit = 1000;

    internal static VaultDepositView Handle(Args args)
    {
        if (NetworkManager.IsClient || !GameManager.RunSimulation)
        {
            throw ApiErrors.Refused("not_host", "This game is a multiplayer client; only the host moves items.");
        }

        IngotVaults.RequireLoaded();
        bool dryRun = WriteMode.IsDryRun(args);
        VaultTarget target = VaultTarget.Resolve(args.ThingId("vault_id"));
        DepositRequest request = DepositRequest.Parse(args);
        target.RequirePowered();
        VaultStore store = target.Store();
        VaultLedger ledger = new VaultLedger(store);
        BatchBuilder batch = new BatchBuilder(request.Entries.Count);
        HashSet<long> seen = new HashSet<long>();
        List<DepositPlan> plans = new List<DepositPlan>(request.Entries.Count);
        for (int index = 0; index < request.Entries.Count; index++)
        {
            DepositEntry entry = request.Entries[index];
            if (!DepositPlan.TryPlan(entry, target, seen, out DepositPlan? plan, out ApiException? refusal))
            {
                batch.Failed(new NotDepositedView(index, entry.Id, entry.PrefabName, refusal!));
                continue;
            }

            plan!.Record(ledger);
            plans.Add(plan);
            batch.Succeeded(plan.View(index));
        }

        if (!dryRun && plans.Count > 0)
        {
            foreach (DepositPlan plan in plans)
            {
                plan.Apply(store);
            }

            store.Refresh();
        }

        return new VaultDepositView(dryRun, target.View(), request.Matched, request.Skipped, request.Truncated,
            batch.Build(), ledger.Views(readBack: !dryRun));
    }
}

/// <summary>Dry run by default; a real run needs dry_run false and confirm true, as the other write tools.</summary>
internal static class WriteMode
{
    internal static bool IsDryRun(Args args)
    {
        bool dryRun = args.OptionalBool("dry_run") ?? true;
        bool confirm = args.OptionalBool("confirm") ?? false;
        if (dryRun && confirm)
        {
            throw ApiErrors.InvalidArgument("confirm: true needs dry_run: false; nothing was changed.");
        }

        if (!dryRun && !confirm)
        {
            throw ApiErrors.Refused("confirm_required",
                "A real run needs dry_run: false and confirm: true; nothing was changed.");
        }

        return dryRun;
    }
}

/// <summary>One item to deposit: its id, and how much of it (null for all).</summary>
internal sealed class DepositEntry
{
    internal DepositEntry(ThingId id, double? quantity, string? prefabName = null)
    {
        Id = id;
        Quantity = quantity;
        PrefabName = prefabName;
    }

    internal ThingId Id { get; }

    internal double? Quantity { get; }

    /// <summary>Known up front in the filter form, for a refusal's reply.</summary>
    internal string? PrefabName { get; }
}

/// <summary>The three forms: items [{reference_id, quantity}], reference_ids [...], or a filter over every item.</summary>
internal sealed class DepositRequest
{
    private const int DefaultLimit = 256;

    private static readonly string[] FilterArguments =
        { "prefab_contains", "name_contains", "location", "within_id", "near_player_m", "kind", "limit" };

    private DepositRequest(List<DepositEntry> entries, int? matched, int skipped, bool truncated)
    {
        Entries = entries;
        Matched = matched;
        Skipped = skipped;
        Truncated = truncated;
    }

    internal List<DepositEntry> Entries { get; }

    internal int? Matched { get; }

    internal int Skipped { get; }

    internal bool Truncated { get; }

    internal static DepositRequest Parse(Args args)
    {
        if (args.Has("items"))
        {
            args.Reject("items", "reference_ids");
            args.Reject("items", FilterArguments);
            return new DepositRequest(Items(args), null, 0, false);
        }

        if (args.Has("reference_ids"))
        {
            args.Reject("reference_ids", FilterArguments);
            List<DepositEntry> entries = new List<DepositEntry>();
            foreach (ThingId id in args.ThingIds("reference_ids", VaultDepositApi.MaximumItems))
            {
                entries.Add(new DepositEntry(id, null));
            }

            return new DepositRequest(entries, null, 0, false);
        }

        return Filtered(args);
    }

    private static List<DepositEntry> Items(Args args)
    {
        List<Args?> items = args.Objects("items", VaultDepositApi.MaximumItems);
        List<DepositEntry> entries = new List<DepositEntry>(items.Count);
        for (int index = 0; index < items.Count; index++)
        {
            Args item = items[index] ?? throw ApiErrors.InvalidArgument($"items[{index}] must be an object.");
            entries.Add(new DepositEntry(item.ThingId("reference_id"), item.OptionalPositiveDouble("quantity")));
        }

        return entries;
    }

    // Every item the filter keeps that the vault takes, nearest the player first, up to limit.
    private static DepositRequest Filtered(Args args)
    {
        bool narrowed = false;
        foreach (string name in FilterArguments)
        {
            narrowed |= name != "limit" && args.Has(name);
        }

        if (!narrowed)
        {
            throw ApiErrors.InvalidArgument(
                "Name the items: items, reference_ids, or a filter (prefab_contains, name_contains, location, " +
                "within_id, near_player_m, kind).");
        }

        if (args.IsWord("location", "machine_stock"))
        {
            throw ApiErrors.InvalidArgument("Machine stock is not an item; location must be any, ground, player or stored.");
        }

        VaultMaterial kind = VaultMaterial.Parse(args.OptionalString("kind"));
        int limit = args.OptionalInt("limit", 1, VaultDepositApi.MaximumLimit) ?? DefaultLimit;
        List<ItemRecord> records = WorldItems.Collect(ItemFilter.Parse(args), PlayerOrigin.Current());
        List<ItemRecord> taken = new List<ItemRecord>(records.Count);
        int skipped = 0;
        foreach (ItemRecord record in records)
        {
            if (!VaultRule.Takes(record.Item))
            {
                skipped++;
            }
            else if (kind.Keeps(record.Item))
            {
                taken.Add(record);
            }
        }

        taken.Sort(ItemRecord.NearestFirst);
        List<DepositEntry> entries = new List<DepositEntry>(Math.Min(limit, taken.Count));
        for (int index = 0; index < taken.Count && index < limit; index++)
        {
            entries.Add(new DepositEntry(new ThingId(taken[index].Item.ReferenceId), null,
                taken[index].Item.PrefabName));
        }

        return new DepositRequest(entries, records.Count, skipped, taken.Count > limit);
    }
}

/// <summary>The filter form's kind: ingot, ore (not ice), ice, or any the vault takes.</summary>
internal sealed class VaultMaterial
{
    private readonly Func<Item, bool> _keeps;

    private VaultMaterial(Func<Item, bool> keeps)
    {
        _keeps = keeps;
    }

    internal bool Keeps(Item item) => _keeps(item);

    internal static VaultMaterial Parse(string? kind) =>
        (kind ?? "any").ToLowerInvariant() switch
        {
            "any" => new VaultMaterial(static _ => true),
            "ingot" => new VaultMaterial(static item => item is Ingot),
            "ore" => new VaultMaterial(static item => item is Ore && !(item is Ice)),
            "ice" => new VaultMaterial(static item => item is Ice),
            _ => throw ApiErrors.InvalidArgument("Argument 'kind' must be any, ingot, ore or ice.")
        };
}

/// <summary>What an Ingot Vault takes (its import rule, see VaultDepositApi).</summary>
internal static class VaultRule
{
    /// <summary>An Ingot or an Ore (ices, slag, organics and grindable ores are Ores).</summary>
    internal static bool Takes(DynamicThing item) => item is Ingot || item is Ore;

    /// <summary>Also the Import slot's class check, which only the vault being deposited into can answer.</summary>
    internal static ApiException? Refusal(DynamicThing item, DeviceImportExport vault)
    {
        Slot import = vault.ImportSlot;
        if (!Takes(item) || (import != null && import.Type != Slot.Class.None && item.SlotType != import.Type))
        {
            return ApiErrors.Refused("not_vault_material",
                $"{item.DisplayName} is not something the vault imports: it keeps ingots, ores and ices only.");
        }

        if (item is Ingot ingot && !(ingot.CreatedReagentMixture != null &&
                                     ingot.CreatedReagentMixture.TotalReagents > VaultStore.Trace))
        {
            return ApiErrors.Refused("no_reagents",
                $"{item.DisplayName} carries no reagents; the vault would destroy it and store nothing.");
        }

        return null;
    }
}

/// <summary>One item checked against the vault's rule, with the amount it moves.</summary>
internal sealed class DepositPlan
{
    private DepositPlan(DynamicThing item, VaultStock? ore, double quantity, double held)
    {
        Item = item;
        Ore = ore;
        Quantity = quantity;
        Held = held;
        From = item.ParentSlot != null
            ? new SlotRefView(new ThingId(item.ParentSlot.Parent.ReferenceId), item.ParentSlot.SlotIndex)
            : null;
    }

    internal DynamicThing Item { get; }

    /// <summary>The ore line it adds to; null for an ingot, which adds its reagents.</summary>
    private VaultStock? Ore { get; }

    internal double Quantity { get; }

    private double Held { get; }

    private SlotRefView? From { get; }

    private bool IsWhole => Quantity >= Held - VaultStore.Trace;

    internal static bool TryPlan(DepositEntry entry, VaultTarget target, HashSet<long> seen, out DepositPlan? plan,
        out ApiException? refusal)
    {
        plan = null;
        refusal = ItemRefusal(entry, target, seen, out DynamicThing? item);
        if (refusal != null)
        {
            return false;
        }

        double held = HeldIn(item!);
        double quantity = entry.Quantity ?? held;
        refusal = QuantityRefusal(item!, quantity, held);
        if (refusal != null)
        {
            return false;
        }

        plan = new DepositPlan(item!, item is Ingot ? null : new VaultStock.OreStock(item!.PrefabHash), quantity,
            held);
        return true;
    }

    private static ApiException? ItemRefusal(DepositEntry entry, VaultTarget target, HashSet<long> seen,
        out DynamicThing? item)
    {
        item = null;
        if (!GameLookup.TryFindThing(entry.Id, out Thing found) || found.IsBeingDestroyed)
        {
            return ApiErrors.ThingNotFound(entry.Id);
        }

        if (!seen.Add(found.ReferenceId))
        {
            return ApiErrors.InvalidArgument($"{found.DisplayName} ({entry.Id}) is named twice.");
        }

        item = found as DynamicThing;
        if (item == null)
        {
            return ApiErrors.Refused("not_vault_material", $"{found.DisplayName} is not an item.");
        }

        Slot? slot = item.ParentSlot;
        if (slot != null && slot.Parent != null && (IngotVaults.IsVault(slot.Parent) || IngotVaults.IsRemote(slot.Parent)))
        {
            // The vault may be importing or exporting it (its _importingItem); let the vault finish with it.
            return ApiErrors.Refused("in_vault_slot",
                $"{item.DisplayName} is in a slot of {slot.Parent.DisplayName}; take it out or let the vault finish.");
        }

        if (slot != null && slot.IsLocked)
        {
            return ApiErrors.Refused("slot_locked", $"{item.DisplayName} is in a locked slot.");
        }

        return VaultRule.Refusal(item, target.Vault);
    }

    // A stack's count, an ingot's grams (Consumable.Quantity).
    private static double HeldIn(DynamicThing item) =>
        item switch
        {
            Stackable stack => stack.Quantity,
            Consumable consumable => consumable.Quantity,
            _ => 1.0
        };

    private static ApiException? QuantityRefusal(DynamicThing item, double quantity, double held)
    {
        if (quantity > held + VaultStore.Trace)
        {
            return ApiErrors.InvalidArgument($"{item.DisplayName} holds {held}; cannot take {quantity}.");
        }

        return item is Stackable && Math.Abs(quantity - Math.Round(quantity)) > VaultStore.Trace
            ? ApiErrors.InvalidArgument($"{item.DisplayName} counts whole items; quantity {quantity} is not whole.")
            : null;
    }

    internal void Record(VaultLedger ledger)
    {
        if (Ore != null)
        {
            ledger.Record(Ore, Quantity);
            return;
        }

        ReagentMixture created = ((Ingot)Item).CreatedReagentMixture;
        foreach (Reagent reagent in Reagent.AllReagents)
        {
            double amount = created.Get(reagent);
            if (amount > 0.0)
            {
                ledger.Record(new VaultStock.IngotStock(reagent), amount * Quantity);
            }
        }
    }

    internal DepositedView View(int index) =>
        new DepositedView(index, GameLookup.ViewOf(Item), Ore != null ? "ore" : "ingot", From, Quantity,
            IsWhole ? 0.0 : Held - Quantity);

    /// <summary>Adds to the store as CollectResource does, then takes the item (or the part) off its source.</summary>
    internal void Apply(VaultStore store)
    {
        if (Ore != null)
        {
            Ore.Add(store, Quantity);
        }
        else
        {
            store.Ingots.Add(((Ingot)Item).CreatedReagentMixture * Quantity);
        }

        if (IsWhole)
        {
            OnServer.Destroy(Item);
        }
        else if (Item is Stackable stack)
        {
            stack.RemoveQuantity((int)Math.Round(Quantity));
        }
        else
        {
            Consumable consumable = (Consumable)Item;
            consumable.Quantity -= (float)Quantity;
        }
    }
}
