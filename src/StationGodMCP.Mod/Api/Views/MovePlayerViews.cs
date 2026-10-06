#nullable enable

using System;
using Newtonsoft.Json;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Api.Views;

/// <summary>
/// move_player's reply: the player, how the move is made, where they stood and where they go (each with its room),
/// the destination as asked, and how far apart the two are.
/// </summary>
internal sealed class MovePlayerView
{
    private const int DistanceDecimals = 1;

    internal MovePlayerView(bool dryRun, ThingView player, string movedBy, PlaceView before, PlaceView after,
        MoveDestinationView destination, double distanceM, string? note)
    {
        DryRun = dryRun;
        Player = player;
        MovedBy = movedBy;
        Before = before;
        After = after;
        Destination = destination;
        DistanceM = Math.Round(distanceM, DistanceDecimals);
        Note = note;
    }

    public bool DryRun { get; }

    public ThingView Player { get; }

    /// <summary>server, client or seat_exit: which game moves the player (see the tool's help).</summary>
    public string MovedBy { get; }

    public PlaceView Before { get; }

    /// <summary>Where the player lands: read back after a move this server makes, else the point the move sends.</summary>
    public PlaceView After { get; }

    public MoveDestinationView Destination { get; }

    public double DistanceM { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Note { get; }
}

/// <summary>A point with the room its cell belongs to; outside: true and no room when it is in no closed room.</summary>
internal sealed class PlaceView
{
    internal PlaceView(PositionView position, string? roomId, string? roomType)
    {
        Position = position;
        RoomId = roomId;
        RoomType = roomType;
        Outside = roomId == null ? true : null;
    }

    public PositionView Position { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? RoomId { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? RoomType { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public bool? Outside { get; }
}

/// <summary>The destination as asked: its form, the thing or player it is next to, and safe_ground.</summary>
internal sealed class MoveDestinationView
{
    internal MoveDestinationView(string form, ThingView? nextTo, bool safeGround)
    {
        Form = form;
        NextTo = nextTo;
        SafeGround = safeGround ? true : null;
    }

    public string Form { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ThingView? NextTo { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public bool? SafeGround { get; }
}
