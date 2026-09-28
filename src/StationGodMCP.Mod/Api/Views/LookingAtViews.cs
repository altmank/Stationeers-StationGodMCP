#nullable enable

using Newtonsoft.Json;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Api.Views;

/// <summary>looking_at: the local player, the thing under their crosshair and the interactable on it.</summary>
internal sealed class LookingAtView
{
    internal LookingAtView(LocalPlayerView? player, LookingAtTargetView? target,
        LookingAtInteractableView? interactable)
    {
        Player = player;
        Target = target;
        Interactable = interactable;
    }

    public LocalPlayerView? Player { get; }

    public LookingAtTargetView? Target { get; }

    public LookingAtInteractableView? Interactable { get; }
}

/// <summary>The thing under the crosshair, with what the other tools can do with it.</summary>
internal sealed class LookingAtTargetView
{
    internal LookingAtTargetView(ThingView thing, string? customName, string kind, string runtimeType,
        PositionView position, double? distanceM, bool isDevice, bool hasAtmosphere, HeldInView? parent,
        OrientationView? rotation = null)
    {
        Rotation = rotation;
        ReferenceId = thing.ReferenceId;
        PrefabName = thing.PrefabName;
        DisplayName = thing.DisplayName;
        CustomName = customName;
        Kind = kind;
        RuntimeType = runtimeType;
        Position = position;
        DistanceM = distanceM;
        IsDevice = isDevice;
        HasAtmosphere = hasAtmosphere;
        Parent = parent;
    }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public string? DisplayName { get; }

    /// <summary>The labeller name, null when it has none.</summary>
    public string? CustomName { get; }

    /// <summary>item, dynamic, structure, entity or other (ThingKinds).</summary>
    public string Kind { get; }

    public string RuntimeType { get; }

    public PositionView Position { get; }

    public double? DistanceM { get; }

    /// <summary>A logic device the device tools take (describe_device, read_logic).</summary>
    public bool IsDevice { get; }

    /// <summary>atmosphere_contents has something to report for it.</summary>
    public bool HasAtmosphere { get; }

    /// <summary>For a thing in a slot: its holder and the slot.</summary>
    public HeldInView? Parent { get; }

    /// <summary>
    /// How it stands turned: facing (its front), up and Euler degrees, the forms place_structure takes, so it can be
    /// placed again as it stands or turned (facing reversed: 180 degrees). Structures only; left out otherwise.
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public OrientationView? Rotation { get; }
}

/// <summary>The button, switch, port or slot under the crosshair.</summary>
internal sealed class LookingAtInteractableView
{
    internal LookingAtInteractableView(string action, string? displayName, string? contextualName, int state,
        InteractableSlotView? slot)
    {
        Action = action;
        DisplayName = displayName;
        ContextualName = contextualName;
        State = state;
        Slot = slot;
    }

    /// <summary>The game's InteractableType name (Activate, OnOff, Open, Slot1...).</summary>
    public string Action { get; }

    public string? DisplayName { get; }

    /// <summary>The name the game shows for it on this thing, as the tooltip reads it.</summary>
    public string? ContextualName { get; }

    public int State { get; }

    /// <summary>For a slot: its index, name and what is in it.</summary>
    public InteractableSlotView? Slot { get; }
}

/// <summary>A slot under the crosshair and its occupant.</summary>
internal sealed class InteractableSlotView
{
    internal InteractableSlotView(int slotIndex, string? slotName, ThingView? occupant)
    {
        SlotIndex = slotIndex;
        SlotName = slotName;
        Occupant = occupant;
    }

    public int SlotIndex { get; }

    public string? SlotName { get; }

    public ThingView? Occupant { get; }
}
