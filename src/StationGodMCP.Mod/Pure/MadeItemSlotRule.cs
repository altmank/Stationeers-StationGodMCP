#nullable enable

namespace StationGodMCP.Pure;

/// <summary>What build item may say about a named empty slot before the item is made.</summary>
internal enum MadeItemSlot
{
    /// <summary>Slot.AllowMove takes the prefab: the item goes in.</summary>
    Takes,

    /// <summary>The prefab reports no slot class: the made item is judged by Slot.AllowMove before it goes in.</summary>
    CheckMade,

    Refuses
}

/// <summary>
/// build item into a named empty slot: Slot.AllowMove judges an item by its own SlotType, and move item judges the item
/// it moves. A prefab can report no class (SlotType None) while the items made from it have one: ScriptedScreens'
/// Circuitboard (Lua Chip) prefab says None and a made board goes into a Console's Circuitboard slot by move item. So
/// when the prefab's only fault is that missing class, the made item is checked with move item's test before it goes in
/// (and taken back, refused, when that says no); any other refusal (Thing.CanEnter, a draggable item) stands.
/// </summary>
internal static class MadeItemSlotRule
{
    internal static MadeItemSlot Judge(bool prefabTaken, bool prefabHasClass, bool canEnter, bool draggable) =>
        prefabTaken ? MadeItemSlot.Takes
        : !prefabHasClass && canEnter && !draggable ? MadeItemSlot.CheckMade
        : MadeItemSlot.Refuses;
}
