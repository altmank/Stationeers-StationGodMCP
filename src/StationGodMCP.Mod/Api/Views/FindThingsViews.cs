#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Api.Views;

/// <summary>find_things: one page of the things whose names match, nearest first.</summary>
internal sealed class FindThingsView
{
    internal FindThingsView(Slice<FoundThingView> page, int scanned, LocalPlayerView? localPlayer)
    {
        Things = page.Items;
        Count = page.Items.Count;
        Total = page.Total;
        Offset = page.Offset;
        Limit = page.Limit;
        HasMore = page.HasMore;
        Scanned = scanned;
        LocalPlayer = localPlayer;
    }

    public List<FoundThingView> Things { get; }

    public int Count { get; }

    public int Total { get; }

    public int Offset { get; }

    public int Limit { get; }

    public bool HasMore { get; }

    /// <summary>Every thing registered in the world when the call ran, matching or not.</summary>
    public int Scanned { get; }

    public LocalPlayerView? LocalPlayer { get; }
}

/// <summary>A thing find_things found: what it is, its names, where it is, what other tools can do with it.</summary>
internal sealed class FoundThingView
{
    /// <summary>Location of a structure: built in place, in no slot.</summary>
    internal const string Built = "built";

    /// <summary>Location of a thing that is neither a structure nor a dynamic thing.</summary>
    internal const string World = "world";

    internal FoundThingView(ThingView thing, string? customName, string gameName, string kind, string runtimeType,
        bool labelable, string location, string? carriedBy, List<HeldInView> heldIn, PositionView position,
        double? distanceM, bool isDevice, bool hasAtmosphere, OrientationView? rotation = null, bool isBroken = false,
        string condition = "intact", PrintView? made = null)
    {
        Made = made;
        IsBroken = isBroken;
        Condition = condition;
        Rotation = rotation;
        ReferenceId = thing.ReferenceId;
        PrefabName = thing.PrefabName;
        DisplayName = thing.DisplayName;
        CustomName = customName;
        GameName = gameName;
        Kind = kind;
        RuntimeType = runtimeType;
        Labelable = labelable;
        Location = location;
        CarriedBy = carriedBy;
        HeldIn = heldIn;
        Position = position;
        DistanceM = distanceM;
        IsDevice = isDevice;
        HasAtmosphere = hasAtmosphere;
    }

    /// <summary>Where it was printed, when the print log has it (1.4.3+); left out otherwise.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public PrintView? Made { get; }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    /// <summary>The name the game shows: the label when there is one.</summary>
    public string? DisplayName { get; }

    /// <summary>The Labeller's name, null when it has none.</summary>
    public string? CustomName { get; }

    /// <summary>The game's name for the prefab, whatever the label.</summary>
    public string GameName { get; }

    /// <summary>item, dynamic, structure, entity or other (ThingKinds).</summary>
    public string Kind { get; }

    public string RuntimeType { get; }

    /// <summary>The Labeller can rename this class of thing (Labels).</summary>
    public bool Labelable { get; }

    /// <summary>ground, player or stored for a dynamic thing (as find_items), built (structure), else world.</summary>
    public string Location { get; }

    public string? CarriedBy { get; }

    /// <summary>For a thing in a slot: its slot first, then outwards; empty otherwise.</summary>
    public List<HeldInView> HeldIn { get; }

    /// <summary>The outermost holder's position (the thing's own when in no slot).</summary>
    public PositionView Position { get; }

    public double? DistanceM { get; }

    /// <summary>A logic device the device tools take (describe_device, read_logic).</summary>
    public bool IsDevice { get; }

    /// <summary>atmosphere_contents has something to report for it.</summary>
    public bool HasAtmosphere { get; }

    /// <summary>
    /// The game's broken state (Thing.IsBroken; a structure also below build state 0), a burst pipe (Pipe.IsBurst) or
    /// a burnt cable (CableRuptured).
    /// Neither shows in the damage numbers, so this, not thing_health's numbers, says it is wrecked; remove_structure
    /// takes a broken structure with allow_broken.
    /// </summary>
    public bool IsBroken { get; }

    /// <summary>broken, damaged, intact, indestructible or none (no damage state); thing_health has the numbers.</summary>
    public string Condition { get; }

    /// <summary>
    /// How it stands turned: facing (its front), up and Euler degrees, the forms place_structure takes, so it can be
    /// placed again as it stands or turned (facing reversed: 180 degrees). Structures only; left out otherwise.
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public OrientationView? Rotation { get; }
}
