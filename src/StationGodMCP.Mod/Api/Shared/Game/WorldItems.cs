#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// Every item in the world wherever it is. Items come from OcclusionManager.AllDynamicThings, the list the game's own
/// deletelooseitems command walks (a filter naming prefabs reads only that list's things the prefab index files under
/// matching names), and an item is loose when it has no ParentSlot (that command's rule). Organs
/// (lungs, brain, stomach) are Items in the game and are left out.
/// </summary>
internal static class WorldItems
{
    internal static List<Item> All() => Keep(Pools.Snapshot(OcclusionManager.AllDynamicThings));

    /// <summary>
    /// Every item whose prefab name the match may keep: from the prefab index when it can answer (ThingIndex), else
    /// every item.
    /// </summary>
    internal static List<Item> Named(PrefabMatch match) =>
        ThingIndex.DynamicThings(match) is { } indexed ? Keep(indexed) : All();

    private static List<Item> Keep(List<DynamicThing> things)
    {
        List<Item> items = new List<Item>(things.Count);
        for (int index = 0; index < things.Count; index++)
        {
            if (things[index] is Item item && item != null && !(item is Organ) && !item.IsCursor &&
                !item.IsBeingDestroyed)
            {
                items.Add(item);
            }
        }

        return items;
    }

    /// <summary>Every item the filter keeps, described, in the game's list order.</summary>
    internal static List<ItemRecord> Collect(ItemFilter filter, PlayerOrigin origin)
    {
        List<Item> items = Named(filter.Prefab);
        List<ItemRecord> records = new List<ItemRecord>();
        foreach (Item item in items)
        {
            if (filter.Names(item))
            {
                ItemRecord record = Describe(item, origin);
                if (filter.Keeps(record))
                {
                    records.Add(record);
                }
            }
        }

        return records;
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

    private const int MaximumDepth = 16;

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
/// find_items' and item_totals' filter: names, where the item is, what holds it, how near the player and in what area
/// (min and max, or near with radius_m: where its outermost holder stands; a machine's stock where the machine
/// stands, a silo's store where the silo stands). vault_deposit reads the names and places without an area or an exact
/// prefab. Also keeps machine stock (MachineStock) for location any or machine_stock, and silo stock (SiloStock) for
/// location any or silo; a WorldItems walk never yields those locations, so callers that only collect items
/// (list_containers) never see them.
/// </summary>
internal sealed class ItemFilter
{
    internal static readonly ItemFilter Everything =
        new ItemFilter(PrefabMatch.Any, null, "any", null, null, PointArea.Anywhere);

    private ItemFilter(PrefabMatch prefab, string? nameContains, string location, ThingId? withinId,
        double? nearPlayerM, PointArea area)
    {
        Prefab = prefab;
        NameContains = nameContains;
        Location = location;
        WithinId = withinId;
        NearPlayerM = nearPlayerM;
        Area = area;
    }

    /// <summary>prefab (find_items and item_totals) and prefab_contains.</summary>
    internal PrefabMatch Prefab { get; }

    internal string? NameContains { get; }

    /// <summary>any, ground, player, stored, machine_stock or silo.</summary>
    internal string Location { get; }

    internal ThingId? WithinId { get; }

    internal double? NearPlayerM { get; }

    /// <summary>Where the item's outermost holder stands; anywhere when no area is given.</summary>
    internal PointArea Area { get; }

    /// <summary>find_items' and item_totals' filter: the names and places, an exact prefab and an area.</summary>
    internal static ItemFilter ParseWithArea(Args args) =>
        Parse(args, PrefabMatches.Parse(args), AreaArgs.Parse(args));

    /// <summary>vault_deposit's filter: the names and places, prefab_contains only, anywhere.</summary>
    internal static ItemFilter Parse(Args args) =>
        Parse(args, new PrefabMatch(null, args.OptionalString("prefab_contains")), PointArea.Anywhere);

    private static ItemFilter Parse(Args args, PrefabMatch prefab, PointArea area)
    {
        string location = args.OptionalString("location") ?? "any";
        if (location != "any" && location != "ground" && location != "player" && location != "stored" &&
            location != MachineStock.Location && location != SiloStock.Location)
        {
            throw ApiErrors.InvalidArgument(
                "Argument 'location' must be any, ground, player, stored, machine_stock or silo.");
        }

        return new ItemFilter(prefab, args.OptionalString("name_contains"), location,
            RequireHolder(args.OptionalThingId("within_id")), args.OptionalPositiveDouble("near_player_m"), area);
    }

    // A within_id that names nothing is refused, as container_contents refuses it, so a mistyped id never reads as an
    // empty holder.
    private static ThingId? RequireHolder(ThingId? withinId)
    {
        if (withinId.HasValue &&
            (!GameLookup.TryFindThing(withinId.Value, out Thing holder) || holder.IsBeingDestroyed))
        {
            throw ApiErrors.ThingNotFound(withinId.Value);
        }

        return withinId;
    }

    internal static ItemFilter StoredOnly() =>
        new ItemFilter(PrefabMatch.Any, null, "stored", null, null, PointArea.Anywhere);

    internal bool Names(Item item) => Prefab.Keeps(item.PrefabName) && Contains(item.DisplayName, NameContains);

    /// <summary>Whether machine stock can match at all: location any or machine_stock.</summary>
    internal bool WantsStock => Location == "any" || Location == MachineStock.Location;

    /// <summary>Stock matches the ingot's prefab and name, else the reagent's name; within_id is the machine.</summary>
    internal bool Keeps(StockRecord record)
    {
        if (!WantsStock || !Prefab.Keeps(record.IngotPrefab) || !Contains(record.DisplayName, NameContains) ||
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
    internal bool WantsSilo => Location == "any" || Location == SiloStock.Location;

    /// <summary>A stored thing matches by its prefab and shown name, where the silo stands; within_id is the silo.</summary>
    internal bool Keeps(SiloRecord record)
    {
        if (!WantsSilo || !Prefab.Keeps(record.PrefabName) || !Contains(record.DisplayName, NameContains) ||
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

    internal bool Keeps(ItemRecord record)
    {
        if (Location != "any" && record.Location != Location)
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
