#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects.Entities;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Protocol;
using UnityEngine;
using World = StationGodMCP.WorldModel.World;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// The one place that decides who "the player" is (Pure/PlayerRule): the local player (Human.LocalHuman, which is
/// InventoryManager.ParentHuman) when there is one, else on a dedicated server the one connected, living player in the
/// world; a call that names its player (as_player, CallContext.Player) gets that one. Distances are measured from that
/// player; the absent origin answers every question with "no distance" and
/// carries why there is no player, so no tool checks for one itself.
/// </summary>
internal abstract class PlayerOrigin
{
    private PlayerOrigin()
    {
    }

    /// <summary>The distance from the player, rounded to 0.1 m; null without a player.</summary>
    internal abstract double? DistanceTo(Vector3 position);

    /// <summary>The distance from the player unrounded, for filters; null without a player.</summary>
    internal abstract double? ExactDistanceTo(Vector3 position);

    internal abstract LocalPlayerView? View { get; }

    internal abstract bool IsPresent { get; }

    /// <summary>The player; null without one.</summary>
    internal abstract Human? Player { get; }

    /// <summary>Why there is no player, a whole sentence; null with one.</summary>
    internal abstract string? Absence { get; }

    private static readonly IReadOnlyList<PlayerCandidate<Human>> NoCandidates = new List<PlayerCandidate<Human>>();

    internal static PlayerOrigin Current()
    {
        string? named = CallContext.Current.Player;
        if (named != null)
        {
            return Named(named);
        }

        Human? local = Local();
        return PlayerRule.Choose(local, local != null ? NoCandidates : Candidates()) switch
        {
            PlayerChoice<Human>.Found found => new Present(found.Player),
            PlayerChoice<Human>.Missing missing => new Absent(missing.Reason),
            _ => throw new System.InvalidOperationException("Unknown player choice.")
        };
    }

    /// <summary>Every human in the world by name and reference id, for a player named by either (PlayerMatch).</summary>
    internal static List<NamedPlayer<Human>> NamedPlayers()
    {
        List<NamedPlayer<Human>> players = new List<NamedPlayer<Human>>();
        foreach (Human human in World.Humans)
        {
            if (human != null && !human.IsBeingDestroyed)
            {
                players.Add(new NamedPlayer<Human>(human, human.ReferenceId, human.DisplayName));
            }
        }

        return players;
    }

    // The human the call's as_player names, in play or not, found as move player finds one.
    private static PlayerOrigin Named(string wanted) => PlayerMatch.Find(NamedPlayers(), wanted) switch
    {
        PlayerFound<Human>.One one => new Present(one.Player.Body),
        PlayerFound<Human>.None none => new Absent("as_player: " + none.Reason),
        PlayerFound<Human>.Several several => new Absent("as_player: " + several.Reason),
        _ => throw new System.InvalidOperationException("Unknown player match.")
    };

    /// <summary>
    /// Every player whose screen show draws on: the one the call names or the rule picks, else, when the call names
    /// none and several are in play (a dedicated server), each of them; empty, with why (absence), when there is none.
    /// </summary>
    internal static List<Human> Audience(out string? absence)
    {
        PlayerOrigin one = Current();
        bool named = CallContext.Current.Player != null;
        IReadOnlyList<PlayerCandidate<Human>> humans = one.Player != null || named ? NoCandidates : Candidates();
        return PlayerRule.Audience(one.Player, one.Absence, named, humans, out absence);
    }

    /// <summary>
    /// The local player alone, for what only this machine has: the camera, the crosshair, the placement cursor. A
    /// remote player's camera and cursor stay on their own machine, so a dedicated server has none to read.
    /// </summary>
    internal static Human? Local()
    {
        Human human = Human.LocalHuman;
        return human != null ? human : null;
    }

    /// <summary>The player itself, for tools about the player; no_local_player, saying why, when there is none.</summary>
    internal static Human RequireHuman() => Current().Require();

    internal Human Require()
    {
        Human? player = Player;
        return player != null ? player : throw NoPlayer(Absence!);
    }

    /// <summary>Refuses a filter that needs a player when there is none.</summary>
    internal PlayerOrigin RequireIf(bool required)
    {
        if (required && !IsPresent)
        {
            throw NoPlayer(Absence!);
        }

        return this;
    }

    private static ApiException NoPlayer(string reason) => ApiErrors.Refused("no_local_player", reason);

    // Every human the world has, as the rule sees it; only read when there is no local player.
    private static List<PlayerCandidate<Human>> Candidates()
    {
        List<PlayerCandidate<Human>> candidates = new List<PlayerCandidate<Human>>();
        foreach (Human human in World.Humans)
        {
            if (human == null || human.IsBeingDestroyed)
            {
                continue;
            }

            candidates.Add(new PlayerCandidate<Human>(human, human.DisplayName, IsConnected(human), IsAlive(human)));
        }

        return candidates;
    }

    /// <summary>Its brain belongs to a client connected now (Brain.IsOnline).</summary>
    internal static bool IsConnected(Human human) => human.OrganBrain != null && human.OrganBrain.IsOnline;

    /// <summary>Not dead and not decaying.</summary>
    internal static bool IsAlive(Human human) => human.State != EntityState.Dead && human.State != EntityState.Decay;

    private sealed class Present : PlayerOrigin
    {
        private readonly Human _human;

        internal Present(Human human)
        {
            _human = human;
        }

        internal override double? DistanceTo(Vector3 position) =>
            System.Math.Round(Vector3.Distance(_human.Position, position), PositionView.Decimals);

        internal override double? ExactDistanceTo(Vector3 position) => Vector3.Distance(_human.Position, position);

        internal override LocalPlayerView? View =>
            new LocalPlayerView(
                new ThingId(_human.ReferenceId), _human.DisplayName, GameLookup.ViewOf(_human.Position));

        internal override bool IsPresent => true;

        internal override Human? Player => _human;

        internal override string? Absence => null;
    }

    private sealed class Absent : PlayerOrigin
    {
        private readonly string _reason;

        internal Absent(string reason)
        {
            _reason = reason;
        }

        internal override double? DistanceTo(Vector3 position) => null;

        internal override double? ExactDistanceTo(Vector3 position) => null;

        internal override LocalPlayerView? View => null;

        internal override bool IsPresent => false;

        internal override Human? Player => null;

        internal override string? Absence => _reason;
    }
}
