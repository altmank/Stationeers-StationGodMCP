#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Assets.Scripts.Networking;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Objects.Motherboards;
using Assets.Scripts.Objects.Pipes;
using Assets.Scripts.Objects.Structures;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Controllers;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game.Structures;

/// <summary>
/// What swap door hands from the old door to the new one, planned (Plan: the report) or done (Act: the job, just
/// before the old door is destroyed). What points at a door lives on its data networks and on the boards linked to it.
/// <list type="bullet">
/// <item>A board linked to the door (Door.LinkedMotherboards: airlock and door control boards) has the old door
/// replaced in its linked devices in place, so the order is kept, and the game's own OnLinkWithBoard and
/// OnUnlinkWithBoard calls are made, which set the door's AirlockController.</item>
/// <item>An IC housing's pins are set as write pins sets them, with each pin's network flag.</item>
/// <item>Any public settable property of a device on the networks, or of a board in a device's slots, that holds the
/// old door is set to the new one through the game's own setter. Examples: a logic reader's, writer's or mirror's
/// device (CurrentDevice, CurrentOutput), and an airlock board's ExteriorAirlock and InteriorAirlock. The setters flag
/// the change for clients.</item>
/// <item>A field that still holds the old door after that is reported as still_points, with its name.</item>
/// <item>A chip whose program names the old door's id, or batch-writes another prefab, is reported as still_points
/// (DoorHandoverRule).</item>
/// </list>
/// Then the label is copied (Thing.RenameThing), and the old door's Open and Lock are set on the new door
/// (OnServer.Interact) and read back. Nothing here throws: a step that fails is reported as failed. Main thread, host.
/// </summary>
internal static class DoorHandover
{
    private const BindingFlags AllInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly Dictionary<Type, List<PropertyInfo>> Settable = new Dictionary<Type, List<PropertyInfo>>();

    private static readonly Dictionary<Type, List<FieldInfo>> Pointing = new Dictionary<Type, List<FieldInfo>>();

    /// <summary>The report's handover: what points at the old door and what the swap would do with it.</summary>
    internal static List<DoorHandoverView> Plan(Door old, Structure target)
    {
        List<DoorHandoverView> handover = new List<DoorHandoverView>();
        Walk(old, null, target, handover);
        ThingId id = new ThingId(old.ReferenceId);
        if (!string.IsNullOrEmpty(old.CustomName))
        {
            handover.Add(new DoorHandoverView(id, old.PrefabName, "label", DoorHandoverRule.Keep, old.CustomName));
        }

        if (old.IsOpen)
        {
            handover.Add(new DoorHandoverView(id, old.PrefabName, "open", DoorHandoverRule.Keep, null));
        }

        if (old.IsLocked)
        {
            handover.Add(new DoorHandoverView(id, old.PrefabName, "lock", DoorHandoverRule.Keep, null));
        }

        return handover;
    }

    /// <summary>The job's handover, with the new door standing and the old one about to be destroyed.</summary>
    internal static List<DoorHandoverView> Act(Door old, Door door)
    {
        List<DoorHandoverView> handover = new List<DoorHandoverView>();
        Walk(old, door, door, handover);
        Label(old, door, handover);
        State(door, "open", old.IsOpen, door.InteractOpen, static d => d.IsOpen, handover);
        State(door, "lock", old.IsLocked, door.InteractLock, static d => d.IsLocked, handover);
        return handover;
    }

    // Every pointer at the old door, repointed when door is given; target is the prefab or door that replaces it.
    private static void Walk(Door old, Door? door, Structure target, List<DoorHandoverView> handover)
    {
        HashSet<long> seen = new HashSet<long> { old.ReferenceId };
        if (door != null)
        {
            seen.Add(door.ReferenceId);
        }

        foreach (Motherboard board in new List<Motherboard>(old.LinkedMotherboards))
        {
            if (board != null && seen.Add(board.ReferenceId))
            {
                Guarded(board, "board", handover, () => Board(board, old, door, handover));
                Guarded(board, "board", handover, () => Members(board, old, door, target, handover));
            }
        }

        foreach (CableNetwork network in new List<CableNetwork>(old.ConnectedCableNetworks))
        {
            if (network == null)
            {
                continue;
            }

            foreach (Device device in ControllerScan.DataDevices(network))
            {
                if (!seen.Add(device.ReferenceId))
                {
                    continue;
                }

                if (device is CircuitHousing housing)
                {
                    Guarded(housing, "pins", handover, () => Pins(housing, old, door, handover));
                }

                Guarded(device, "device", handover, () => Members(device, old, door, target, handover));
                SlotBoards(device, old, door, target, seen, handover);
            }
        }

        Guarded(old, "chips", handover, () => Chips(old, target, handover));
    }

    // The boards in a device's slots (a computer's airlock board) that the door is not linked with but which hold it.
    private static void SlotBoards(Device device, Door old, Door? door, Structure target, HashSet<long> seen,
        List<DoorHandoverView> handover)
    {
        if (device.Slots == null)
        {
            return;
        }

        for (int index = 0; index < device.Slots.Count; index++)
        {
            if (device.Slots[index]?.Get() is Motherboard board && board != null && seen.Add(board.ReferenceId))
            {
                Guarded(board, "board", handover, () => Members(board, old, door, target, handover));
            }
        }
    }

    // The board's linked devices: the old door replaced where it stands in the list, the link calls made.
    private static void Board(Motherboard board, Door old, Door? door, List<DoorHandoverView> handover)
    {
        List<Device> linked = board.LinkedDevices;
        int at = linked.IndexOf(old);
        if (at < 0)
        {
            return;
        }

        if (door != null)
        {
            linked[at] = door;
            old.OnUnlinkWithBoard(board);
            door.OnLinkWithBoard(board);
        }

        handover.Add(View(board, "board", door != null ? DoorHandoverRule.Repointed : DoorHandoverRule.Repoint, null));
    }

    private static void Pins(CircuitHousing housing, Door old, Door? door, List<DoorHandoverView> handover)
    {
        int count = Math.Min(housing.Devices.Length, ChipWrites.PinUpdateFlags.Length);
        for (int pin = 0; pin < count; pin++)
        {
            if (!ReferenceEquals(housing.Devices[pin], old))
            {
                continue;
            }

            if (door != null)
            {
                housing.Devices[pin] = door;
                if (NetworkManager.IsServer)
                {
                    housing.NetworkUpdateFlags |= ChipWrites.PinUpdateFlags[pin];
                }
            }

            handover.Add(View(housing, "d" + pin,
                door != null ? DoorHandoverRule.Repointed : DoorHandoverRule.Repoint, null));
        }

        if (door != null)
        {
            Controllers.Forget();
        }
    }

    // A thing's settable properties that hold the old door, set through the game's setter; then any field that still
    // holds it, reported. A board's linked list and a housing's pins are lists, not such members.
    private static void Members(Thing owner, Door old, Door? door, Structure target, List<DoorHandoverView> handover)
    {
        HashSet<string> named = new HashSet<string>();
        foreach (PropertyInfo property in SettableOf(owner.GetType()))
        {
            if (!Holds(owner, property, old) || !named.Add(property.Name))
            {
                continue;
            }

            if (!property.PropertyType.IsInstanceOfType(target))
            {
                handover.Add(View(owner, property.Name, DoorHandoverRule.StillPoints,
                    $"{property.Name} takes a {property.PropertyType.Name}; {target.PrefabName} is none."));
                continue;
            }

            if (door != null)
            {
                property.SetValue(owner, door);
            }

            handover.Add(View(owner, property.Name,
                door != null ? DoorHandoverRule.Repointed : DoorHandoverRule.Repoint, null));
        }

        foreach (FieldInfo field in PointingOf(owner.GetType()))
        {
            if (ReferenceEquals(field.GetValue(owner), old) && (door != null || !Covered(field, named)))
            {
                handover.Add(View(owner, field.Name, DoorHandoverRule.StillPoints,
                    $"{owner.GetType().Name}.{field.Name} still holds the old door; set it again in game " +
                    "(screwdriver or the device's own setting)."));
            }
        }
    }

    private static bool Holds(Thing owner, PropertyInfo property, Door old)
    {
        try
        {
            return ReferenceEquals(property.GetValue(owner), old);
        }
        catch (TargetInvocationException)
        {
            // A getter of the game's that needs state this thing does not have (no network, no parent): it holds
            // nothing to repoint.
            return false;
        }
    }

    // A planned property repoint sets the field behind it (CurrentDevice sets _currentDevice).
    private static bool Covered(FieldInfo field, HashSet<string> properties)
    {
        string bare = field.Name.TrimStart('_').ToLowerInvariant();
        foreach (string property in properties)
        {
            if (string.Equals(property, bare, StringComparison.OrdinalIgnoreCase) ||
                bare.EndsWith(property.ToLowerInvariant(), StringComparison.Ordinal) ||
                property.ToLowerInvariant().EndsWith(bare, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // Chips that reach the door by its id or by a batch: what the swap cannot repoint (DoorHandoverRule).
    private static void Chips(Door old, Structure target, List<DoorHandoverView> handover)
    {
        bool samePrefab = target.PrefabHash == old.PrefabHash;
        HashSet<string> seen = new HashSet<string>();
        foreach (Controller controller in Controllers.Of(old))
        {
            string? note = DoorHandoverRule.StillPointing(controller.Via, old.ReferenceId, samePrefab);
            if (note == null || !seen.Add(controller.ReferenceId + "/" + controller.Via))
            {
                continue;
            }

            string line = controller.Line is { } at ? $" (line {at})" : string.Empty;
            handover.Add(new DoorHandoverView(new ThingId(controller.ReferenceId), controller.PrefabName,
                controller.Via, DoorHandoverRule.StillPoints, note + line));
        }
    }

    private static void Label(Door old, Door door, List<DoorHandoverView> handover)
    {
        if (string.IsNullOrEmpty(old.CustomName))
        {
            return;
        }

        try
        {
            Thing.RenameThing(door.ReferenceId, old.CustomName);
            handover.Add(View(door, "label", DoorHandoverRule.Restored, old.CustomName));
        }
        catch (Exception error)
        {
            // Thing.RenameThing raising the game's rename events on the new door.
            StationGodMod.LogWarning($"door label handover to {door.ReferenceId} failed: {error}");
            handover.Add(View(door, "label", DoorHandoverRule.Failed, error.Message));
        }
    }

    // The old door's open or lock set on the new door as a player's interaction sets it, then read back.
    private static void State(Door door, string how, bool wanted, Interactable? interactable, Func<Door, bool> read,
        List<DoorHandoverView> handover)
    {
        if (!wanted)
        {
            return;
        }

        try
        {
            if (interactable != null)
            {
                OnServer.Interact(interactable, 1);
            }

            handover.Add(read(door)
                ? View(door, how, DoorHandoverRule.Restored, null)
                : View(door, how, DoorHandoverRule.Failed,
                    $"The new door did not take {how}; set it in game or with write logic."));
        }
        catch (Exception error)
        {
            // OnServer.Interact running the door's interaction (power, animation) on a door just built.
            StationGodMod.LogWarning($"door {how} handover to {door.ReferenceId} failed: {error}");
            handover.Add(View(door, how, DoorHandoverRule.Failed, error.Message));
        }
    }

    // One step that reads or writes the game: a failure is reported, never thrown into the swap.
    private static void Guarded(Thing owner, string how, List<DoorHandoverView> handover, Action step)
    {
        try
        {
            step();
        }
        catch (Exception error)
        {
            // A getter or setter of the game's (a property read, a board link) failing on this thing.
            StationGodMod.LogWarning($"door handover at {owner.ReferenceId} ({how}) failed: {error}");
            handover.Add(View(owner, how, DoorHandoverRule.Failed, error.Message));
        }
    }

    // Public instance properties with a public getter and setter that can hold a door: a Thing, or an interface a door
    // is (ILogicable, IAirlockDevice). Cached per type.
    private static List<PropertyInfo> SettableOf(Type type)
    {
        if (Settable.TryGetValue(type, out List<PropertyInfo> cached))
        {
            return cached;
        }

        List<PropertyInfo> properties = new List<PropertyInfo>();
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0 &&
                property.GetSetMethod() != null && property.GetGetMethod() != null && CanHoldDoor(property.PropertyType))
            {
                properties.Add(property);
            }
        }

        Settable[type] = properties;
        return properties;
    }

    // Instance fields that can hold a door, of every class from the thing's own down to MonoBehaviour.
    private static List<FieldInfo> PointingOf(Type type)
    {
        if (Pointing.TryGetValue(type, out List<FieldInfo> cached))
        {
            return cached;
        }

        List<FieldInfo> fields = new List<FieldInfo>();
        for (Type? at = type; at != null && at != typeof(MonoBehaviour); at = at.BaseType)
        {
            foreach (FieldInfo field in at.GetFields(AllInstance | BindingFlags.DeclaredOnly))
            {
                if (CanHoldDoor(field.FieldType) && !typeof(IEnumerable).IsAssignableFrom(field.FieldType))
                {
                    fields.Add(field);
                }
            }
        }

        Pointing[type] = fields;
        return fields;
    }

    private static bool CanHoldDoor(Type type) =>
        type.IsAssignableFrom(typeof(Door)) && type != typeof(object) && type != typeof(MonoBehaviour) &&
        type != typeof(Component) && type != typeof(UnityEngine.Object);

    private static DoorHandoverView View(Thing thing, string how, string action, string? note) =>
        new DoorHandoverView(new ThingId(thing.ReferenceId), thing.PrefabName, how, action, note);
}
