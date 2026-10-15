#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Verbs;
using StationGodMCP.WorldModel;
using World = StationGodMCP.WorldModel.World;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// Every item in the world wherever it is. Items come from the world model (World.Items: the Items of
/// OcclusionManager.AllDynamicThings, the list the game's own deletelooseitems command walks; a filter naming prefabs
/// reads only the items the world index files under matching names), and an item is loose when it has no ParentSlot
/// (that command's rule). Organs (lungs, brain, stomach) are Items in the game and are left out.
/// </summary>
internal static class WorldItems
{
    internal static List<Item> All() => Keep(World.Items());

    /// <summary>Every item whose prefab name the match may keep (the world index's); every item for no match.</summary>
    internal static List<Item> Named(PrefabMatch match) => match.IsActive ? Keep(World.Items(match)) : All();

    /// <summary>Every food, drink and rotten food item (World.Consumables), as All keeps items.</summary>
    internal static List<Item> Consumables() => Keep(World.Consumables());

    private static List<Item> Keep(List<Item> items)
    {
        items.RemoveAll(static item => item == null || item is Organ || item.IsCursor || item.IsBeingDestroyed);
        return items;
    }

    /// <summary>Every item the filter keeps, described, in the game's list order.</summary>
    internal static List<ItemRecord> Collect(ItemFilter filter, PlayerOrigin origin)
    {
        List<Item> items = Named(filter.IndexMatch);
        List<ItemRecord> records = new List<ItemRecord>();
        foreach (Item item in items)
        {
            ItemRecord? record = Kept(item, filter, origin);
            if (record != null)
            {
                records.Add(record);
            }
        }

        return records;
    }

    /// <summary>
    /// Collect spread over frames (a Work or ScanBuild body): the candidates (the items the index files under the
    /// filter's names, or the Item class's candidates checked one per step), each item the filter keeps handed to found
    /// as its turn comes, one candidate per step.
    /// </summary>
    internal static IEnumerable<object?> Scan(ItemFilter filter, PlayerOrigin origin, Action<ItemRecord> found)
    {
        bool named = filter.IndexMatch.IsActive;
        IReadOnlyList<Thing> candidates = named ? World.Items(filter.IndexMatch) : World.Candidates(WorldClass.Item);
        yield return null;
        for (int index = 0; index < candidates.Count; index++)
        {
            Thing candidate = candidates[index];
            if ((named || World.Holds(WorldClass.Item, candidate)) && candidate is Item item &&
                !(item == null || item is Organ || item.IsCursor || item.IsBeingDestroyed))
            {
                ItemRecord? record = Kept(item, filter, origin);
                if (record != null)
                {
                    found(record);
                }
            }

            yield return null;
        }
    }

    private static ItemRecord? Kept(Item item, ItemFilter filter, PlayerOrigin origin)
    {
        if (!filter.Names(item) || !filter.CouldHold(item.ParentSlot != null))
        {
            return null;
        }

        ItemRecord record = Describe(item, origin);
        return filter.Keeps(record) ? record : null;
    }

    /// <summary>
    /// exclude_in_use: takes out the items that are parts a device is using (PartsInUse: a running
    /// filtration machine's filters, a chip in its housing) or sit inside one, and answers how many it took out.
    /// </summary>
    internal static int LeaveOutPartsInUse(List<ItemRecord> records) =>
        records.RemoveAll(static record => InUse(record.Path));

    private static bool InUse(List<Slot> path)
    {
        foreach (Slot slot in path)
        {
            if (PartsInUse.InUse(slot))
            {
                return true;
            }
        }

        return false;
    }

    internal static ItemRecord Describe(Item item, PlayerOrigin origin)
    {
        HolderChain chain = HolderChain.Of(item);
        return new ItemRecord(item, chain.Root, chain.Path, chain.Carrier, chain.Location,
            origin.ExactDistanceTo(chain.Root.Position));
    }

    // Item.GetQuantity is not a count for everything: BatteryCell returns its charge ratio there, so only IQuantity
    // items report it.
    internal static double QuantityOf(Item item) => item is IQuantity ? item.GetQuantity : 1;

    internal static double? MaxQuantityOf(Item item) => item is IQuantity quantity ? quantity.GetMaxQuantity : null;
}

/// <summary>
/// A dynamic thing's holders from its own slot outwards (DynamicThing.ParentSlot, then the holder's own ParentSlot, at
/// most 16 deep), the outermost holder, and the first player among them.
/// </summary>
internal sealed class HolderChain
{
    internal const string Ground = "ground";
    internal const string Player = "player";
    internal const string Stored = "stored";

    internal const int MaximumDepth = 16;

    private HolderChain(Thing root, List<Slot> path, Human? carrier)
    {
        Root = root;
        Path = path;
        Carrier = carrier;
    }

    /// <summary>The outermost holder, or the thing itself when loose.</summary>
    internal Thing Root { get; }

    /// <summary>The thing's slot first, then outwards.</summary>
    internal List<Slot> Path { get; }

    internal Human? Carrier { get; }

    /// <summary>ground (in no slot), player (a player holds it at any depth) or stored.</summary>
    internal string Location => Path.Count == 0 ? Ground : Carrier != null ? Player : Stored;

    internal static HolderChain Of(DynamicThing thing)
    {
        List<Slot> path = new List<Slot>();
        Human? carrier = null;
        Thing holder = thing;
        Slot? slot = thing.ParentSlot;
        while (slot != null && slot.Parent != null && path.Count < MaximumDepth)
        {
            path.Add(slot);
            holder = slot.Parent;
            if (carrier == null && holder is Human human)
            {
                carrier = human;
            }

            slot = holder is DynamicThing dynamicHolder ? dynamicHolder.ParentSlot : null;
        }

        return new HolderChain(holder, path, carrier);
    }

    /// <summary>
    /// The thing that places this one in the world: a thing in a slot keeps no position of its own (a stored item reads
    /// the world origin), so its outermost holder; anything else itself.
    /// </summary>
    internal static Thing PlaceOf(Thing thing) => thing is DynamicThing item ? RootOf(item) : thing;

    /// <summary>The outermost holder alone, as Of finds it, without building the chain.</summary>
    internal static Thing RootOf(DynamicThing thing)
    {
        Thing holder = thing;
        Slot? slot = thing.ParentSlot;
        for (int depth = 0; slot != null && slot.Parent != null && depth < MaximumDepth; depth++)
        {
            holder = slot.Parent;
            slot = holder is DynamicThing dynamicHolder ? dynamicHolder.ParentSlot : null;
        }

        return holder;
    }

    internal static List<HeldInView> ViewOf(List<Slot> path)
    {
        List<HeldInView> heldIn = new List<HeldInView>(path.Count);
        foreach (Slot slot in path)
        {
            heldIn.Add(new HeldInView(GameLookup.ViewOf(slot.Parent), slot.SlotIndex, slot.DisplayName));
        }

        return heldIn;
    }

    internal List<HeldInView> View() => ViewOf(Path);
}

/// <summary>An item, its holders from the inside out, and where it is.</summary>
internal sealed class ItemRecord
{
    internal ItemRecord(Item item, Thing root, List<Slot> path, Human? carrier, string location, double? distance)
    {
        Item = item;
        Root = root;
        Path = path;
        Carrier = carrier;
        Location = location;
        Quantity = WorldItems.QuantityOf(item);
        MaxQuantity = WorldItems.MaxQuantityOf(item);
        Distance = distance;
    }

    internal Item Item { get; }

    /// <summary>The outermost holder, or the item itself when loose.</summary>
    internal Thing Root { get; }

    /// <summary>The item's slot first, then outwards.</summary>
    internal List<Slot> Path { get; }

    internal Human? Carrier { get; }

    /// <summary>ground, player or stored.</summary>
    internal string Location { get; }

    internal double Quantity { get; }

    internal double? MaxQuantity { get; }

    /// <summary>From the player to the outermost holder, unrounded; null without a player.</summary>
    internal double? Distance { get; }

    /// <summary>Nearest first (no player: all last), then by prefab name and reference id.</summary>
    internal static int NearestFirst(ItemRecord a, ItemRecord b)
    {
        int byDistance = (a.Distance ?? double.MaxValue).CompareTo(b.Distance ?? double.MaxValue);
        if (byDistance != 0)
        {
            return byDistance;
        }

        int byPrefab = string.CompareOrdinal(a.Item.PrefabName, b.Item.PrefabName);
        return byPrefab != 0 ? byPrefab : a.Item.ReferenceId.CompareTo(b.Item.ReferenceId);
    }

    internal bool IsWithin(long holderId)
    {
        for (int index = 0; index < Path.Count; index++)
        {
            if (Path[index].Parent.ReferenceId == holderId)
            {
                return true;
            }
        }

        return false;
    }

    internal ItemFields Fields() =>
        new ItemFields(GameLookup.ViewOf(Item), Quantity, MaxQuantity, Place());

    internal ItemPlace Place()
    {
        List<HeldInView> heldIn = HolderChain.ViewOf(Path);
        double? distance = Distance.HasValue ? System.Math.Round(Distance.Value, PositionView.Decimals) : null;
        return new ItemPlace(Location, Carrier != null ? Carrier.DisplayName : null, heldIn,
            GameLookup.ViewOf(Root.Position), distance);
    }

    internal ItemView ToView() => new ItemView(Fields());
}

/// <summary>
/// The items a list keeps: by prefab (the verbs' prefab filter) and part of the shown name, where the item is, what
/// holds it, how near the player and in what area (where its outermost holder stands; a machine's stock where the
/// machine stands, a silo's store where the silo stands). Also keeps machine stock (MachineStock) for location any or
/// machine_stock, and silo stock (SiloStock) for location any or silo; a WorldItems walk never yields those locations,
/// so callers that only collect items (find container) never see them.
/// </summary>
internal sealed class ItemFilter
{
    internal const string AnyLocation = "any";

    internal static readonly ItemFilter Everything = new ItemFilter(null, null, AnyLocation, null, null, PointArea.Anywhere);

    private ItemFilter(PrefabFilter? prefab, string? nameContains, string location, ThingId? withinId,
        double? nearPlayerM, PointArea area)
    {
        Prefab = prefab;
        NameContains = nameContains;
        Location = location;
        WithinId = withinId;
        NearPlayerM = nearPlayerM;
        Area = area;
    }

    /// <summary>The prefabs kept; null keeps every prefab.</summary>
    internal PrefabFilter? Prefab { get; }

    internal string? NameContains { get; }

    /// <summary>any, ground, player, stored, machine_stock or silo.</summary>
    internal string Location { get; }

    internal ThingId? WithinId { get; }

    internal double? NearPlayerM { get; }

    /// <summary>Where the item's outermost holder stands; anywhere when no area is given.</summary>
    internal PointArea Area { get; }

    /// <summary>The prefab index's match for the prefabs kept (PrefabMatch.ForIndex).</summary>
    internal PrefabMatch IndexMatch => PrefabMatch.ForIndex(Prefab);

    /// <summary>
    /// The filter a list's options give: prefab, part of the shown name, location, the holder items must be inside
    /// (within: a thing's id, refused thing_not_found when it names none), metres around the player and an area.
    /// </summary>
    internal static ItemFilter Of(PrefabFilter? prefab, string? name, string? location, string? within,
        double? nearPlayerM, PointArea area) =>
        new ItemFilter(prefab, name, location ?? AnyLocation, Holder(within), nearPlayerM, area);

    /// <summary>A deposit's filter (prefab_contains, name_contains, location, within_id), anywhere.</summary>
    // A within that names nothing is refused, so a mistyped id never reads as an empty holder.
    private static ThingId? Holder(string? within)
    {
        if (within == null)
        {
            return null;
        }

        if (ThingId.TryRead(new Newtonsoft.Json.Linq.JValue(within), out ThingId id) &&
            GameLookup.TryFindThing(id, out Thing holder) && !holder.IsBeingDestroyed)
        {
            return id;
        }

        throw ApiErrors.Refused(ApiErrors.ThingNotFoundCode, $"within: no thing has id {within}.");
    }

    internal static ItemFilter StoredOnly() =>
        new ItemFilter(null, null, HolderChain.Stored, null, null, PointArea.Anywhere);

    internal bool Names(Item item) =>
        (Prefab == null || Prefab.Keeps(item.PrefabName, item.PrefabHash)) && Contains(item.DisplayName, NameContains);

    /// <summary>Whether the prefab filter keeps a prefab known only by its name (its hash the name's).</summary>
    internal bool KeepsPrefab(string? prefabName) =>
        Prefab == null ||
        Prefab.Keeps(prefabName, string.IsNullOrEmpty(prefabName) ? 0 : UnityEngine.Animator.StringToHash(prefabName));

    /// <summary>Whether machine stock can match at all: location any or machine_stock.</summary>
    internal bool WantsStock => Location == AnyLocation || Location == MachineStock.Location;

    /// <summary>Stock matches the ingot's prefab and name, else the reagent's name; within_id is the machine.</summary>
    internal bool Keeps(StockRecord record)
    {
        if (!WantsStock || !KeepsPrefab(record.IngotPrefab) || !Contains(record.DisplayName, NameContains) ||
            !Area.Contains(Bodies.V(record.Machine.Position)))
        {
            return false;
        }

        if (WithinId.HasValue && record.Machine.ReferenceId != WithinId.Value.Value)
        {
            return false;
        }

        return !NearPlayerM.HasValue || (record.Distance.HasValue && record.Distance.Value <= NearPlayerM.Value);
    }

    /// <summary>Whether silo stock can match at all: location any or silo.</summary>
    internal bool WantsSilo => Location == AnyLocation || Location == SiloStock.Location;

    /// <summary>A stored thing matches by its prefab and shown name, where the silo stands; within_id is the silo.</summary>
    internal bool Keeps(SiloRecord record)
    {
        if (!WantsSilo || !KeepsPrefab(record.PrefabName) || !Contains(record.DisplayName, NameContains) ||
            !Area.Contains(Bodies.V(record.Silo.Position)))
        {
            return false;
        }

        if (WithinId.HasValue && record.Silo.ReferenceId != WithinId.Value.Value)
        {
            return false;
        }

        return !NearPlayerM.HasValue || (record.Distance.HasValue && record.Distance.Value <= NearPlayerM.Value);
    }

    /// <summary>
    /// Whether an item in a slot (inSlot) or loose could be kept at all, before it is described (review D, R-D-14: a
    /// stored-only find dropped every loose item only after describing it): ground wants a loose item, stored and
    /// player one in a slot, and within_id one held.
    /// </summary>
    internal bool CouldHold(bool inSlot) => Location switch
    {
        HolderChain.Ground => !inSlot && !WithinId.HasValue,
        HolderChain.Stored or HolderChain.Player => inSlot,
        _ => inSlot || !WithinId.HasValue
    };

    internal bool Keeps(ItemRecord record)
    {
        if (Location != AnyLocation && record.Location != Location)
        {
            return false;
        }

        if (WithinId.HasValue && !record.IsWithin(WithinId.Value.Value))
        {
            return false;
        }

        if (!Area.Contains(Bodies.V(record.Root.Position)))
        {
            return false;
        }

        return !NearPlayerM.HasValue || (record.Distance.HasValue && record.Distance.Value <= NearPlayerM.Value);
    }

    /// <summary>Whether text contains part, ignoring case; an empty part matches anything.</summary>
    internal static bool Contains(string? text, string? part) =>
        string.IsNullOrEmpty(part) ||
        (!string.IsNullOrEmpty(text) && text!.IndexOf(part, System.StringComparison.OrdinalIgnoreCase) >= 0);
}
