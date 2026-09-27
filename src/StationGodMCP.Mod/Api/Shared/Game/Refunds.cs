#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using Objects.Items;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// Gives back what deconstructing pieces would, into the source's inventory and never through the player's body. For
/// each item (RefundPlacement): first onto matching stacks already held anywhere in the inventory tree (Stackable
/// .AddQuantity, as OnServer.CreateOrStack tops up a stack), then as new stacks made straight into empty slots that
/// accept the item (OnServer.Create into a slot, as a fabricator's output is made; Slot.AllowMove decides), then, for
/// what is left, on the ground a metre in front of the holder, at rest. The tree: the source's own slots and everything
/// in them, then, when the source is carried by a player (a worn belt), the rest of that player's. A player's own body
/// slots (hands, suit, helmet, back...) never get a new item, only the slots of worn and held items; a stack already
/// in a hand is topped up.
/// </summary>
internal static class Refunds
{
    internal const string Merged = "merged";
    internal const string InSlot = "slot";
    internal const string Ground = "ground";

    private const int MaximumDepth = 6;
    private const float GroundDistance = 1.0f;

    internal static void Deliver(Thing source, List<ItemAmount> refund, List<UpgradeRefundView> delivered)
    {
        foreach (KeyValuePair<Item, int> total in Totals(refund))
        {
            DeliverInto(source, total.Key, total.Value, delivered);
        }
    }

    /// <summary>Makes the refund on the ground at a position (refund_to ground: where each piece stood), at rest.</summary>
    internal static void DeliverAt(Vector3 position, List<ItemAmount> refund, List<UpgradeRefundView> delivered)
    {
        foreach (KeyValuePair<Item, int> total in Totals(refund))
        {
            foreach (RefundStep step in RefundPlacement.Plan(total.Value, FullStack(total.Key), new List<int>(), 0))
            {
                Record(delivered, MakeAt(total.Key, step.Quantity, position), total.Key, step.Quantity, Ground);
            }
        }
    }

    private static List<KeyValuePair<Item, int>> Totals(List<ItemAmount> refund)
    {
        List<KeyValuePair<Item, int>> totals = new List<KeyValuePair<Item, int>>();
        foreach (ItemAmount amount in refund)
        {
            int at = totals.FindIndex(total => total.Key.PrefabHash == amount.Prefab.PrefabHash);
            totals.Add(new KeyValuePair<Item, int>(amount.Prefab,
                amount.Quantity + (at >= 0 ? totals[at].Value : 0)));
            if (at >= 0)
            {
                totals.RemoveAt(at);
            }
        }

        return totals;
    }

    private static int FullStack(Item prefab) =>
        prefab is Stackable stackable && stackable.MaxQuantity > 0 ? stackable.MaxQuantity : 1;

    private static void DeliverInto(Thing source, Item prefab, int quantity, List<UpgradeRefundView> delivered)
    {
        Human? body = source is Human human ? human : source.RootParentHuman;
        List<Stackable> stacks = new List<Stackable>();
        List<int> rooms = new List<int>();
        List<Slot> empty = new List<Slot>();
        foreach (Slot slot in InventorySlots(source, body))
        {
            DynamicThing? occupant = slot.Get();
            if (occupant == null)
            {
                if (slot.Parent != body && Slot.AllowMove(prefab, slot))
                {
                    empty.Add(slot);
                }
            }
            else if (!slot.IsLocked && !occupant.IsBeingDestroyed && occupant is Stackable stack &&
                     prefab is Stackable kind && stack.CanStack(kind) && stack.MaxQuantity > stack.Quantity)
            {
                stacks.Add(stack);
                rooms.Add(stack.MaxQuantity - stack.Quantity);
            }
        }

        foreach (RefundStep step in RefundPlacement.Plan(quantity, FullStack(prefab), rooms, empty.Count))
        {
            switch (step)
            {
                case RefundStep.Merge merge:
                    Stackable onto = stacks[merge.Stack];
                    int added = onto.AddQuantity(step.Quantity);
                    Record(delivered, onto, prefab, added, Merged);
                    if (added < step.Quantity)
                    {
                        // The stack filled between the survey and the merge; the rest goes down beside the holder.
                        Record(delivered, MakeAt(prefab, step.Quantity - added, GroundBeside(source, body)), prefab,
                            step.Quantity - added, Ground);
                    }

                    break;
                case RefundStep.IntoSlot slot:
                    Item made = OnServer.Create<Item>(prefab, empty[slot.Slot]);
                    SetQuantity(made, step.Quantity);
                    if (made.ParentSlot == null)
                    {
                        // The slot refused it after all and the game put it in the world at the origin: bring it
                        // down beside the holder, at rest.
                        OnServer.MoveToWorld(made, GroundBeside(source, body), Quaternion.identity);
                    }

                    Record(delivered, made, prefab, step.Quantity, made.ParentSlot != null ? InSlot : Ground);
                    break;
                default:
                    Record(delivered, MakeAt(prefab, step.Quantity, GroundBeside(source, body)), prefab,
                        step.Quantity, Ground);
                    break;
            }
        }
    }

    // The source's slots and every slot below them, then the rest of the player carrying it, each thing once.
    private static List<Slot> InventorySlots(Thing source, Human? carrier)
    {
        List<Slot> slots = new List<Slot>();
        HashSet<long> visited = new HashSet<long>();
        Collect(source, slots, visited, 0);
        if (carrier != null)
        {
            Collect(carrier, slots, visited, 0);
        }

        return slots;
    }

    private static void Collect(Thing thing, List<Slot> slots, HashSet<long> visited, int depth)
    {
        if (depth > MaximumDepth || !visited.Add(thing.ReferenceId) || thing.Slots == null)
        {
            return;
        }

        foreach (Slot slot in thing.Slots)
        {
            if (slot == null)
            {
                continue;
            }

            slots.Add(slot);
            DynamicThing? occupant = slot.Get();
            if (occupant != null && !occupant.IsBeingDestroyed)
            {
                Collect(occupant, slots, visited, depth + 1);
            }
        }
    }

    // A metre in front of the player at its centre of mass, where the game spawns a player's items
    // (OnServer.SpawnDynamicThingMaxStack); a metre in front of the source when no player carries it.
    private static Vector3 GroundBeside(Thing source, Human? body)
    {
        if (body != null && body.RigidBody != null)
        {
            return body.RigidBody.worldCenterOfMass + body.EntityForward * GroundDistance;
        }

        return source.Position + source.ThingTransform.forward * GroundDistance;
    }

    private static Item MakeAt(Item prefab, int quantity, Vector3 position)
    {
        Item item = OnServer.Create<Item>(prefab, position, Quaternion.identity);
        SetQuantity(item, quantity);
        return item;
    }

    // As OnServer.CreateOrStack sets a new item's amount.
    private static void SetQuantity(Item item, int quantity)
    {
        if (item is Stackable stack)
        {
            stack.SetQuantity(quantity);
        }

        if (item is Consumable consumable)
        {
            consumable.Quantity = quantity;
        }
    }

    private static void Record(List<UpgradeRefundView> delivered, Thing item, Item prefab, int quantity, string where)
    {
        if (quantity > 0)
        {
            delivered.Add(new UpgradeRefundView(new ThingId(item.ReferenceId), prefab.PrefabName, quantity, where));
        }
    }
}
