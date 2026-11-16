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
    internal PlayerMoveOrder(string? player, MoveDestination destination, bool safeGround, StandOptions? stand = null)
    {
        Player = player;
        Destination = destination;
        SafeGround = safeGround;
        Stand = stand;
    }

    public string? Player { get; }

    public MoveDestination Destination { get; }

    public bool SafeGround { get; }

    /// <summary>The stand asked for (with to or at), or null for the plain move.</summary>
    public StandOptions? Stand { get; }

    /// <summary>The order the options give; null with every problem added.</summary>
    internal static PlayerMoveOrder? Of(string? player, JToken? at, string? to, string? toPlayer, bool safeGround,
        List<OptionProblem> problems, JToken? stand = null)
    {
        int before = problems.Count;
        if (player != null && player.Trim().Length == 0)
        {
            problems.Add(new OptionProblem("player", "is a player's name or id, not empty"));
        }

        MoveDestination? destination = MoveDestination.Of(at, to, toPlayer, problems);
        StandOptions? standing = stand == null ? null : StandOrder.Of(stand, toPlayer != null, safeGround, problems);
        return problems.Count == before ? new PlayerMoveOrder(player, destination!, safeGround, standing) : null;
    }
}

/// <summary>move player's stand option, checked: {reach_m, clear_m, floor, on}, used with to or at.</summary>
internal static class StandOrder
{
    private static readonly string[] Keys = { "reach_m", "clear_m", "floor", "on" };

    internal static StandOptions? Of(JToken stand, bool withToPlayer, bool safeGround, List<OptionProblem> problems)
    {
        int before = problems.Count;
        if (stand is not JObject given)
        {
            problems.Add(new OptionProblem("stand", "is an object {reach_m, clear_m, floor, on}"));
            return null;
        }

        if (withToPlayer)
        {
            problems.Add(new OptionProblem("stand", "goes with to or at, not to_player"));
        }

        if (safeGround)
        {
            problems.Add(new OptionProblem("stand", "goes without safe_ground: the stand finds its own floor"));
        }

        foreach (JProperty property in given.Properties())
        {
            if (Array.IndexOf(Keys, property.Name) < 0)
            {
                problems.Add(new OptionProblem("stand." + property.Name, "is not a stand option: " + string.Join(", ", Keys)));
            }
        }

        StandOptions fresh = StandOptions.Default;
        double reach = Number(given, "reach_m", fresh.ReachM, problems);
        if (reach <= 0 || reach > StandOptions.MaxReachM)
        {
            problems.Add(new OptionProblem("stand.reach_m", $"is above 0 and at most {StandOptions.MaxReachM:0} m, the game's reach"));
        }

        double clear = Number(given, "clear_m", fresh.ClearM, problems);
        if (clear < 0 || clear > 2)
        {
            problems.Add(new OptionProblem("stand.clear_m", "is 0 to 2 metres"));
        }

        FloorRule floor = fresh.Floor;
        if (given["floor"] is { Type: not JTokenType.Null } floorText)
        {
            string? name = floorText.Type == JTokenType.String ? (string)floorText! : null;
            if (name == "required")
            {
                floor = FloorRule.Required;
            }
            else if (name == "any")
            {
                floor = FloorRule.Any;
            }
            else
            {
                problems.Add(new OptionProblem("stand.floor", "is required or any"));
            }
        }

        bool on = Flag(given, "on", fresh.On, problems);
        return problems.Count == before ? new StandOptions(reach, clear, floor, on) : null;
    }

    private static double Number(JObject given, string key, double fallback, List<OptionProblem> problems)
    {
        if (given[key] is not { Type: not JTokenType.Null } token)
        {
            return fallback;
        }

        if (token.Type is JTokenType.Integer or JTokenType.Float)
        {
            return (double)token;
        }

        problems.Add(new OptionProblem("stand." + key, "is a number"));
        return fallback;
    }

    private static bool Flag(JObject given, string key, bool fallback, List<OptionProblem> problems)
    {
        if (given[key] is not { Type: not JTokenType.Null } token)
        {
            return fallback;
        }

        if (token.Type == JTokenType.Boolean)
        {
            return (bool)token;
        }

        problems.Add(new OptionProblem("stand." + key, "is true or false"));
        return fallback;
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
