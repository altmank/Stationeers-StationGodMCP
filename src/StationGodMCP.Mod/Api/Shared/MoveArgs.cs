#nullable enable

using Newtonsoft.Json.Linq;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared;

/// <summary>
/// move_player's arguments: the player (a name or reference id; the player when absent), exactly one destination,
/// safe_ground and dry_run. A real move runs at once: the tool is instant and its reply says where the player was.
/// </summary>
internal sealed class MoveRequest
{
    private MoveRequest(string? player, MoveDestination destination, bool safeGround, bool dryRun)
    {
        Player = player;
        Destination = destination;
        SafeGround = safeGround;
        DryRun = dryRun;
    }

    /// <summary>The name or reference id asked for; null for the player (see PlayerOrigin).</summary>
    internal string? Player { get; }

    internal MoveDestination Destination { get; }

    internal bool SafeGround { get; }

    internal bool DryRun { get; }

    internal static MoveRequest Of(Args args) =>
        new MoveRequest(NonEmpty(args.OptionalString("player"), "player"), MoveDestination.Of(args),
            args.OptionalBool("safe_ground") ?? false, args.OptionalBool("dry_run") ?? false);

    /// <summary>A player's name or id as given; a blank one is invalid_argument.</summary>
    internal static string? NonEmpty(string? text, string name)
    {
        if (text != null && text.Trim().Length == 0)
        {
            throw ApiErrors.InvalidArgument($"Argument '{name}' must be a player's name or reference id, not empty.");
        }

        return text;
    }
}

/// <summary>Where the player goes: a point, next to a thing, or next to another player.</summary>
internal abstract class MoveDestination
{
    /// <summary>The largest coordinate a point may have, in metres.</summary>
    internal const double MaximumCoordinate = 100000.0;

    private static readonly string[] Forms = { "at", "to_id", "near_player" };

    private MoveDestination()
    {
    }

    /// <summary>The form's name as the reply gives it: at, to_id or near_player.</summary>
    internal abstract string Form { get; }

    internal static MoveDestination Of(Args args)
    {
        int given = 0;
        foreach (string form in Forms)
        {
            given += args.Has(form) ? 1 : 0;
        }

        if (given != 1)
        {
            throw ApiErrors.InvalidArgument("Pass exactly one destination: at [x, y, z], to_id or near_player.");
        }

        if (args.Has("at"))
        {
            return new Point(PointOf(args.Optional("at")!, "at"));
        }

        if (args.Has("to_id"))
        {
            return new NextToThing(args.ThingId("to_id"));
        }

        return new NextToPlayer(MoveRequest.NonEmpty(args.OptionalString("near_player"), "near_player")!);
    }

    /// <summary>[x, y, z] or {x, y, z} in metres, each finite and within MaximumCoordinate.</summary>
    internal static Vec3 PointOf(JToken token, string name)
    {
        if (token is JArray array && array.Count == 3 && Number(array[0], out double x) &&
            Number(array[1], out double y) && Number(array[2], out double z))
        {
            return Within(new Vec3(x, y, z), name);
        }

        if (token is JObject item && Number(item["x"], out double ox) && Number(item["y"], out double oy) &&
            Number(item["z"], out double oz))
        {
            return Within(new Vec3(ox, oy, oz), name);
        }

        throw ApiErrors.InvalidArgument($"{name} must be a position: [x, y, z] or {{x, y, z}} in metres.");
    }

    private static Vec3 Within(Vec3 point, string name)
    {
        if (System.Math.Abs(point.X) > MaximumCoordinate || System.Math.Abs(point.Y) > MaximumCoordinate ||
            System.Math.Abs(point.Z) > MaximumCoordinate)
        {
            throw ApiErrors.InvalidArgument($"{name} is beyond {MaximumCoordinate:0} m of the world origin.");
        }

        return point;
    }

    private static bool Number(JToken? token, out double value)
    {
        value = 0.0;
        if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.Float))
        {
            return false;
        }

        value = token.Value<double>();
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    internal sealed class Point : MoveDestination
    {
        internal Point(Vec3 at)
        {
            At = at;
        }

        internal Vec3 At { get; }

        internal override string Form => "at";
    }

    internal sealed class NextToThing : MoveDestination
    {
        internal NextToThing(ThingId thing)
        {
            Thing = thing;
        }

        internal ThingId Thing { get; }

        internal override string Form => "to_id";
    }

    internal sealed class NextToPlayer : MoveDestination
    {
        internal NextToPlayer(string player)
        {
            Player = player;
        }

        /// <summary>The other player's name or reference id.</summary>
        internal string Player { get; }

        internal override string Form => "near_player";
    }
}
