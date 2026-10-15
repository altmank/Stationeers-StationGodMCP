#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// move player's order (catalogue move.json, player): the player (a name or id; the player when left out), exactly one
/// destination (at, to or to_player) and safe_ground.
/// </summary>
internal sealed class PlayerMoveOrder
{
    internal PlayerMoveOrder(string? player, MoveDestination destination, bool safeGround)
    {
        Player = player;
        Destination = destination;
        SafeGround = safeGround;
    }

    public string? Player { get; }

    public MoveDestination Destination { get; }

    public bool SafeGround { get; }

    /// <summary>The order the options give; null with every problem added.</summary>
    internal static PlayerMoveOrder? Of(string? player, JToken? at, string? to, string? toPlayer, bool safeGround,
        List<OptionProblem> problems)
    {
        int before = problems.Count;
        if (player != null && player.Trim().Length == 0)
        {
            problems.Add(new OptionProblem("player", "is a player's name or id, not empty"));
        }

        MoveDestination? destination = MoveDestination.Of(at, to, toPlayer, problems);
        return problems.Count == before ? new PlayerMoveOrder(player, destination!, safeGround) : null;
    }
}

/// <summary>Where the player goes: a point, beside a thing, or beside another player.</summary>
internal abstract class MoveDestination
{
    /// <summary>The largest coordinate a point may have, in metres.</summary>
    internal const double MaximumCoordinate = 100000.0;

    private MoveDestination()
    {
    }

    /// <summary>The option the destination came from, as the reply names it: at, to or to_player.</summary>
    internal abstract string Form { get; }

    /// <summary>The one destination given; null with the problem added.</summary>
    internal static MoveDestination? Of(JToken? at, string? to, string? toPlayer, List<OptionProblem> problems)
    {
        int given = (at != null ? 1 : 0) + (to != null ? 1 : 0) + (toPlayer != null ? 1 : 0);
        if (given != 1)
        {
            problems.Add(new OptionProblem("at", given == 0
                ? "or to or to_player is needed: where the player goes"
                : "goes without to and to_player: give one destination"));
            return null;
        }

        if (at != null)
        {
            return PointOf(at, problems) is { } point ? new Point(point) : null;
        }

        string id = (to ?? toPlayer)!;
        string form = to != null ? "to" : "to_player";
        if (id.Trim().Length == 0)
        {
            problems.Add(new OptionProblem(form, to != null ? "is a thing's id, not empty" : "is a player's name or id, not empty"));
            return null;
        }

        return to != null ? new NextToThing(id) : new NextToPlayer(id);
    }

    private static Vec3? PointOf(JToken at, List<OptionProblem> problems)
    {
        Vec3? point = at is JArray list ? Verbs.Point.Of(list) : null;
        if (point is not { } given || !Finite(given))
        {
            problems.Add(new OptionProblem("at", "is a position [x, y, z] in metres"));
            return null;
        }

        if (Math.Abs(given.X) > MaximumCoordinate || Math.Abs(given.Y) > MaximumCoordinate ||
            Math.Abs(given.Z) > MaximumCoordinate)
        {
            problems.Add(new OptionProblem("at", $"is beyond {MaximumCoordinate:0} m of the world origin"));
            return null;
        }

        return given;
    }

    private static bool Finite(Vec3 point) =>
        !double.IsNaN(point.X) && !double.IsInfinity(point.X) && !double.IsNaN(point.Y) &&
        !double.IsInfinity(point.Y) && !double.IsNaN(point.Z) && !double.IsInfinity(point.Z);

    internal sealed class Point : MoveDestination
    {
        internal Point(Vec3 at)
        {
            At = at;
        }

        public Vec3 At { get; }

        internal override string Form => "at";
    }

    /// <summary>Beside a thing: its id as given (a reference id or "looking_at").</summary>
    internal sealed class NextToThing : MoveDestination
    {
        internal NextToThing(string id)
        {
            Id = id;
        }

        public string Id { get; }

        internal override string Form => "to";
    }

    /// <summary>Beside another player: their name or reference id.</summary>
    internal sealed class NextToPlayer : MoveDestination
    {
        internal NextToPlayer(string player)
        {
            Player = player;
        }

        public string Player { get; }

        internal override string Form => "to_player";
    }
}

/// <summary>
/// move player's replies: {player, moved_by, before, after, destination, distance_m, note?}. The report has the same
/// keys, after the place the move would put the player, and one target, [player id, name, position now -> position
/// after], with the what's undo mark.
/// </summary>
internal static class PlayerMoveReplies
{
    internal static MovePlayerView Acted(MovePlayerView moved) => moved;

    internal static ReportReply Reported(MovePlayerView planned, string undoMark)
    {
        ReportTarget target = new ReportTarget(planned.Player.ReferenceId.ToString(), null, planned.Player.DisplayName,
            planned.Before.Position, planned.After.Position, undoMark);
        return Report.Of(new[] { target }, Acted(planned), new List<ReportProblem>());
    }
}
