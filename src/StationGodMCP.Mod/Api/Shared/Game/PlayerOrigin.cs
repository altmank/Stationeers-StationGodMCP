#nullable enable

using Assets.Scripts.Objects.Entities;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// Where distances are measured from: the local player (Human.LocalHuman), or nowhere on a dedicated server. The
/// absent origin answers every question with "no distance" instead of making each tool check for a player.
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

    internal static PlayerOrigin Current()
    {
        Human human = Human.LocalHuman;
        return human != null ? new Present(human) : Absent.Instance;
    }

    /// <summary>The local player itself, for tools about the player; refused when there is none.</summary>
    internal static Human RequireHuman()
    {
        Human human = Human.LocalHuman;
        if (human == null)
        {
            throw NoLocalPlayer();
        }

        return human;
    }

    /// <summary>Refuses a filter that needs a player when there is none.</summary>
    internal PlayerOrigin RequireIf(bool required)
    {
        if (required && !IsPresent)
        {
            throw NoLocalPlayer();
        }

        return this;
    }

    private static ApiException NoLocalPlayer() =>
        ApiErrors.Refused("no_local_player", "There is no local player in this game (a dedicated server has none).");

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
    }

    private sealed class Absent : PlayerOrigin
    {
        internal static readonly Absent Instance = new Absent();

        internal override double? DistanceTo(Vector3 position) => null;

        internal override double? ExactDistanceTo(Vector3 position) => null;

        internal override LocalPlayerView? View => null;

        internal override bool IsPresent => false;
    }
}
