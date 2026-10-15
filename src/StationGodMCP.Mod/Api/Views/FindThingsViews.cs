#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Api.Views;

/// <summary>
/// find thing's location filter: any (default), or one of the locations a thing reports: ground, player and stored as
/// find item has them (a dynamic thing in no slot, carried by a player at any depth, in any other slot), built (a
/// structure) and world (neither a structure nor a dynamic thing: players, animals).
/// </summary>
internal static class ThingLocations
{
    internal const string Any = "any";

    internal static readonly string[] All = { "ground", "player", "stored", FoundThingView.Built, FoundThingView.World };
}

/// <summary>
/// find thing's reply (catalogue find.json): one page of the things found (ListReply: things, count, total, offset,
/// limit, has_more), scanned (every thing the call looked at; 0 on a walk's later page, which scans nothing) and
/// local_player, then cursor_id while a walk has more pages (a wire key the hub and sg read, never shown to an agent).
/// </summary>
internal static class FoundThings
{
    internal const string List = "things";

    internal static ReplyObject Reply(Page<FoundThingView> page, int scanned, LocalPlayerView? localPlayer, string? cursorId)
    {
        ReplyObject rest = new ReplyObject().Add("scanned", scanned).Add("local_player", localPlayer);
        if (cursorId != null)
        {
            rest.Add("cursor_id", cursorId);
        }

        return ListReply.OfPage(List, page, rest);
    }
}

/// <summary>A thing find thing found: what it is, its names, where it is, what other verbs can do with it.</summary>
internal sealed class FoundThingView
{
    /// <summary>Location of a structure: built in place, in no slot.</summary>
    internal const string Built = "built";

    /// <summary>Location of a thing that is neither a structure nor a dynamic thing.</summary>
    internal const string World = "world";

    internal FoundThingView(ThingView thing, string? customName, string gameName, string kind, string runtimeType,
        bool labelable, string location, string? carriedBy, List<HeldInView> heldIn, PositionView position,
        double? distanceM, bool isDevice, bool hasAtmosphere, OrientationView? rotation = null, bool isBroken = false,
        string condition = "intact", PrintView? made = null, string? rocketState = null,
        ThingColorView? color = null, BuildStateView? buildState = null)
    {
        BuildState = buildState;
        Color = color;
        Made = made;
        RocketState = rocketState;
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

    /// <summary>ground, player or stored for a dynamic thing (as find item), built (structure), else world.</summary>
    public string Location { get; }

    public string? CarriedBy { get; }

    /// <summary>For a thing in a slot: its slot first, then outwards; empty otherwise.</summary>
    public List<HeldInView> HeldIn { get; }

    /// <summary>The outermost holder's position (the thing's own when in no slot).</summary>
    public PositionView Position { get; }

    public double? DistanceM { get; }

    /// <summary>A logic device the device verbs take (inspect device, read logic).</summary>
    public bool IsDevice { get; }

    /// <summary>inspect atmosphere has something to report for it.</summary>
    public bool HasAtmosphere { get; }

    /// <summary>
    /// The game's broken state (Thing.IsBroken; a structure also below build state 0), a burst pipe (Pipe.IsBurst) or
    /// a burnt cable (CableRuptured).
    /// Neither shows in the damage numbers, so this, not inspect health's numbers, says it is wrecked.
    /// </summary>
    public bool IsBroken { get; }

    /// <summary>broken, damaged, intact, indestructible or none (no damage state); inspect health has the numbers.</summary>
    public string Condition { get; }

    /// <summary>
    /// How it stands turned: facing (its front), up and Euler degrees, the forms build structure takes, so it can be
    /// placed again as it stands or turned (facing reversed: 180 degrees). Structures only; left out otherwise.
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public OrientationView? Rotation { get; }

    /// <summary>
    /// The state of the rocket a structure is part of (OnLaunchMount, Launching, Landing...), so a position taken while
    /// it flies is read as such; left out for anything not part of a rocket.
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? RocketState { get; }

    /// <summary>
    /// Its colour {index, name, is_default} as write paint reads it: a paintable thing's paint, a state-coloured thing's
    /// state colour; left out for a thing with neither.
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ThingColorView? Color { get; }

    /// <summary>A structure not yet built to its last state: its build state, current of last.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public BuildStateView? BuildState { get; }
}
