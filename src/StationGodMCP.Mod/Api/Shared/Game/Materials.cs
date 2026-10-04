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
    /// States 0 to lastState without state 0's kit: what a merge kit's replacement costs (MultiMergeConstructor.cs:86
    /// takes no kit when it replaces a standing fuselage piece), the later states as usual.
    /// </summary>
    internal static List<ItemAmount> AmountsAfterKit(Structure structure, int lastState, Dictionary<int, Item> items)
    {
        List<IReadOnlyList<BuildEntry>> states = EntriesOf(structure, items);
        if (states.Count > 0)
        {
            states[0] = new List<BuildEntry>();
        }

        List<ItemCount> counts = MaterialRule.Totals(states, lastState);
        return counts.ConvertAll(count => new ItemAmount(items[count.Item], count.Quantity));
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

    /// <summary>
    /// Takes up to the quantity from the stacks in order (StockTake), splitting the last one used; returns how many
    /// were taken. A stack that loses more than its part stops the build with an error naming it.
    /// </summary>
    internal int Take(int quantity)
    {
        int taken = 0;
        foreach (Stackable stack in Stacks)
        {
            if (stack == null || stack.IsBeingDestroyed)
            {
                continue;
            }

            int part = StockTake.PartOf(quantity - taken, stack.Quantity);
            if (part == 0)
            {
                continue;
            }

            int before = stack.Quantity;
            stack.OnUseItem(part, null);
            int after = stack.Quantity;
            if (StockTake.TookTooMuch(part, before, after))
            {
                throw new InvalidOperationException(
                    $"{stack.DisplayName} {stack.ReferenceId} went from {before} to {after} when {part} were taken "
                    + "from it; nothing more was taken.");
            }

            taken += before - after;
        }

        return taken;
    }
}
