#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Objects.Rockets;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Net;
using StationGodMCP.Pure;
using StationGodMCP.Pure.RemoteView;
using TerrainSystem;
using UnityEngine;

namespace StationGodMCP.Api;

/// <summary>
/// move_player: put a player anywhere, a cheat. The console's teleport is creative only and moves the local player by
/// writing its transform (TeleportCommand), which on a dedicated server has no player to move and could not hold a
/// client's: a client's character is its own to move (see PlayerMoves). So the move is made by whichever game owns the
/// character (PlayerMover): this server for its own player or a body nobody controls, the player's own StationGod for a
/// remote player, and the game's seat exit for a player in a seat, bed or capsule, which reaches every client.
/// </summary>
internal static class MovePlayerApi
{
    internal static MovePlayerView Handle(Args args)
    {
        if (NetworkManager.IsClient || !GameManager.RunSimulation)
        {
            throw ApiErrors.Refused("not_host", "This game is a multiplayer client; only the host moves players.");
        }

        MoveRequest request = MoveRequest.Of(args);
        Human player = MovablePlayers.Require(request.Player);
        Vector3 from = player.ThingTransformPosition;
        Target target = Target.Resolve(request.Destination, player, from);
        Vector3 to = request.SafeGround ? SpawnPoint.GetSafePoint(target.Point) : target.Point;
        WorldBounds.Require(to);
        PlayerMover mover = PlayerMover.For(player);
        Vector3 after = request.DryRun ? to : mover.Move(player, to);
        return new MovePlayerView(request.DryRun, GameLookup.ViewOf(player), mover.Name, Places.Of(from),
            Places.Of(after), new MoveDestinationView(request.Destination.Form, target.NextTo, request.SafeGround),
            Vector3.Distance(from, to), mover.Note(request.DryRun));
    }

    /// <summary>The destination's point, and the thing or player it is next to.</summary>
    private sealed class Target
    {
        private Target(Vector3 point, ThingView? nextTo)
        {
            Point = point;
            NextTo = nextTo;
        }

        internal Vector3 Point { get; }

        internal ThingView? NextTo { get; }

        internal static Target Resolve(MoveDestination destination, Human player, Vector3 from) =>
            destination switch
            {
                MoveDestination.Point point => new Target(Bodies.U(point.At), null),
                MoveDestination.NextToThing thing => BesideThing(thing.Thing, player, from),
                MoveDestination.NextToPlayer other => BesidePlayer(MovablePlayers.Find(other.Player), player, from),
                _ => throw new System.InvalidOperationException("Unknown destination.")
            };

        private static Target BesideThing(ThingId id, Human player, Vector3 from)
        {
            Thing thing = GameLookup.RequireThing(id);
            Thing place = HolderChain.PlaceOf(thing);
            if (place == player)
            {
                throw ApiErrors.InvalidArgument(
                    $"to_id {id} is {player.DisplayName} or something they carry, so it moves with them.");
            }

            Box3 body = place is Human human
                ? Landing.PlayerAt(Bodies.V(human.ThingTransformPosition))
                : Bodies.RenderBox(place);
            return new Target(Bodies.U(Landing.Beside(body, Bodies.V(from))), GameLookup.ViewOf(thing));
        }

        private static Target BesidePlayer(Human other, Human player, Vector3 from)
        {
            if (other == player)
            {
                throw ApiErrors.InvalidArgument($"near_player is {player.DisplayName}, the player being moved.");
            }

            Box3 body = Landing.PlayerAt(Bodies.V(other.ThingTransformPosition));
            return new Target(Bodies.U(Landing.Beside(body, Bodies.V(from))), GameLookup.ViewOf(other));
        }
    }

    /// <summary>A point's position and the closed room its cell belongs to.</summary>
    private static class Places
    {
        internal static PlaceView Of(Vector3 point)
        {
            RoomController rooms = RoomController.World;
            Room? room = rooms != null ? rooms.GetRoom(new WorldGrid(point).Value) : null;
            return new PlaceView(GameLookup.ViewOf(point), room != null ? RoomsApi.IdOf(room) : null,
                room?.RoomType.ToString());
        }
    }
}

/// <summary>The players move_player finds by name or id, and the one it moves.</summary>
internal static class MovablePlayers
{
    /// <summary>The player named, else the player (PlayerOrigin); refused when they cannot be moved.</summary>
    internal static Human Require(string? wanted)
    {
        Human player = wanted == null ? PlayerOrigin.RequireHuman() : Find(wanted);
        if (!PlayerOrigin.IsAlive(player))
        {
            throw ApiErrors.Refused("player_not_movable", $"{player.DisplayName} is dead; a body is not moved.");
        }

        if (player.IsBeingDragged)
        {
            throw ApiErrors.Refused("player_not_movable",
                $"{player.DisplayName} is being dragged by another player; move them once they are let go.");
        }

        return player;
    }

    /// <summary>The human a name or reference id means (PlayerMatch).</summary>
    internal static Human Find(string wanted)
    {
        List<NamedPlayer<Human>> players = new List<NamedPlayer<Human>>();
        foreach (Human human in new List<Human>(Human.AllHumans))
        {
            if (human != null && !human.IsBeingDestroyed)
            {
                players.Add(new NamedPlayer<Human>(human, human.ReferenceId, human.DisplayName));
            }
        }

        return PlayerMatch.Find(players, wanted) switch
        {
            PlayerFound<Human>.One one => one.Player.Body,
            PlayerFound<Human>.None none => throw ApiErrors.Refused("player_not_found", none.Reason),
            PlayerFound<Human>.Several several => throw ApiErrors.Refused("ambiguous_player", several.Reason),
            _ => throw new System.InvalidOperationException("Unknown player match.")
        };
    }
}

/// <summary>
/// The world's edge as the console's teleport checks it (the terrain octree at that x and z), with the low-orbit area
/// above the planet inside too (Rocket.LowOrbitPlayableBounds, where 'loworbitstation goto' moves a player).
/// </summary>
internal static class WorldBounds
{
    internal static void Require(Vector3 point)
    {
        VoxelOctree octree = VoxelTerrain.Octree;
        if (octree == null || Rocket.LowOrbitPlayableBounds.Contains(point))
        {
            return;
        }

        Vector3Int column = VoxelTerrain.WorldToOctreeSpace(new Vector3Int((int)point.x, 1, (int)point.z));
        if (octree.OutsideBounds(column))
        {
            throw ApiErrors.Refused("outside_world",
                $"({point.x:0.#}, {point.z:0.#}) is outside the world's terrain, where nothing holds a player up.");
        }
    }
}

/// <summary>
/// Which game moves the player, by who owns the character. Each answers where the player stands once moved.
/// </summary>
internal abstract class PlayerMover
{
    private PlayerMover()
    {
    }

    /// <summary>server, client or seat_exit, as the reply names it.</summary>
    internal abstract string Name { get; }

    internal abstract Vector3 Move(Human player, Vector3 to);

    internal virtual string? Note(bool dryRun) => null;

    internal static PlayerMover For(Human player)
    {
        if (player.ParentSlot != null)
        {
            return new SeatExit();
        }

        if (player == Human.LocalHuman || !PlayerOrigin.IsConnected(player))
        {
            return new ThisServer();
        }

        (ReceivedView View, long ConnectionId)? held = RemoteViews.Find(player);
        if (held == null)
        {
            throw ApiErrors.Refused("client_cannot_move",
                $"{player.DisplayName}'s game owns their movement, and this server cannot reach it: " +
                PlayerView.NoRemoteView(player, player.DisplayName) + " Nothing was moved.");
        }

        return new OwningClient(held.Value.ConnectionId);
    }

    /// <summary>
    /// This game's own player, or a body no connected game controls: Human.ForceSetPosition, the game's own way to put
    /// a player somewhere (TutorialSpawnPoint). Nothing pulls it back: the server follows a body's sent position only
    /// while its brain is online and not its own (DynamicThing.UpdateNetworkPosition).
    /// </summary>
    private sealed class ThisServer : PlayerMover
    {
        internal override string Name => "server";

        internal override Vector3 Move(Human player, Vector3 to)
        {
            player.ForceSetPosition(to);
            return player.ThingTransformPosition;
        }
    }

    /// <summary>
    /// A player in a seat, bed, cryo tube or capsule slot: the game's seat exit (OnServer.MoveToWorld, as
    /// AtmosphericSeat.Exit and the lander capsule's door do) puts them at the point, upright. The server empties the
    /// slot, which sends the thing's transform to every client, and a client takes the point for its own player as it
    /// leaves the slot (DynamicThing.ProcessUpdateTransform into MoveToWorld), so this reaches a remote player too.
    /// </summary>
    private sealed class SeatExit : PlayerMover
    {
        internal override string Name => "seat_exit";

        internal override Vector3 Move(Human player, Vector3 to)
        {
            float yaw = player.ParentSlot.Parent.ThingTransformRotation.eulerAngles.y;
            OnServer.MoveToWorld(player, to, Quaternion.Euler(0f, yaw, 0f), Vector3.zero, Vector3.zero);
            return player.ThingTransformPosition;
        }

        internal override string? Note(bool dryRun) =>
            "The player is in a seat, bed or capsule: the move takes them out of it.";
    }

    /// <summary>
    /// A player whose game is another machine: their StationGod moves them (PlayerMoves), and this server's copy is put
    /// there too so every reader sees the new place at once. The client's next position update confirms it.
    /// </summary>
    private sealed class OwningClient : PlayerMover
    {
        private readonly long _connectionId;

        internal OwningClient(long connectionId)
        {
            _connectionId = connectionId;
        }

        internal override string Name => "client";

        internal override Vector3 Move(Human player, Vector3 to)
        {
            if (!PlayerMoves.Send(_connectionId, player, to))
            {
                throw ApiErrors.Refused("client_cannot_move",
                    $"{player.DisplayName}'s game could not be reached (they left, or StationGod's networking is " +
                    "off). Nothing was moved.");
            }

            player.ForceSetPosition(to);
            return to;
        }

        internal override string? Note(bool dryRun) =>
            dryRun
                ? "A real move goes to the player's own game, which moves them on receipt."
                : "Sent to the player's own game, which moves them on receipt; a dry run with the same player reads " +
                  "back where they stand.";
    }
}
