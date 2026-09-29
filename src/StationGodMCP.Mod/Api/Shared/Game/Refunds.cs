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
/// each item (RefundPlacement): first onto matching stacks (Stackable.AddQuantity, as OnServer.CreateOrStack tops up
/// a stack): the source itself when it is such a stack, then any held in the inventory tree; then as new stacks made
/// straight into empty slots that accept the item (OnServer.Create into a slot, as a fabricator's output is made;
/// Slot.AllowMove decides, on a slot the game's quick moves would use: SlotAccess.AutoTakesNew), then, for what is
/// left, on the ground a metre in front of the outermost holder, at rest. The tree, level by level (a holder's own
/// slots before the slots of the items in them): the source's own slots and everything in them, then, when the source
/// is carried by a player (a worn belt), the rest of that player's, or, when it is stored (a coil stack in a locker),
/// the rest of its outermost holder's. A player's own body slots (hands, suit, helmet, back...) never get a new item,
/// only the slots of worn and held items; a stack already in a hand is topped up. Hidden slots (not interactable) and
/// a stack's own slot (a cable coil's), and whatever is in them, are left out: the game destroys their contents with
/// the holder.
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
        if (source is Stackable own)
        {
            OfferStack(own, prefab, stacks, rooms);
        }

        foreach (Slot slot in InventorySlots(source, body ?? StoredIn(source)))
        {
            DynamicThing? occupant = slot.Get();
            if (occupant == null)
            {
                if (slot.Parent != body && SlotAccess.AutoTakesNew(prefab, slot))
                {
                    empty.Add(slot);
                }
            }
            else if (!slot.IsLocked && occupant is Stackable stack && stack != source)
            {
                OfferStack(stack, prefab, stacks, rooms);
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

    // A matching stack with room, as a place to top up.
    private static void OfferStack(Stackable stack, Item prefab, List<Stackable> stacks, List<int> rooms)
    {
        if (!stack.IsBeingDestroyed && prefab is Stackable kind && stack.CanStack(kind) &&
            stack.MaxQuantity > stack.Quantity)
        {
            stacks.Add(stack);
            rooms.Add(stack.MaxQuantity - stack.Quantity);
        }
    }

    // The outermost holder of a thing stored in a slot (the locker a coil stack is in), or null.
    private static Thing? StoredIn(Thing source) =>
        source is DynamicThing item && item.ParentSlot != null && item.RootParent != null && item.RootParent != source
            ? item.RootParent
            : null;

    // The source's slots and every slot below them, then the rest of the holder carrying or storing it, each thing
    // once and level by level, so a holder's own empty slots come before those of the items in them; hidden slots,
    // stacks' own slots and what is in them are left out (SlotAccess.Reaches).
    internal static List<Slot> InventorySlots(Thing source, Thing? holder)
    {
        List<Slot> slots = new List<Slot>();
        HashSet<long> visited = new HashSet<long>();
        Collect(source, slots, visited);
        if (holder != null)
        {
            Collect(holder, slots, visited);
        }

        return slots;
    }

    private static void Collect(Thing root, List<Slot> slots, HashSet<long> visited)
    {
        Queue<(Thing Thing, int Depth)> pending = new Queue<(Thing Thing, int Depth)>();
        pending.Enqueue((root, 0));
        while (pending.Count > 0)
        {
            (Thing thing, int depth) = pending.Dequeue();
            if (depth > MaximumDepth || !visited.Add(thing.ReferenceId) || thing.Slots == null)
            {
                continue;
            }

            foreach (Slot slot in thing.Slots)
            {
                if (slot == null || !SlotAccess.Reaches(slot))
                {
                    continue;
                }

                slots.Add(slot);
                DynamicThing? occupant = slot.Get();
                if (occupant != null && !occupant.IsBeingDestroyed)
                {
                    pending.Enqueue((occupant, depth + 1));
                }
            }
        }
    }

    // A metre in front of the player at its centre of mass, where the game spawns a player's items
    // (OnServer.SpawnDynamicThingMaxStack); else a metre in front of the source, or of its outermost holder when it is
    // stored in a slot (a thing in a slot keeps no position of its own and reads the world origin).
    internal static Vector3 GroundBeside(Thing source, Human? body)
    {
        if (body != null && body.RigidBody != null)
        {
            return body.RigidBody.worldCenterOfMass + body.EntityForward * GroundDistance;
        }

        Thing anchor = StoredIn(source) ?? source;
        return anchor.Position + anchor.ThingTransform.forward * GroundDistance;
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
