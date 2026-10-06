#nullable enable

using Assets.Scripts.Objects.Entities;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Pure;
using StationGodMCP.Pure.RemoteView;
using UnityEngine;

namespace StationGodMCP.Net;

/// <summary>
/// Moving a player whose game is another machine. A client owns its character's movement: its brain has the
/// authority (Brain.HasAuthority = LocalControl), the client ignores the server's position updates for it
/// (DynamicThing.ProcessPhysicsUpdate and ProcessUpdateTransform both skip a thing it has authority over), and the
/// server's copy follows what the client sends (DynamicThing.UpdateNetworkPosition). So the server tells the player's
/// own StationGod to move them, and the game's sync carries the new position back.
/// </summary>
internal static class PlayerMoves
{
    /// <summary>The server's side: sends the order on the connection the player's views come from.</summary>
    internal static bool Send(long connectionId, Human human, Vector3 to) =>
        StationGodNet.SendMove(connectionId, MoveWire.Encode(new MoveCommand(human.ReferenceId, Bodies.V(to))));

    /// <summary>A client's side: moves this game's player when the order is for them.</summary>
    internal static void Receive(byte[] payload)
    {
        switch (MoveWire.Decode(payload))
        {
            case WireRead<MoveCommand>.Read read:
                Move(read.Value);
                return;
            case WireRead<MoveCommand>.OtherProtocol other:
                OnceLog.Warning("move_protocol", $"The server's StationGod moves with view protocol {other.Protocol}; " +
                                                 $"this game's StationGod {StationGodMod.Version} speaks " +
                                                 $"{ViewProtocol.Current}, so the move is not made.");
                return;
            case WireRead<MoveCommand>.Malformed malformed:
                OnceLog.Warning("move_malformed", $"A move from the server did not read ({malformed.Reason}).");
                return;
        }
    }

    private static void Move(MoveCommand command)
    {
        Human human = Human.LocalHuman;
        if (human == null || human.ReferenceId != command.HumanId || human.IsBeingDestroyed)
        {
            StationGodMod.LogWarning($"The server moved player {command.HumanId}, who is not this game's player.");
            return;
        }

        Vector3 to = Bodies.U(command.To);
        Place(human, to);
        StationGodMod.Log($"The server moved {human.DisplayName} to {command.To}.");
    }

    /// <summary>
    /// Puts the player at a point as Human.ForceSetPosition does, which acts only where the simulation runs: the
    /// position, the transform, the rigidbody, its velocity stopped, and the interpolation reset so nothing pulls the
    /// body back. The client then sends the new position as its own movement.
    /// </summary>
    internal static void Place(Human human, Vector3 to)
    {
        human.Position = to;
        human.ThingTransformPosition = to;
        human.Transform.position = to;
        Rigidbody body = human.ActiveRigidbody;
        if (body != null)
        {
            body.MovePosition(to);
            if (!body.isKinematic)
            {
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
        }

        human.ResetInterpolation();
    }
}
