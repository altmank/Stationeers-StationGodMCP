#nullable enable

using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// plan spot's call, checked without the game: the prefab (a whole name or a hash), the point the spots are near
/// (looking_at when left out), where to search (one face plane: plane with side, or looking; or a room's walls, with
/// plane or looking naming one more of its planes), the turn, the radius, the requirements, and how many spots are
/// checked and listed.
/// </summary>
internal sealed class SpotOrder
{
    internal const double DefaultRadiusM = 4.0;
    internal const int DefaultChecks = 40;
    internal const int DefaultLimit = 5;

    private SpotOrder(PrefabRef prefab, NearPoint near, double radiusM, long? room, bool looking, string? plane,
        string? side, Vec3? around, GridStep? facing, SpotRequirements require, int limit, int maxChecks)
    {
        Prefab = prefab;
        Near = near;
        RadiusM = radiusM;
        Room = room;
        Looking = looking;
        Plane = plane;
        Side = side;
        Around = around;
        Facing = facing;
        Require = require;
        Limit = limit;
        MaxChecks = maxChecks;
    }

    public PrefabRef Prefab { get; }

    public NearPoint Near { get; }

    public double RadiusM { get; }

    /// <summary>The room whose walls are searched; null searches the one plane named.</summary>
    public long? Room { get; }

    public bool Looking { get; }

    public string? Plane { get; }

    public string? Side { get; }

    public Vec3? Around { get; }

    public GridStep? Facing { get; }

    public SpotRequirements Require { get; }

    public int Limit { get; }

    public int MaxChecks { get; }

    /// <summary>Whether a plane is named besides a room's walls (plane or looking).</summary>
    public bool NamesPlane => Plane != null || Looking;

    /// <summary>The call's order, or null with every problem added.</summary>
    internal static SpotOrder? Of(PlanSpotOptions options, List<OptionProblem> problems)
    {
        int before = problems.Count;
        Selector selector = options.Selector;
        PrefabRef? prefab = PrefabOf(selector.Prefab, problems);
        long? room = null;
        if (selector.Room is { } roomText)
        {
            room = long.TryParse(roomText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) && id > 0
                ? id
                : null;
            if (room == null)
            {
                problems.Add(new OptionProblem("room", "is a room's id, as find room lists it"));
            }
        }
        else if (options.Plane == null && options.Looking != true)
        {
            problems.Add(new OptionProblem("plane", "or looking, or room, is needed: where to search"));
        }

        Vec3? around = null;
        if (options.Around != null && (around = options.Around is JArray point ? Point.Of(point) : null) == null)
        {
            problems.Add(new OptionProblem("around", "is a point, [x, y, z]"));
        }

        GridStep? facing = null;
        if (options.Facing != null)
        {
            if (GridStep.TryParse(options.Facing, out GridStep step))
            {
                facing = step;
            }
            else
            {
                problems.Add(new OptionProblem("facing", "is one of +x, -x, +y, -y, +z, -z"));
            }
        }

        SpotRequirements require = Requirements(options.Require as JObject, problems);
        return problems.Count > before
            ? null
            : new SpotOrder(prefab!, selector.Near ?? new NearPoint.LookingAt(), selector.Radius ?? DefaultRadiusM, room,
                options.Looking == true, options.Plane, options.Side, around, facing, require,
                options.Paging.Limit ?? DefaultLimit, (int)(options.MaxChecks ?? DefaultChecks));
    }

    private static PrefabRef? PrefabOf(PrefabFilter? given, List<OptionProblem> problems)
    {
        if (given is { Patterns.Count: 1 })
        {
            switch (given.Patterns[0])
            {
                case PrefabPattern.Whole whole:
                    return new PrefabRef.Named(whole.Name);
                case PrefabPattern.Hash hash:
                    return new PrefabRef.Hashed(hash.Value);
            }
        }

        problems.Add(new OptionProblem("prefab", "is one prefab: its whole name or its hash"));
        return null;
    }

    // require's keys, each read as its type; a key left out takes its default.
    private static SpotRequirements Requirements(JObject? given, List<OptionProblem> problems)
    {
        JObject require = given ?? new JObject();
        bool Flag(string key, bool otherwise)
        {
            JToken? value = require[key];
            if (value == null)
            {
                return otherwise;
            }

            if (value.Type == JTokenType.Boolean)
            {
                return (bool)value;
            }

            problems.Add(new OptionProblem("require." + key, "is true or false"));
            return otherwise;
        }

        double? Metres(string key)
        {
            JToken? value = require[key];
            if (value == null)
            {
                return null;
            }

            double? metres = OptionReader.NumberOf(value);
            if (metres is not >= 0)
            {
                problems.Add(new OptionProblem("require." + key, "is a distance in metres, 0 or more"));
                return null;
            }

            return metres;
        }

        return new SpotRequirements(Flag("one_section", true), Metres("min_bottom_above_floor_m"),
            Flag("ports_reachable", false), Flag("avoid_doors", true), Metres("front_clear_m") ?? 0.0,
            Flag("no_visual_overlap", true));
    }
}
