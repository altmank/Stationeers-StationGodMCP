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
/// Who and what a refund can go to, resolved when the plan is made: the route (refund_to), the source (from_id, else
/// the player), the player whose inventory takes items (from_id when it is a player, else the player),
/// each container a chain names, and where the pieces stood (the ground when there is no holder at all).
/// </summary>
internal sealed class RefundReceivers
{
    private readonly Dictionary<long, Thing> _containers;
    private readonly Vector3? _pieceGround;

    private RefundReceivers(RefundRoute route, Thing? from, Human? player, List<RefundTarget> usable,
        Dictionary<long, Thing> containers, List<string> skipped, Vector3? pieceGround)
    {
        Route = route;
        From = from;
        Player = player;
        Usable = usable;
        _containers = containers;
        Skipped = skipped;
        _pieceGround = pieceGround;
    }

    internal RefundRoute Route { get; }

    /// <summary>from_id, else the player; null when neither exists or the tool needs no source.</summary>
    internal Thing? From { get; }

    internal Human? Player { get; }

    /// <summary>A chain's targets the game can use, in order; empty for the single words.</summary>
    internal List<RefundTarget> Usable { get; }

    /// <summary>Why each target left out was left out.</summary>
    internal List<string> Skipped { get; }

    /// <summary>
    /// The receivers of a route. A container a chain names must exist and have slots (refund_target_not_found or
    /// refund_target_not_container otherwise, as refusals); a target whose need is missing is skipped, with a
    /// warning when the caller named the chain (the default skips quietly). fromNamed: whether the request passed
    /// from_id (from is then null when it names no thing).
    /// </summary>
    internal static RefundReceivers Resolve(RefundRoute route, Thing? from, bool fromNamed, Vector3? pieceGround,
        List<GuardFinding> findings)
    {
        Human? player = from is Human human ? human : PlayerOrigin.Current().Player;
        List<RefundTarget> usable = new List<RefundTarget>();
        Dictionary<long, Thing> containers = new Dictionary<long, Thing>();
        List<string> skipped = new List<string>();
        if (route is RefundRoute.Chain chain)
        {
            RefundFrom named = !fromNamed ? RefundFrom.Absent : from != null ? RefundFrom.Found : RefundFrom.Missing;
            bool storage = from != null && Refunds.InventorySlots(from, Refunds.StoredIn(from)).Count > 0;
            RefundReach reach = new RefundReach(player != null, from is Stackable, storage, named);
            usable = RefundChainRule.Usable(chain.Targets, reach, skipped);
            if (route != RefundRoute.Default)
            {
                skipped.ForEach(why => findings.Add(new GuardFinding("refund_target_skipped", GuardLevel.Warning, why)));
            }

            foreach (RefundTarget target in usable)
            {
                if (target is RefundTarget.Container container)
                {
                    Container(container.Id, containers, findings);
                }
            }
        }

        return new RefundReceivers(route, from, player, usable, containers, skipped, pieceGround);
    }

    private static void Container(long id, Dictionary<long, Thing> containers, List<GuardFinding> findings)
    {
        if (!GameLookup.TryFindThing(new ThingId(id), out Thing thing) || thing.IsBeingDestroyed)
        {
            findings.Add(new GuardFinding("refund_target_not_found", GuardLevel.Refusal,
                $"refund_to names {id}, and there is no thing with that reference id."));
            return;
        }

        if (thing.Slots == null || !thing.Slots.Exists(static slot => slot != null && SlotAccess.Reaches(slot)))
        {
            findings.Add(new GuardFinding("refund_target_not_container", GuardLevel.Refusal,
                $"refund_to names {Names.Of(thing)} {id}, which has no slot a refund could go into."));
            return;
        }

        containers[id] = thing;
    }

    internal Thing? ContainerOf(long id) => _containers.TryGetValue(id, out Thing thing) ? thing : null;

    /// <summary>Every container a chain names, to check none is removed by the same request.</summary>
    internal IEnumerable<Thing> Containers => _containers.Values;

    /// <summary>
    /// The ground: in front of the source (the player carrying it, as today), else in front of the player, else where
    /// the first piece stood.
    /// </summary>
    internal Vector3 GroundAt()
    {
        if (From != null && !From.IsBeingDestroyed)
        {
            return Refunds.GroundBeside(From, From is Human human ? human : From.RootParentHuman);
        }

        if (Player != null)
        {
            return Refunds.GroundBeside(Player, Player);
        }

        return _pieceGround ?? Vector3.zero;
    }
}

/// <summary>
/// Gives back what deconstructing pieces would, never through the player's body. A refund is planned per item along
/// places tried in turn (RefundLedger): each place first tops up matching stacks (Stackable.AddQuantity, as
/// OnServer.CreateOrStack tops up a stack), then makes new stacks straight into empty slots that accept the item
/// (OnServer.Create into a slot, as a fabricator's output is made; Slot.AllowMove decides, on a slot the game's quick
/// moves would use: SlotAccess.AutoTakesNew); what no place takes goes on the ground, at rest. The places:
/// <list type="bullet">
/// <item>refund_to source (the single word, as before): the source itself when it is a stack, then its slots and
/// everything in them, then, when a player carries it (a worn belt), the rest of that player's; a source stored in a
/// locker does not use the locker's other slots.</item>
/// <item>a chain: inventory (the player's tree), source (the from_id stack), storage (from_id's slots, then those of
/// the container it is stored in), a container id (its slots), ground (in front of the source).</item>
/// </list>
/// The trees go level by level (a holder's own slots before the slots of the items in them). A player's own body
/// slots (hands, suit, helmet, back...) never get a new item, only the slots of worn and held items; a stack already in
/// a hand is topped up. Hidden slots (not interactable) and a stack's own slot (a cable coil's), and whatever is in
/// them, are left out: the game destroys their contents with the holder.
/// </summary>
internal static class Refunds
{
    internal const string Merged = "merged";
    internal const string InSlot = "slot";
    internal const string Ground = "ground";

    private const int MaximumDepth = 6;
    private const float GroundDistance = 1.0f;

    /// <summary>refund_to source as before: into the source's inventory tree, the rest in front of it.</summary>
    internal static void Deliver(Thing source, List<ItemAmount> refund, List<UpgradeRefundView> delivered)
    {
        Human? body = source is Human human ? human : source.RootParentHuman;
        Run(new List<Place> { Place.HolderOf(source, body) }, () => GroundBeside(source, body), refund, delivered);
    }

    /// <summary>Delivers along the receivers' route; refund_to ground at a remove_structure is DeliverAt instead.</summary>
    internal static void Deliver(RefundReceivers receivers, List<ItemAmount> refund, List<UpgradeRefundView> delivered)
    {
        switch (receivers.Route)
        {
            case { GivesBack: false }:
                return;
            case { NeedsHolder: true }:
                if (receivers.From != null)
                {
                    Deliver(receivers.From, refund, delivered);
                }

                return;
            default:
                Run(PlacesOf(receivers), receivers.GroundAt, refund, delivered);
                return;
        }
    }

    /// <summary>Makes the refund on the ground at a position (refund_to ground: where each piece stood), at rest.</summary>
    internal static void DeliverAt(Vector3 position, List<ItemAmount> refund, List<UpgradeRefundView> delivered)
    {
        foreach (KeyValuePair<Item, int> total in Totals(refund))
        {
            foreach (RefundStep step in RefundPlacement.Plan(total.Value, FullStack(total.Key), new List<int>(), 0))
            {
                Record(delivered, MakeAt(total.Key, step.Quantity, position), total.Key, step.Quantity, Ground,
                    RefundTarget.GroundName);
            }
        }
    }

    /// <summary>
    /// Where the refund would go if it were delivered now, as Deliver plans it; null when nothing is given back.
    /// refund_to ground (the single word) at a remove_structure: everything on the ground where each piece stood.
    /// </summary>
    internal static RefundPlanView? Forecast(RefundReceivers? receivers, List<ItemAmount> refund)
    {
        if (receivers == null || !receivers.Route.GivesBack)
        {
            return null;
        }

        List<Place> places;
        if (receivers.Route.NeedsHolder)
        {
            Thing? source = receivers.From;
            places = source != null
                ? new List<Place> { Place.HolderOf(source, source is Human human ? human : source.RootParentHuman) }
                : new List<Place>();
        }
        else
        {
            places = PlacesOf(receivers);
        }

        List<RefundDestinationView> destinations = new List<RefundDestinationView>();
        Registry registry = new Registry();
        RefundLedger ledger = new RefundLedger();
        foreach (KeyValuePair<Item, int> total in Totals(refund))
        {
            List<RefundOffer> offers = places.ConvertAll(place => place.OfferFor(total.Key, registry));
            foreach (RefundChainStep step in ledger.Plan(total.Value, FullStack(total.Key), offers))
            {
                bool fallback = step.Target >= places.Count;
                string target = fallback ? RefundTarget.GroundName : places[step.Target].Kind;
                destinations.Add(step.Step switch
                {
                    RefundStep.Merge => new RefundDestinationView(total.Key.PrefabName, step.Step.Quantity, target,
                        Merged, new ThingId(step.Key!.Value), false),
                    RefundStep.IntoSlot => new RefundDestinationView(total.Key.PrefabName, step.Step.Quantity, target,
                        InSlot, HolderOf(registry.Slot(step.Key!.Value)), false),
                    _ => new RefundDestinationView(total.Key.PrefabName, step.Step.Quantity, target, Ground, null,
                        fallback)
                });
            }
        }

        return new RefundPlanView(RefundArgs.View(receivers.Route), receivers.Skipped, destinations);
    }

    private static ThingId? HolderOf(Slot slot) => slot.Parent != null ? new ThingId(slot.Parent.ReferenceId) : null;

    // A chain's places in order; refund_to ground (the single word) outside remove_structure: the ground in front of
    // the source.
    private static List<Place> PlacesOf(RefundReceivers receivers)
    {
        List<Place> places = new List<Place>();
        if (receivers.Route is not RefundRoute.Chain)
        {
            places.Add(Place.OnGround);
            return places;
        }

        Thing? from = receivers.From;
        foreach (RefundTarget target in receivers.Usable)
        {
            switch (target)
            {
                case RefundTarget.Container container:
                    Thing? thing = receivers.ContainerOf(container.Id);
                    if (thing != null)
                    {
                        places.Add(new Place(target.Kind, null, InventorySlots(thing, null), false));
                    }

                    break;
                case { Kind: RefundTarget.InventoryName } when receivers.Player != null:
                    places.Add(new Place(target.Kind, null, InventorySlots(receivers.Player, receivers.Player), false));
                    break;
                case { Kind: RefundTarget.SourceName } when from is Stackable stack:
                    places.Add(new Place(target.Kind, stack, new List<Slot>(), false));
                    break;
                case { Kind: RefundTarget.StorageName } when from != null:
                    places.Add(new Place(target.Kind, null, InventorySlots(from, StoredIn(from)), false));
                    break;
                case { Kind: RefundTarget.GroundName }:
                    places.Add(Place.OnGround);
                    break;
            }
        }

        return places;
    }

    private delegate Vector3 GroundPosition();

    private static void Run(List<Place> places, GroundPosition ground, List<ItemAmount> refund,
        List<UpgradeRefundView> delivered)
    {
        Registry registry = new Registry();
        RefundLedger ledger = new RefundLedger();
        foreach (KeyValuePair<Item, int> total in Totals(refund))
        {
            List<RefundOffer> offers = places.ConvertAll(place => place.OfferFor(total.Key, registry));
            foreach (RefundChainStep step in ledger.Plan(total.Value, FullStack(total.Key), offers))
            {
                string target = step.Target < places.Count ? places[step.Target].Kind : RefundTarget.GroundName;
                Give(total.Key, step, target, registry, ground, delivered);
            }
        }
    }

    private static void Give(Item prefab, RefundChainStep step, string target, Registry registry, GroundPosition ground,
        List<UpgradeRefundView> delivered)
    {
        int quantity = step.Step.Quantity;
        switch (step.Step)
        {
            case RefundStep.Merge:
                Stackable onto = registry.Stack(step.Key!.Value);
                int added = onto.AddQuantity(quantity);
                Record(delivered, onto, prefab, added, Merged, target);
                if (added < quantity)
                {
                    // The stack filled between the survey and the merge; the rest goes down in front of the holder.
                    Record(delivered, MakeAt(prefab, quantity - added, ground()), prefab, quantity - added, Ground,
                        target);
                }

                break;
            case RefundStep.IntoSlot:
                Item made = OnServer.Create<Item>(prefab, registry.Slot(step.Key!.Value));
                SetQuantity(made, quantity);
                if (made.ParentSlot == null)
                {
                    // The slot refused it after all and the game put it in the world at the origin: bring it down in
                    // front of the holder, at rest.
                    OnServer.MoveToWorld(made, ground(), Quaternion.identity);
                }

                Record(delivered, made, prefab, quantity, made.ParentSlot != null ? InSlot : Ground, target);
                break;
            default:
                Record(delivered, MakeAt(prefab, quantity, ground()), prefab, quantity, Ground, target);
                break;
        }
    }

    /// <summary>One place a refund may go: its reply name, a stack to top up first, the slots it offers, or the ground.</summary>
    private sealed class Place
    {
        internal Place(string kind, Stackable? stack, List<Slot> slots, bool ground)
        {
            Kind = kind;
            Stack = stack;
            Slots = slots;
            IsGround = ground;
        }

        internal static Place OnGround { get; } = new Place(RefundTarget.GroundName, null, new List<Slot>(), true);

        internal string Kind { get; }

        private Stackable? Stack { get; }

        private List<Slot> Slots { get; }

        private bool IsGround { get; }

        // refund_to source as before: the source as a stack, then its tree and the player carrying it.
        internal static Place HolderOf(Thing source, Human? body) =>
            new Place(RefundTarget.SourceName, source as Stackable, InventorySlots(source, body), false);

        // The stacks with room and the empty slots that take this item, as they are now.
        internal RefundOffer OfferFor(Item prefab, Registry registry)
        {
            if (IsGround)
            {
                return RefundOffer.OnGround;
            }

            List<long> stacks = new List<long>();
            List<int> rooms = new List<int>();
            List<long> empty = new List<long>();
            if (Stack != null)
            {
                OfferStack(Stack, prefab, stacks, rooms, registry);
            }

            foreach (Slot slot in Slots)
            {
                DynamicThing? occupant = slot.Get();
                if (occupant == null)
                {
                    if (!(slot.Parent is Human) && SlotAccess.AutoTakesNew(prefab, slot))
                    {
                        empty.Add(registry.KeyOf(slot));
                    }
                }
                else if (!slot.IsLocked && occupant is Stackable stack && stack != Stack)
                {
                    OfferStack(stack, prefab, stacks, rooms, registry);
                }
            }

            return new RefundOffer(stacks, rooms, empty, false);
        }

        // A matching stack with room, as a place to top up.
        private static void OfferStack(Stackable stack, Item prefab, List<long> stacks, List<int> rooms,
            Registry registry)
        {
            if (!stack.IsBeingDestroyed && prefab is Stackable kind && stack.CanStack(kind) &&
                stack.MaxQuantity > stack.Quantity)
            {
                stacks.Add(registry.KeyOf(stack));
                rooms.Add(stack.MaxQuantity - stack.Quantity);
            }
        }
    }

    /// <summary>The stacks and slots of one delivery by key, so every place names the same slot the same way.</summary>
    private sealed class Registry
    {
        private readonly Dictionary<Slot, long> _slotKeys = new Dictionary<Slot, long>();
        private readonly List<Slot> _slots = new List<Slot>();
        private readonly Dictionary<long, Stackable> _stacks = new Dictionary<long, Stackable>();

        internal long KeyOf(Slot slot)
        {
            if (!_slotKeys.TryGetValue(slot, out long key))
            {
                key = _slots.Count;
                _slots.Add(slot);
                _slotKeys[slot] = key;
            }

            return key;
        }

        internal long KeyOf(Stackable stack)
        {
            _stacks[stack.ReferenceId] = stack;
            return stack.ReferenceId;
        }

        internal Slot Slot(long key) => _slots[(int)key];

        internal Stackable Stack(long key) => _stacks[key];
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

    // The outermost holder of a thing stored in a slot (the locker a coil stack is in), or null.
    internal static Thing? StoredIn(Thing source) =>
        source is DynamicThing item && item.ParentSlot != null && item.RootParent != null && item.RootParent != source
            ? item.RootParent
            : null;

    // The source's slots and every slot below them, then the rest of the holder carrying it, each thing
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

    private static void Record(List<UpgradeRefundView> delivered, Thing item, Item prefab, int quantity, string where,
        string target)
    {
        if (quantity > 0)
        {
            delivered.Add(new UpgradeRefundView(new ThingId(item.ReferenceId), prefab.PrefabName, quantity, where,
                target));
        }
    }
}
