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

    /// <summary>The checked spawn; every refusal is thrown here, before anything is made.</summary>
    internal static ItemSpawnPlan Check(ItemSpawnOrder order, string label)
    {
        HostOnly.Require();

        Item prefab = ItemPrefab(order.Prefab);
        double full = Delivery.FullStack(prefab);
        if (ItemSpawnRule.QuantityRefusal(order.Quantity, full, !(prefab is Consumable), prefab.PrefabName) is { } bad)
        {
            throw ApiErrors.InvalidArgument($"{label}: {bad}");
        }

        if (order.At is { } at)
        {
            return new ItemSpawnPlan(prefab, order.Quantity, null, null,
                ItemSpawnRule.AtPoint(order.Quantity, full), new Vector3((float)at.X, (float)at.Y, (float)at.Z));
        }

        Thing holder = string.Equals(order.To, PlayerWord, StringComparison.OrdinalIgnoreCase)
            ? PlayerOrigin.RequireHuman()
            : TargetIds.Thing(order.To!, label);
        Delivery delivery = Delivery.Survey(holder,
            order.Slot is { } index ? new SlotChoice.Exact(index) : new SlotChoice.Auto(), prefab);
        StackPlan plan = StackPlacement.Plan(order.Quantity, full, delivery.Rooms, delivery.Empty.Count, false);
        if (!plan.Fits)
        {
            throw ApiErrors.Refused("no_room",
                $"{Names.Of(holder)} takes {VaultAmount.Of(plan.Placed)} of {order.Quantity} {Names.Of(prefab)}; free a " +
                "slot, name another holder, or give at. Nothing was made.");
        }

        return new ItemSpawnPlan(prefab, order.Quantity, holder, delivery, plan, null);
    }

    /// <summary>The items made: into the holder, or loose at the point.</summary>
    internal static ItemSpawnView Make(ItemSpawnPlan plan)
    {
        List<PlacedView> items;
        if (plan.Delivery != null)
        {
            items = plan.Delivery.Deliver(plan.Stacks, plan.Prefab, NothingTaken.Instance, plan.Quantity);
        }
        else
        {
            items = new List<PlacedView>(plan.Stacks.Steps.Count);
            foreach (PlacementStep step in plan.Stacks.Steps)
            {
                Item made = OnServer.Create<Item>(plan.Prefab, plan.Point!.Value, Quaternion.identity);
                Delivery.SetAmount(made, step.Quantity);
                items.Add(new PlacedView(PlacedView.Ground, null, step.Quantity, new ThingId(made.ReferenceId)));
            }
        }

        return View(plan, items);
    }

    /// <summary>What the call would make, nothing made.</summary>
    internal static ItemSpawnView Preview(ItemSpawnPlan plan)
    {
        List<PlacedView> items;
        if (plan.Delivery != null)
        {
            items = plan.Delivery.Preview(plan.Stacks);
        }
        else
        {
            items = new List<PlacedView>(plan.Stacks.Steps.Count);
            foreach (PlacementStep step in plan.Stacks.Steps)
            {
                items.Add(new PlacedView(PlacedView.Ground, null, step.Quantity, null));
            }
        }

        return View(plan, items);
    }

    private static ItemSpawnView View(ItemSpawnPlan plan, List<PlacedView> items) =>
        new ItemSpawnView(ItemSpawnView.PlacedOf(items), plan.Prefab.PrefabName, Names.Of(plan.Prefab), plan.Quantity,
            plan.Holder != null ? GameLookup.ViewOf(plan.Holder) : null,
            plan.Point is { } point ? new PositionView(point.x, point.y, point.z) : null, items);

    // A loaded item prefab by its whole name (any case) or hash; a structure or anything else is refused.
    private static Item ItemPrefab(PrefabPattern pattern)
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

        if (!(found is Item item) || item == null)
        {
            throw ApiErrors.InvalidArgument(
                $"{found.PrefabName} is a {found.GetType().Name}, not an item; build structure places structures.");
        }

        return item;
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

/// <summary>A checked build item: the prefab, the quantity, and where each stack goes (a holder's slots, or a point).</summary>
internal sealed class ItemSpawnPlan
{
    internal ItemSpawnPlan(Item prefab, double quantity, Thing? holder, Delivery? delivery, StackPlan stacks,
        Vector3? point)
    {
        Prefab = prefab;
        Quantity = quantity;
        Holder = holder;
        Delivery = delivery;
        Stacks = stacks;
        Point = point;
    }

    internal Item Prefab { get; }

    internal double Quantity { get; }

    internal Thing? Holder { get; }

    internal Delivery? Delivery { get; }

    internal StackPlan Stacks { get; }

    internal Vector3? Point { get; }
}
