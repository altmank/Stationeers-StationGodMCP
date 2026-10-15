#nullable enable

using System.Collections.Generic;
using System;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Objects.Pipes;
using Assets.Scripts.Objects;
using Assets.Scripts;
using Objects.Items;
using Reagents;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Shaping;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game;

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

/// <summary>The filter form's kind: ingot, ore (not ice), ice, or any the vault takes.</summary>
internal sealed class VaultMaterial
{
    private readonly Func<Item, bool> _keeps;

    private VaultMaterial(Func<Item, bool> keeps)
    {
        _keeps = keeps;
    }

    internal bool Keeps(Item item) => _keeps(item);

    /// <summary>any, ingot, ore or ice; null for any other word.</summary>
    internal static VaultMaterial? Of(string kind) =>
        kind.ToLowerInvariant() switch
        {
            "any" => new VaultMaterial(static _ => true),
            "ingot" => new VaultMaterial(static item => item is Ingot),
            "ore" => new VaultMaterial(static item => item is Ore && !(item is Ice)),
            "ice" => new VaultMaterial(static item => item is Ice),
            _ => null
        };
}

/// <summary>
/// What an Ingot Vault takes (CODE, IngotVault 1.2.0): an item reaches the Import slot only when the slot's class is
/// None or the item's SlotType (DeviceImport.TryChuteImport), and is kept only when it is an Ingot or an Ore (Ice is an
/// Ore; OnChildEnterInventory).
/// </summary>
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
                $"{Names.Of(item)} is not something the vault imports: it keeps ingots, ores and ices only.");
        }

        if (item is Ingot ingot && !(ingot.CreatedReagentMixture != null &&
                                     ingot.CreatedReagentMixture.TotalReagents > VaultStore.Trace))
        {
            return ApiErrors.Refused("no_reagents",
                $"{Names.Of(item)} carries no reagents; the vault would destroy it and store nothing.");
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
            return ApiErrors.InvalidArgument($"{Names.Of(found)} ({entry.Id}) is named twice.");
        }

        item = found as DynamicThing;
        if (item == null)
        {
            return ApiErrors.Refused("not_vault_material", $"{Names.Of(found)} is not an item.");
        }

        Slot? slot = item.ParentSlot;
        if (slot != null && slot.Parent != null && (IngotVaults.IsVault(slot.Parent) || IngotVaults.IsRemote(slot.Parent)))
        {
            // The vault may be importing or exporting it (its _importingItem); let the vault finish with it.
            return ApiErrors.Refused("in_vault_slot",
                $"{Names.Of(item)} is in a slot of {Names.Of(slot.Parent)}; take it out or let the vault finish.");
        }

        if (slot != null && slot.IsLocked)
        {
            return ApiErrors.Refused("slot_locked", $"{Names.Of(item)} is in a locked slot.");
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
            return ApiErrors.InvalidArgument($"{Names.Of(item)} holds {held}; cannot take {quantity}.");
        }

        return item is Stackable && Math.Abs(quantity - Math.Round(quantity)) > VaultStore.Trace
            ? ApiErrors.InvalidArgument($"{Names.Of(item)} counts whole items; quantity {quantity} is not whole.")
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

/// <summary>The stored line a withdrawal names by exactly one of a prefab's name, its hash, or a reagent (ingots only).</summary>
internal static class WithdrawStock
{
    internal static VaultStock Find(VaultStore store, string? prefabName, int? prefabHash, string? reagent)
    {
        List<VaultStock> lines = store.Lines();
        foreach (VaultStock line in lines)
        {
            if (Matches(line, prefabName, prefabHash, reagent))
            {
                return line;
            }
        }

        throw ApiErrors.Refused("not_in_vault",
            $"The vault holds no {prefabName ?? reagent ?? prefabHash.ToString()}; it holds {Names(lines)}.");
    }

    private static bool Matches(VaultStock line, string? prefabName, int? prefabHash, string? reagent)
    {
        if (prefabHash.HasValue)
        {
            return line.PrefabHash == prefabHash.Value;
        }

        if (prefabName != null)
        {
            Item? prefab = line.ItemPrefab;
            return prefab != null && string.Equals(prefab.PrefabName, prefabName, StringComparison.OrdinalIgnoreCase);
        }

        return line is VaultStock.IngotStock ingot &&
               (string.Equals(ingot.Reagent.TypeName, reagent, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ingot.Reagent.DisplayName, reagent, StringComparison.OrdinalIgnoreCase));
    }

    private static string Names(List<VaultStock> lines)
    {
        if (lines.Count == 0)
        {
            return "nothing";
        }

        List<string> names = new List<string>(lines.Count);
        foreach (VaultStock line in lines)
        {
            names.Add(line.View().PrefabName ?? line.Key);
        }

        return string.Join(", ", names);
    }
}

/// <summary>
/// The items a vault deposit takes: named by id, or by a filter (Nearest: every item the filter keeps that the vault
/// takes and kind keeps, nearest the player first, at most limit; Matched counts what the filter kept and Skipped what
/// the vault does not take).
/// </summary>
internal sealed class DepositPicks
{
    internal DepositPicks(List<DepositEntry> entries, int? matched, int skipped, int taken)
    {
        Entries = entries;
        Matched = matched;
        Skipped = skipped;
        Taken = taken;
    }

    public List<DepositEntry> Entries { get; }

    /// <summary>How many items the filter named before the vault's rule; null for things named by id.</summary>
    public int? Matched { get; }

    public int Skipped { get; }

    public int Taken { get; }


    /// <summary>Things named by id, in the order given.</summary>
    internal static DepositPicks ById(List<DepositEntry> entries) => new DepositPicks(entries, null, 0, entries.Count);

    internal static DepositPicks Nearest(ItemFilter filter, VaultMaterial kind, int limit)
    {
        List<ItemRecord> records = WorldItems.Collect(filter, PlayerOrigin.Current());
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

        return new DepositPicks(entries, records.Count, skipped, taken.Count);
    }
}
