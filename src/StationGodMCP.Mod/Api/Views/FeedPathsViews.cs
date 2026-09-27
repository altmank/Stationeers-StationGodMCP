#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Api.Views;

/// <summary>
/// feed_paths: from a root device, each device on the network with the rooms its feed crosses (daisy chains flagged),
/// each room with how many places its devices' feeds enter it, and devices no path reaches.
/// </summary>
internal sealed class FeedPathsView
{
    internal FeedPathsView(ThingView root, ThingId networkId, string kind, string? rootRoomId,
        List<FeedDeviceView> devices, List<FeedRoomView> rooms, List<ThingView> unreached)
    {
        Root = root;
        NetworkId = networkId;
        Kind = kind;
        RootRoomId = rootRoomId;
        Devices = devices;
        Rooms = rooms;
        Unreached = unreached;
        DaisyChains = devices.FindAll(static device => device.FedThroughOtherRooms).Count;
        MultipleFeeds = rooms.FindAll(static room => room.MultipleFeeds).Count;
    }

    public ThingView Root { get; }

    public ThingId NetworkId { get; }

    /// <summary>cable, pipe or chute.</summary>
    public string Kind { get; }

    /// <summary>The root's room; null outside.</summary>
    public string? RootRoomId { get; }

    /// <summary>Devices fed through a room that is neither the root's nor their own.</summary>
    public int DaisyChains { get; }

    /// <summary>Rooms whose devices' feeds enter at more than one piece.</summary>
    public int MultipleFeeds { get; }

    /// <summary>Every device the network reaches from the root, nearest first.</summary>
    public List<FeedDeviceView> Devices { get; }

    public List<FeedRoomView> Rooms { get; }

    /// <summary>Devices on the network that no run of pieces from the root reaches.</summary>
    public List<ThingView> Unreached { get; }
}

/// <summary>
/// One device: its room, the pieces between it and the root, the rooms those pieces pass through in order, and those
/// that are neither the root's nor its own (through: a daisy chain when not empty).
/// </summary>
internal sealed class FeedDeviceView
{
    internal FeedDeviceView(ThingView device, string? roomId, int pieces, List<string> rooms, List<string> through)
    {
        ReferenceId = device.ReferenceId;
        PrefabName = device.PrefabName;
        DisplayName = device.DisplayName;
        RoomId = roomId;
        Pieces = pieces;
        Rooms = rooms;
        Through = through;
    }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public string? DisplayName { get; }

    /// <summary>Null outside any room.</summary>
    public string? RoomId { get; }

    public int Pieces { get; }

    public List<string> Rooms { get; }

    public List<string> Through { get; }

    public bool FedThroughOtherRooms => Through.Count > 0;
}

/// <summary>A room: how many of its devices the network feeds and the pieces where those feeds enter it.</summary>
internal sealed class FeedRoomView
{
    internal FeedRoomView(string roomId, int devices, List<ThingId> entries, List<PositionView> entryAt)
    {
        RoomId = roomId;
        Devices = devices;
        Entries = entries;
        EntryAt = entryAt;
    }

    public string RoomId { get; }

    public int Devices { get; }

    public List<ThingId> Entries { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<PositionView> EntryAt { get; }

    public bool MultipleFeeds => Entries.Count > 1;
}
