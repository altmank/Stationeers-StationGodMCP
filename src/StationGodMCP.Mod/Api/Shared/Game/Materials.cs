#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>An item and how many of it.</summary>
internal sealed class ItemAmount
{
    internal ItemAmount(Item prefab, int quantity)
    {
        Prefab = prefab;
        Quantity = quantity;
    }

    internal Item Prefab { get; }

    internal int Quantity { get; }
}

/// <summary>
/// What a structure's build states take and give back, by the game's own rule (MaterialRule): each state's ToolEntry
/// and ToolEntry2 with their quantities, tools and zero quantities skipped (ToolUse.SpawnItem).
/// </summary>
internal static class BuildMaterials
{
    /// <summary>What deconstructing the structure from its current state gives back.</summary>
    internal static List<ItemAmount> RefundOf(Structure piece) =>
        Amounts(piece, piece.CurrentBuildStateIndex, new Dictionary<int, Item>());

    /// <summary>States 0 to lastState of the structure (a prefab or a live piece), per item.</summary>
    internal static List<ItemAmount> Amounts(Structure structure, int lastState, Dictionary<int, Item> items)
    {
        List<ItemCount> counts = MaterialRule.Totals(EntriesOf(structure, items), lastState);
        List<ItemAmount> amounts = new List<ItemAmount>(counts.Count);
        foreach (ItemCount count in counts)
        {
            amounts.Add(new ItemAmount(items[count.Item], count.Quantity));
        }

        return amounts;
    }

    /// <summary>
    /// Every build state's entries, keyed by PrefabHash; each item met is added to items so keys can be turned back
    /// into items.
    /// </summary>
    internal static List<IReadOnlyList<BuildEntry>> EntriesOf(Structure structure, Dictionary<int, Item> items)
    {
        List<IReadOnlyList<BuildEntry>> states = new List<IReadOnlyList<BuildEntry>>();
        if (structure.BuildStates == null)
        {
            return states;
        }

        foreach (BuildState state in structure.BuildStates)
        {
            List<BuildEntry> entries = new List<BuildEntry>(2);
            ToolUse? tool = state?.Tool;
            if (tool != null)
            {
                Add(entries, items, tool.ToolEntry, tool.EntryQuantity);
                Add(entries, items, tool.ToolEntry2, tool.EntryQuantity2);
            }

            states.Add(entries);
        }

        return states;
    }

    private static void Add(List<BuildEntry> entries, Dictionary<int, Item> items, Item? item, int quantity)
    {
        if (item == null)
        {
            return;
        }

        items[item.PrefabHash] = item;
        entries.Add(new BuildEntry(item.PrefabHash, quantity, item is Tool));
    }
}

/// <summary>
/// The stacks of one item the source holds: its own stacks at any depth of slots (hands, suit, backpack, belt, a
/// locker's slots), or the source itself when it is such a stack. Taken as a kit's own placement takes them
/// (Stackable.OnUseItem, which removes an emptied stack).
/// </summary>
internal sealed class ItemStock
{
    private const int MaximumDepth = 8;

    private ItemStock(Item item, List<Stackable> stacks)
    {
        Item = item;
        Stacks = stacks;
        int available = 0;
        foreach (Stackable stack in stacks)
        {
            available += stack.Quantity;
        }

        Available = available;
    }

    internal Item Item { get; }

    internal List<Stackable> Stacks { get; }

    internal int Available { get; }

    internal int Needed { get; set; }

    internal static ItemStock Empty(Item item) => new ItemStock(item, new List<Stackable>());

    internal static ItemStock In(Thing source, Item item)
    {
        List<Stackable> stacks = new List<Stackable>();
        if (source is Stackable own && own.PrefabHash == item.PrefabHash)
        {
            stacks.Add(own);
        }

        Collect(source, item.PrefabHash, stacks, 0);
        return new ItemStock(item, stacks);
    }

    private static void Collect(Thing holder, int prefabHash, List<Stackable> stacks, int depth)
    {
        if (holder.Slots == null || depth >= MaximumDepth)
        {
            return;
        }

        foreach (Slot slot in holder.Slots)
        {
            DynamicThing? occupant = slot?.Get();
            if (occupant == null || occupant.IsBeingDestroyed)
            {
                continue;
            }

            if (occupant is Stackable stack && stack.PrefabHash == prefabHash)
            {
                stacks.Add(stack);
            }

            Collect(occupant, prefabHash, stacks, depth + 1);
        }
    }

    /// <summary>Takes up to the quantity from the stacks in order; returns how many were taken.</summary>
    internal int Take(int quantity)
    {
        int taken = 0;
        foreach (Stackable stack in Stacks)
        {
            if (taken >= quantity)
            {
                break;
            }

            if (stack == null || stack.IsBeingDestroyed || stack.Quantity <= 0)
            {
                continue;
            }

            int part = Math.Min(quantity - taken, stack.Quantity);
            int before = stack.Quantity;
            stack.OnUseItem(part, null);
            taken += before - stack.Quantity;
        }

        return taken;
    }
}

/// <summary>
/// Gives back what deconstructing the old pieces would, as ToolUse.SpawnItem does: OnServer.CreateOrStack at the
/// source's position, then, when the source is a player, the first worn item that takes it (WearableItem.TryCollect:
/// belt, backpack, suit). What nothing takes stays on the ground there.
/// </summary>
internal static class Refunds
{
    internal static void Deliver(Thing source, List<ItemAmount> refund, List<UpgradeRefundView> delivered) =>
        DeliverAt(source.Position, source as Human, refund, delivered);

    /// <summary>
    /// Makes the refund at a position; a player given as collector takes what its worn items take, the rest stays on
    /// the ground there.
    /// </summary>
    internal static void DeliverAt(Vector3 position, Human? collector, List<ItemAmount> refund,
        List<UpgradeRefundView> delivered)
    {
        Dictionary<int, int> totals = new Dictionary<int, int>();
        Dictionary<int, Item> prefabs = new Dictionary<int, Item>();
        foreach (ItemAmount amount in refund)
        {
            totals[amount.Prefab.PrefabHash] = (totals.TryGetValue(amount.Prefab.PrefabHash, out int sum) ? sum : 0) +
                                               amount.Quantity;
            prefabs[amount.Prefab.PrefabHash] = amount.Prefab;
        }

        foreach (KeyValuePair<int, int> total in totals)
        {
            DeliverAll(position, collector, prefabs[total.Key], total.Value, delivered);
        }
    }

    private static void DeliverAll(Vector3 position, Human? collector, Item prefab, int quantity,
        List<UpgradeRefundView> delivered)
    {
        int stack = prefab is Stackable stackable && stackable.MaxQuantity > 0 ? stackable.MaxQuantity : 1;
        int left = quantity;
        while (left > 0)
        {
            int part = Math.Min(stack, left);
            Item item = OnServer.CreateOrStack(prefab, part, position, Quaternion.identity);
            if (item == null)
            {
                return;
            }

            bool collected = collector != null && Collect(collector, item);
            delivered.Add(new UpgradeRefundView(new ThingId(item.ReferenceId), prefab.PrefabName, part,
                collected ? "collected" : "ground"));
            left -= part;
        }
    }

    private static bool Collect(Human human, Item item)
    {
        foreach (Slot slot in human.Slots)
        {
            if (slot?.Get() is WearableItem wearable && wearable.TryCollect(item))
            {
                return true;
            }
        }

        return false;
    }
}
