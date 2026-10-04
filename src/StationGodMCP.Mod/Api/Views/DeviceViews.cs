#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Views;

/// <summary>A device (or worn logic item) as every device tool lists it.</summary>
internal sealed class DeviceView
{
    internal DeviceView(ThingView thing, int prefabHash, string? runtimeType, int slotCount, DeviceWear wear)
    {
        ReferenceId = thing.ReferenceId;
        PrefabName = thing.PrefabName;
        PrefabHash = prefabHash;
        DisplayName = thing.DisplayName;
        RuntimeType = runtimeType;
        SlotCount = slotCount;
        IsGateway = wear.IsGateway;
        WornBy = wear.WornBy;
        WornSlot = wear.WornSlot;
    }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public int PrefabHash { get; }

    public string? DisplayName { get; }

    public string? RuntimeType { get; }

    public int SlotCount { get; }

    /// <summary>Whether this device is the scope's own StationGod Gateway.</summary>
    public bool IsGateway { get; }

    /// <summary>The player wearing or holding it; null for a device.</summary>
    public string? WornBy { get; }

    /// <summary>suit, helmet, backpack, toolbelt, glasses, uniform, left_hand or right_hand; null otherwise.</summary>
    public string? WornSlot { get; }
}

/// <summary>Whether a target is the gateway, and who wears it where.</summary>
internal sealed class DeviceWear
{
    internal DeviceWear(bool isGateway, string? wornBy, string? wornSlot)
    {
        IsGateway = isGateway;
        WornBy = wornBy;
        WornSlot = wornSlot;
    }

    internal bool IsGateway { get; }

    internal string? WornBy { get; }

    internal string? WornSlot { get; }
}

/// <summary>A logic type by number and enum name (null for a mod's type with no name).</summary>
internal sealed class LogicTypeView
{
    internal LogicTypeView(ushort id, string? name)
    {
        Id = id;
        Name = name;
    }

    public ushort Id { get; }

    public string? Name { get; }
}

/// <summary>list_gateways: the scopes gateway_id accepts, "world" first.</summary>
internal sealed class GatewaysView
{
    internal GatewaysView(List<GatewayView> gateways)
    {
        Gateways = gateways;
        Count = gateways.Count;
    }

    public List<GatewayView> Gateways { get; }

    public int Count { get; }

    /// <summary>Always true: "world" reaches every device, so no gateway is needed.</summary>
    public bool BypassGateway => true;
}

internal sealed class GatewayView
{
    internal GatewayView(string gatewayId, string? displayName, string? prefabName, GatewayState state,
        GatewayCounts counts)
    {
        GatewayId = gatewayId;
        DisplayName = ThingName.Displayed(displayName, prefabName);
        PrefabName = prefabName;
        Available = state.Available;
        Status = state.Status;
        DataNetworkCount = counts.DataNetworks;
        VisibleDeviceCount = counts.VisibleDevices;
        WornItemCount = counts.WornItems;
    }

    /// <summary>The gateway's reference id, or "world".</summary>
    public string GatewayId { get; }

    public string? DisplayName { get; }

    public string? PrefabName { get; }

    public bool Available { get; }

    /// <summary>bypass (the world), ready, incomplete or no_data_network.</summary>
    public string Status { get; }

    public int DataNetworkCount { get; }

    public int VisibleDeviceCount { get; }

    public int WornItemCount { get; }
}

internal sealed class GatewayState
{
    internal GatewayState(bool available, string status)
    {
        Available = available;
        Status = status;
    }

    internal bool Available { get; }

    internal string Status { get; }
}

internal sealed class GatewayCounts
{
    internal GatewayCounts(int dataNetworks, int visibleDevices, int wornItems)
    {
        DataNetworks = dataNetworks;
        VisibleDevices = visibleDevices;
        WornItems = wornItems;
    }

    internal int DataNetworks { get; }

    internal int VisibleDevices { get; }

    internal int WornItems { get; }
}

/// <summary>list_devices: every device in the scope that matches, by reference id.</summary>
internal sealed class DevicesView
{
    internal DevicesView(string gatewayId, List<DeviceView> devices)
    {
        GatewayId = gatewayId;
        Devices = devices;
        Count = devices.Count;
    }

    public string GatewayId { get; }

    public List<DeviceView> Devices { get; }

    public int Count { get; }
}

/// <summary>describe_device: a device and every logic type it reads or writes.</summary>
internal sealed class DescribeDeviceView
{
    internal DescribeDeviceView(DeviceView device, List<LogicAccessView> logicTypes, RocketPartView? rocket = null,
        UmbilicalView? umbilical = null, UplinkView? uplink = null, BuildStateView? buildState = null)
    {
        BuildState = buildState;
        Device = device;
        LogicTypes = logicTypes;
        LogicTypeCount = logicTypes.Count;
        Rocket = rocket;
        Umbilical = umbilical;
        Uplink = uplink;
    }

    public DeviceView Device { get; }

    /// <summary>Its build state, current of last, and what the next state takes; absent with a single state.</summary>
    [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public BuildStateView? BuildState { get; }

    /// <summary>The rocket it is part of; absent when none.</summary>
    [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public RocketPartView? Rocket { get; }

    /// <summary>Its pairing when it is an umbilical; absent otherwise.</summary>
    [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public UmbilicalView? Umbilical { get; }

    /// <summary>A Logic Rocket Uplink's downlink and the ones it may follow; absent for any other device.</summary>
    [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public UplinkView? Uplink { get; }

    public List<LogicAccessView> LogicTypes { get; }

    public int LogicTypeCount { get; }
}

internal sealed class LogicAccessView
{
    internal LogicAccessView(LogicTypeView type, bool readable, bool writable)
    {
        Id = type.Id;
        Name = type.Name;
        Readable = readable;
        Writable = writable;
    }

    public ushort Id { get; }

    public string? Name { get; }

    public bool Readable { get; }

    public bool Writable { get; }
}

/// <summary>inspect_slots: a device's logical slots, what is in each, and each slot's readable logic.</summary>
internal sealed class InspectSlotsView
{
    internal InspectSlotsView(string gatewayId, DeviceView device, List<SlotDetailView> slots, int totalSlots)
    {
        GatewayId = gatewayId;
        ReferenceId = device.ReferenceId;
        Device = device;
        Slots = slots;
        Count = slots.Count;
        TotalSlots = totalSlots;
    }

    public string GatewayId { get; }

    public ThingId ReferenceId { get; }

    public DeviceView Device { get; }

    public List<SlotDetailView> Slots { get; }

    public int Count { get; }

    public int TotalSlots { get; }
}

internal sealed class SlotDetailView
{
    internal SlotDetailView(int index, SlotFacts? facts, SlotOccupantView? occupant, List<SlotLogicView> logicValues)
    {
        Index = index;
        UnderlyingSlotIndex = facts?.UnderlyingIndex;
        DisplayName = facts?.DisplayName;
        StringKey = facts?.StringKey;
        SlotClass = facts?.SlotClass;
        Empty = occupant == null;
        Interactable = facts?.Interactable ?? false;
        Locked = facts?.Locked ?? false;
        Swappable = facts?.Swappable ?? false;
        HidesOccupant = facts?.HidesOccupant ?? false;
        SpecificPrefabHashes = facts?.SpecificPrefabHashes;
        Occupant = occupant;
        LogicValues = logicValues;
        LogicValueCount = logicValues.Count;
    }

    /// <summary>The logical index the device's logic uses.</summary>
    public int Index { get; }

    public int? UnderlyingSlotIndex { get; }

    public string? DisplayName { get; }

    public string? StringKey { get; }

    public EnumValueView? SlotClass { get; }

    public bool Empty { get; }

    public bool Interactable { get; }

    public bool Locked { get; }

    public bool Swappable { get; }

    public bool HidesOccupant { get; }

    public int[]? SpecificPrefabHashes { get; }

    public SlotOccupantView? Occupant { get; }

    public List<SlotLogicView> LogicValues { get; }

    public int LogicValueCount { get; }
}

/// <summary>A slot's own fields (Slot), when the device has a slot at that logical index.</summary>
internal sealed class SlotFacts
{
    internal SlotFacts(int underlyingIndex, string? displayName, string? stringKey, EnumValueView slotClass,
        SlotFlags flags)
    {
        UnderlyingIndex = underlyingIndex;
        DisplayName = ThingName.Displayed(displayName, null);
        StringKey = stringKey;
        SlotClass = slotClass;
        Interactable = flags.Interactable;
        Locked = flags.Locked;
        Swappable = flags.Swappable;
        HidesOccupant = flags.HidesOccupant;
        SpecificPrefabHashes = flags.SpecificPrefabHashes;
    }

    internal int UnderlyingIndex { get; }

    internal string? DisplayName { get; }

    internal string? StringKey { get; }

    internal EnumValueView SlotClass { get; }

    internal bool Interactable { get; }

    internal bool Locked { get; }

    internal bool Swappable { get; }

    internal bool HidesOccupant { get; }

    internal int[]? SpecificPrefabHashes { get; }
}

internal sealed class SlotFlags
{
    internal SlotFlags(bool interactable, bool locked, bool swappable, bool hidesOccupant, int[]? specificPrefabHashes)
    {
        Interactable = interactable;
        Locked = locked;
        Swappable = swappable;
        HidesOccupant = hidesOccupant;
        SpecificPrefabHashes = specificPrefabHashes;
    }

    internal bool Interactable { get; }

    internal bool Locked { get; }

    internal bool Swappable { get; }

    internal bool HidesOccupant { get; }

    internal int[]? SpecificPrefabHashes { get; }
}

/// <summary>An enum value by number and name.</summary>
internal sealed class EnumValueView
{
    internal EnumValueView(int id, string? name)
    {
        Id = id;
        Name = name;
    }

    public int Id { get; }

    public string? Name { get; }
}

internal sealed class SlotOccupantView
{
    internal SlotOccupantView(ThingView thing, int prefabHash, string? runtimeType)
    {
        ReferenceId = thing.ReferenceId;
        PrefabName = thing.PrefabName;
        PrefabHash = prefabHash;
        DisplayName = thing.DisplayName;
        RuntimeType = runtimeType;
    }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public int PrefabHash { get; }

    public string? DisplayName { get; }

    public string? RuntimeType { get; }
}

internal sealed class SlotLogicView
{
    internal SlotLogicView(LogicTypeView logicSlotType, double value)
    {
        LogicSlotType = logicSlotType;
        Value = value;
    }

    /// <summary>A LogicSlotType by number and name.</summary>
    public LogicTypeView LogicSlotType { get; }

    public double Value { get; }
}

/// <summary>network_snapshot: the readable logic of every matching device in the scope, up to max_devices.</summary>
internal sealed class NetworkSnapshotView
{
    internal NetworkSnapshotView(string gatewayId, List<DeviceSnapshotView> devices, int matchedCount)
    {
        GatewayId = gatewayId;
        Devices = devices;
        Count = devices.Count;
        MatchedCount = matchedCount;
        Truncated = matchedCount > devices.Count;
    }

    public string GatewayId { get; }

    public List<DeviceSnapshotView> Devices { get; }

    public int Count { get; }

    public int MatchedCount { get; }

    public bool Truncated { get; }
}

internal sealed class DeviceSnapshotView
{
    internal DeviceSnapshotView(DeviceView device, List<object> logicValues)
    {
        Device = device;
        LogicValues = logicValues;
        LogicValueCount = logicValues.Count;
    }

    public DeviceView Device { get; }

    /// <summary>A LogicValueView or LogicValueErrorView per logic type.</summary>
    public List<object> LogicValues { get; }

    public int LogicValueCount { get; }
}

internal sealed class LogicValueView
{
    internal LogicValueView(LogicTypeView logicType, double value)
    {
        LogicType = logicType;
        Value = value;
    }

    public LogicTypeView LogicType { get; }

    public bool Ok => true;

    public double Value { get; }
}

internal sealed class LogicValueErrorView
{
    internal LogicValueErrorView(LogicTypeView logicType, ErrorView error)
    {
        LogicType = logicType;
        Error = error;
    }

    public LogicTypeView LogicType { get; }

    public bool Ok => false;

    public ErrorView Error { get; }
}
