#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using Objects.Items;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// build item: items made out of nothing with OnServer.Create, as a fabricator's output and a vault's vend are made
/// (Delivery): into a holder's slots (matching stacks topped up, then new stacks in empty slots, or the one slot named),
/// or loose at a point, each stack at most the item's full stack. A cheat (CheatSwitch admits it), host only. A
/// holder that cannot take it all is refused before anything is made.
/// </summary>
internal static class ItemSpawns
{
    private const string PlayerWord = "player";

    /// <summary>
    /// The checked spawn; every refusal is thrown here, before anything is made. An items list is planned as one: each
    /// line takes the slots the lines before it left, and a line that does not fit refuses the whole call.
    /// </summary>
    internal static ItemSpawnPlan Check(ItemSpawnOrder order, string label)
    {
        HostOnly.Require();

        List<Item> prefabs = new List<Item>(order.Lines.Count);
        List<double> fulls = new List<double>(order.Lines.Count);
        for (int line = 0; line < order.Lines.Count; line++)
        {
            ItemLine asked = order.Lines[line];
            Item prefab = ItemPrefab(asked.Prefab, Where(order, line));
            double full = Delivery.FullStack(prefab);
            if (ItemSpawnRule.QuantityRefusal(asked.Quantity, full, !(prefab is Consumable), prefab.PrefabName) is { } bad)
            {
                throw ApiErrors.InvalidArgument($"{label}: {Where(order, line)}{bad}");
            }

            prefabs.Add(prefab);
            fulls.Add(full);
        }

        if (order.At is { } at)
        {
            ItemLine only = order.Lines[0];
            return new ItemSpawnPlan(order.FromList, null,
                new[]
                {
                    new SpawnLine(prefabs[0], only.Quantity, null, ItemSpawnRule.AtPoint(only.Quantity, fulls[0]))
                },
                new Vector3((float)at.X, (float)at.Y, (float)at.Z));
        }

        Thing holder = string.Equals(order.To, PlayerWord, StringComparison.OrdinalIgnoreCase)
            ? PlayerOrigin.RequireHuman()
            : TargetIds.Thing(order.To!, label);
        List<Delivery> deliveries = new List<Delivery>(order.Lines.Count);
        Dictionary<Slot, int> slotIds = new Dictionary<Slot, int>();
        List<LineSurvey> surveys = new List<LineSurvey>(order.Lines.Count);
        for (int line = 0; line < order.Lines.Count; line++)
        {
            Delivery delivery;
            try
            {
                delivery = Delivery.Survey(holder, SlotChoice.Of(order.Lines[line].Slot), prefabs[line]);
            }
            catch (ApiException refused) when (order.FromList)
            {
                throw ApiErrors.Refused(refused.Code, $"{Where(order, line)}{refused.Message} Nothing was made.",
                    refused.Details);
            }

            deliveries.Add(delivery);
            surveys.Add(new LineSurvey(IdsOf(delivery.StackSlots, slotIds), delivery.Rooms,
                IdsOf(delivery.Empty, slotIds), order.Lines[line].Quantity, fulls[line]));
        }

        switch (ItemListPlanner.Plan(surveys))
        {
            case ItemListPlan.Fits fits:
                List<SpawnLine> lines = new List<SpawnLine>(order.Lines.Count);
                for (int line = 0; line < order.Lines.Count; line++)
                {
                    lines.Add(new SpawnLine(prefabs[line], order.Lines[line].Quantity, deliveries[line],
                        fits.Lines[line]));
                }

                return new ItemSpawnPlan(order.FromList, holder, lines, null);
            case ItemListPlan.NoRoom noRoom:
                ItemLine refused = order.Lines[noRoom.Line];
                double placed = refused.Quantity - noRoom.Unplaced;
                throw ApiErrors.Refused("no_room",
                    $"{Where(order, noRoom.Line)}{Names.Of(holder)} takes {VaultAmount.Of(placed)} of " +
                    $"{refused.Quantity} {Names.Of(prefabs[noRoom.Line])}" +
                    (noRoom.Line > 0 ? " after the lines before it" : "") +
                    "; free a slot, name another holder, or give at. Nothing was made.");
            default:
                throw ApiErrors.InvalidArgument("Unknown item list plan.");
        }
    }

    // "items[2]: " for a line of a list; nothing for the one-prefab form.
    private static string Where(ItemSpawnOrder order, int line) => order.FromList ? $"items[{line}]: " : "";

    private static List<int> IdsOf(List<Slot> slots, Dictionary<Slot, int> ids)
    {
        List<int> numbers = new List<int>(slots.Count);
        foreach (Slot slot in slots)
        {
            if (!ids.TryGetValue(slot, out int id))
            {
                id = ids.Count;
                ids[slot] = id;
            }

            numbers.Add(id);
        }

        return numbers;
    }

    /// <summary>The items made: into the holder, or loose at the point, line by line.</summary>
    internal static ItemSpawnView Make(ItemSpawnPlan plan)
    {
        List<SpawnedView> items = new List<SpawnedView>();
        for (int line = 0; line < plan.Lines.Count; line++)
        {
            SpawnLine made = plan.Lines[line];
            List<PlacedView> rows;
            if (made.Delivery != null)
            {
                rows = made.Delivery.Deliver(made.Stacks, made.Prefab, NothingTaken.Instance, made.Quantity);
            }
            else
            {
                rows = new List<PlacedView>(made.Stacks.Steps.Count);
                foreach (PlacementStep step in made.Stacks.Steps)
                {
                    Item item = OnServer.Create<Item>(made.Prefab, plan.Point!.Value, Quaternion.identity);
                    Delivery.SetAmount(item, step.Quantity);
                    rows.Add(new PlacedView(PlacedView.Ground, null, step.Quantity, new ThingId(item.ReferenceId)));
                }
            }

            Tag(items, line, made.Prefab, rows);
        }

        return View(plan, items);
    }

    /// <summary>What the call would make, nothing made.</summary>
    internal static ItemSpawnView Preview(ItemSpawnPlan plan)
    {
        List<SpawnedView> items = new List<SpawnedView>();
        for (int line = 0; line < plan.Lines.Count; line++)
        {
            SpawnLine would = plan.Lines[line];
            List<PlacedView> rows;
            if (would.Delivery != null)
            {
                rows = would.Delivery.Preview(would.Stacks);
            }
            else
            {
                rows = new List<PlacedView>(would.Stacks.Steps.Count);
                foreach (PlacementStep step in would.Stacks.Steps)
                {
                    rows.Add(new PlacedView(PlacedView.Ground, null, step.Quantity, null));
                }
            }

            Tag(items, line, would.Prefab, rows);
        }

        return View(plan, items);
    }

    private static void Tag(List<SpawnedView> items, int line, Item prefab, List<PlacedView> rows)
    {
        foreach (PlacedView row in rows)
        {
            items.Add(new SpawnedView(line, prefab.PrefabName, row));
        }
    }

    private static ItemSpawnView View(ItemSpawnPlan plan, List<SpawnedView> items)
    {
        SpawnLine first = plan.Lines[0];
        bool one = !plan.FromList;
        return new ItemSpawnView(ItemSpawnView.PlacedOf(items), one ? first.Prefab.PrefabName : null,
            one ? Names.Of(first.Prefab) : null, one ? first.Quantity : (double?)null,
            plan.Holder != null ? GameLookup.ViewOf(plan.Holder) : null,
            plan.Point is { } point ? new PositionView(point.x, point.y, point.z) : null, items);
    }

    // A loaded item prefab by its whole name (any case) or hash; a structure or anything else is refused.
    private static Item ItemPrefab(PrefabPattern pattern, string where)
    {
        Thing found = ThingPrefab(pattern);
        if (!(found is Item item) || item == null)
        {
            throw ApiErrors.InvalidArgument(
                $"{where}{found.PrefabName} is a {found.GetType().Name}, not an item; build structure places structures.");
        }

        return item;
    }

    /// <summary>A loaded thing prefab by its whole name (any case) or hash; prefab_missing when none is loaded.</summary>
    internal static Thing ThingPrefab(PrefabPattern pattern)
    {
        Thing? found = pattern switch
        {
            PrefabPattern.Hash hash => Prefab.Find(hash.Value),
            PrefabPattern.Whole whole => Prefab.Find(whole.Name) is { } exact && exact != null
                ? exact
                : WorldModel.Prefabs.NamedIgnoringCase(whole.Name),
            _ => null
        };
        if (found == null)
        {
            throw ApiErrors.Refused("prefab_missing",
                $"No loaded prefab is {Shown(pattern)}; item names are as find item and inspect contents report them " +
                "(e.g. ItemIntegratedCircuit10).");
        }

        return found;
    }

    private static string Shown(PrefabPattern pattern) => pattern switch
    {
        PrefabPattern.Hash hash => hash.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        PrefabPattern.Whole whole => whole.Name,
        _ => "that prefab"
    };

    /// <summary>A spawn takes from nothing: nothing to take down, nothing to give back.</summary>
    private sealed class NothingTaken : IWithdrawalSource
    {
        internal static readonly NothingTaken Instance = new NothingTaken();

        public void Take(double quantity)
        {
        }

        public void Return(double quantity)
        {
        }

        public void Settle()
        {
        }
    }
}

/// <summary>One line of a checked build item: the prefab, how many, and where its stacks go (a holder's slots, or a point).</summary>
internal sealed record SpawnLine(Item Prefab, double Quantity, Delivery? Delivery, StackPlan Stacks);

/// <summary>A checked build item: its lines in order, the holder they go into or the point they appear at.</summary>
internal sealed record ItemSpawnPlan(bool FromList, Thing? Holder, IReadOnlyList<SpawnLine> Lines, Vector3? Point);
