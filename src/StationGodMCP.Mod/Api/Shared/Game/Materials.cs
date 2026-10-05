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

/// <summary>A source of a stock and how many of the item it adds.</summary>
internal readonly struct StockSource
{
    internal StockSource(Thing thing, int available)
    {
        Thing = thing;
        Available = available;
    }

    internal Thing Thing { get; }

    internal int Available { get; }
}

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
/// One item a source holds that pays toward a build entry: a stack (Stackable.OnUseItem takes a part and removes an
/// emptied stack) or a whole item such as a printer mod, which pays one and is destroyed, as the game destroys a
/// non-stackable entry item (Structure.HandleToolUse).
/// </summary>
internal abstract class HeldItem
{
    private protected HeldItem(Item item)
    {
        Item = item;
    }

    internal Item Item { get; }

    /// <summary>How it pays, read live (a stack's quantity drops as it is used).</summary>
    internal abstract HeldMaterial Material { get; }

    internal int Quantity => Material.Units;

    internal static HeldItem Of(Item item) =>
        item is Stackable stack ? new HeldStack(stack) : new HeldWhole(item);

    /// <summary>Takes the part (no more than Material.PartOf allows); returns how many were taken.</summary>
    internal abstract int Use(int part);

    private sealed class HeldStack : HeldItem
    {
        private readonly Stackable _stack;

        internal HeldStack(Stackable stack) : base(stack)
        {
            _stack = stack;
        }

        internal override HeldMaterial Material => HeldMaterial.Stack(_stack.Quantity);

        internal override int Use(int part)
        {
            int before = _stack.Quantity;
            _stack.OnUseItem(part, null);
            int after = _stack.Quantity;
            if (StockTake.TookTooMuch(part, before, after))
            {
                throw new InvalidOperationException(
                    $"{_stack.DisplayName} {_stack.ReferenceId} went from {before} to {after} when {part} were taken "
                    + "from it; nothing more was taken.");
            }

            return before - after;
        }
    }

    private sealed class HeldWhole(Item item) : HeldItem(item)
    {
        internal override HeldMaterial Material => HeldMaterial.Whole;

        internal override int Use(int part)
        {
            OnServer.Destroy(Item);
            return part;
        }
    }
}

/// <summary>
/// The held items of one prefab the sources hold, source by source in the order given: each source's own items at any
/// depth of slots (hands, suit, backpack, belt, a locker's slots), or the source itself when it is such an item; an
/// item counted once however many sources reach it. Stacks and whole items alike (HeldItem): a build entry is matched
/// by prefab hash, as ToolBasic.IsToolEntry does, whatever the item's class. Taken in that order as a kit's own
/// placement takes them.
/// </summary>
internal sealed class ItemStock
{
    private const int MaximumDepth = 8;

    private ItemStock(Item item, List<HeldItem> stacks, List<StockSource>? sources = null)
    {
        Item = item;
        Stacks = stacks;
        Sources = sources ?? new List<StockSource>();
        int available = 0;
        foreach (HeldItem stack in stacks)
        {
            available += stack.Quantity;
        }

        Available = available;
    }

    internal Item Item { get; }

    /// <summary>The held items in take order: stacks and whole items.</summary>
    internal List<HeldItem> Stacks { get; }

    internal int Available { get; }

    internal int Needed { get; set; }

    /// <summary>Each source in order with how many of the item it alone adds.</summary>
    internal List<StockSource> Sources { get; }

    internal static ItemStock Empty(Item item) => new ItemStock(item, new List<HeldItem>());

    internal static ItemStock In(Thing source, Item item) => In(new List<Thing> { source }, item);

    internal static ItemStock In(IReadOnlyList<Thing> sources, Item item)
    {
        List<HeldItem> stacks = new List<HeldItem>();
        List<StockSource> counted = new List<StockSource>(sources.Count);
        HashSet<long> seen = new HashSet<long>();
        foreach (Thing source in sources)
        {
            List<Item> own = new List<Item>();
            if (source is Item self && self.PrefabHash == item.PrefabHash)
            {
                own.Add(self);
            }

            Collect(source, item.PrefabHash, own, 0);
            int added = 0;
            foreach (Item held in own)
            {
                if (seen.Add(held.ReferenceId))
                {
                    HeldItem stack = HeldItem.Of(held);
                    stacks.Add(stack);
                    added += stack.Quantity;
                }
            }

            counted.Add(new StockSource(source, added));
        }

        return new ItemStock(item, stacks, counted);
    }

    private static void Collect(Thing holder, int prefabHash, List<Item> items, int depth)
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

            if (occupant is Item item && item.PrefabHash == prefabHash)
            {
                items.Add(item);
            }

            Collect(occupant, prefabHash, items, depth + 1);
        }
    }

    /// <summary>
    /// Takes up to the quantity from the held items in order (StockTake), splitting the last stack used; returns how
    /// many were taken. A stack that loses more than its part stops the build with an error naming it.
    /// </summary>
    internal int Take(int quantity)
    {
        int taken = 0;
        foreach (HeldItem stack in Stacks)
        {
            if (stack.Item == null || stack.Item.IsBeingDestroyed)
            {
                continue;
            }

            int part = stack.Material.PartOf(quantity - taken);
            if (part == 0)
            {
                continue;
            }

            taken += stack.Use(part);
        }

        return taken;
    }
}
