#nullable enable

using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared;

/// <summary>A world position in metres as given.</summary>
internal readonly struct Metres
{
    internal Metres(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    internal double X { get; }

    internal double Y { get; }

    internal double Z { get; }

    public override string ToString() =>
        string.Format(CultureInfo.InvariantCulture, "({0:0.##}, {1:0.##}, {2:0.##})", X, Y, Z);
}

/// <summary>A prefab as named in a request: by its prefab name, or by its prefab hash.</summary>
internal abstract class PrefabRef
{
    private PrefabRef()
    {
    }

    internal sealed class Named : PrefabRef
    {
        internal Named(string name)
        {
            Name = name;
        }

        internal string Name { get; }

        public override string ToString() => Name;
    }

    internal sealed class Hashed : PrefabRef
    {
        internal Hashed(int hash)
        {
            Hash = hash;
        }

        internal int Hash { get; }

        public override string ToString() => Hash.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Where a placement goes as given: a point in metres, or a position the world resolves (the crosshair, an offset from
/// a thing, the player or the crosshair, a spot on the face looked at).
/// </summary>
internal abstract class AtArg
{
    private AtArg()
    {
    }

    internal sealed class Absolute : AtArg
    {
        internal Absolute(Metres point)
        {
            Point = point;
        }

        internal Metres Point { get; }
    }

    internal sealed class Relative : AtArg
    {
        internal Relative(JObject spec)
        {
            Spec = spec;
        }

        /// <summary>{crosshair}, {relative_to, frame, right_m, up_m, forward_m, from}, or {on_face_i_look_at, ...}.</summary>
        internal JObject Spec { get; }
    }
}

/// <summary>facing as a word the world resolves: toward_player, away_from_player, out_of_face, into_room.</summary>
internal sealed class NamedFacing
{
    internal static readonly string[] Words = { "toward_player", "away_from_player", "out_of_face", "into_room" };

    internal NamedFacing(string word, GridStep? up)
    {
        Word = word;
        Up = up;
    }

    internal string Word { get; }

    internal GridStep? Up { get; }
}

/// <summary>One placement of place_structure as parsed.</summary>
internal sealed class PlacementArgs
{
    internal PlacementArgs(int index, PrefabRef prefab, AtArg at, RotationSpec rotation, BuildStatePick state,
        string? label, string? color, JObject? orient = null, NamedFacing? namedFacing = null,
        double? aboveFloorM = null)
    {
        NamedFacing = namedFacing;
        AboveFloorM = aboveFloorM;
        Orient = orient;
        Index = index;
        Prefab = prefab;
        At = at;
        Rotation = rotation;
        State = state;
        Label = label;
        Color = color;
    }

    internal int Index { get; }

    internal PrefabRef Prefab { get; }

    internal AtArg At { get; }

    /// <summary>facing as a word to resolve; null when facing is an axis or not given.</summary>
    internal NamedFacing? NamedFacing { get; }

    /// <summary>above_floor_m: its footprint's bottom this far above the floor below at.</summary>
    internal double? AboveFloorM { get; }

    internal RotationSpec Rotation { get; }

    internal BuildStatePick State { get; }

    internal string? Label { get; }

    internal string? Color { get; }

    /// <summary>orient: the intent the turn is searched for (Orienter); null when the turn is given.</summary>
    internal JObject? Orient { get; }
}

internal sealed class PlaceArguments
{
    internal PlaceArguments(List<PlacementArgs> placements, ThingId? from, bool free, bool allowDoorKeepOut = false)
    {
        AllowDoorKeepOut = allowDoorKeepOut;
        Placements = placements;
        From = from;
        Free = free;
    }

    internal List<PlacementArgs> Placements { get; }

    /// <summary>The thing materials come from; null for the local player.</summary>
    internal ThingId? From { get; }

    /// <summary>Place without materials (creative worlds only).</summary>
    internal bool Free { get; }

    /// <summary>allow_door_keepout: a piece in a door's keep-out is a warning instead of a problem.</summary>
    internal bool AllowDoorKeepOut { get; }
}

/// <summary>Where remove_structure gives back what deconstructing returns.</summary>
internal enum RefundTo
{
    /// <summary>The source's inventory (from_id, default the local player): worn items collect, the rest at its feet.</summary>
    Source,

    /// <summary>On the ground where each removed piece stood.</summary>
    Ground,

    None
}

internal sealed class RemoveArguments
{
    internal RemoveArguments(List<ThingId> ids, RemovalAllowance allow, RefundTo refundTo, ThingId? from)
    {
        Ids = ids;
        Allow = allow;
        RefundTo = refundTo;
        From = from;
    }

    internal List<ThingId> Ids { get; }

    internal RemovalAllowance Allow { get; }

    internal RefundTo RefundTo { get; }

    internal ThingId? From { get; }
}

/// <summary>A request of place_structure or remove_structure: a job to poll, or a run (dry or confirmed).</summary>
internal abstract class BuildForm<T>
{
    private BuildForm()
    {
    }

    internal sealed class Poll : BuildForm<T>
    {
        internal Poll(string jobId)
        {
            JobId = jobId;
        }

        internal string JobId { get; }
    }

    internal sealed class Run : BuildForm<T>
    {
        internal Run(T arguments, bool confirmed)
        {
            Arguments = arguments;
            Confirmed = confirmed;
        }

        internal T Arguments { get; }

        /// <summary>dry_run false and confirm true: start a job. Otherwise a dry run.</summary>
        internal bool Confirmed { get; }
    }
}

/// <summary>
/// place_structure's and remove_structure's arguments. place_structure takes placements (a list), or one placement's
/// fields at the top level. A placement: prefab (a name, or a prefab hash as a number), at ([x, y, z] or {x, y, z} in
/// metres, a point in the cell), at most one of rotation ([x, y, z] degrees, multiples of 90, Quaternion.Euler order),
/// facing (+x, -x, +y, -y, +z, -z, with an optional up) and face (for pieces placed on a cell face, with an optional
/// up), build_state ("finished", "first" or an index), label, color.
/// </summary>
internal static class BuildArgs
{
    internal const int MaximumPlacements = 64;
    internal const int MaximumRemovals = 256;

    private static readonly string[] PlacementFields =
        { "prefab", "at", "rotation", "facing", "up", "face", "build_state", "label", "color", "orient", "above_floor_m" };

    internal static BuildForm<PlaceArguments> ParsePlace(Args args)
    {
        if (args.Has("job_id"))
        {
            return Poll<PlaceArguments>(args, "placements", "from_id", "free", "prefab", "at", "rotation", "facing",
                "up", "face", "build_state", "label", "color", "allow_door_keepout", "orient", "above_floor_m");
        }

        List<PlacementArgs> placements = new List<PlacementArgs>();
        if (args.Has("placements"))
        {
            args.Reject("placements", PlacementFields);
            List<Args?> items = args.Objects("placements", MaximumPlacements);
            for (int index = 0; index < items.Count; index++)
            {
                placements.Add(Placement(items[index] ??
                                         throw ApiErrors.InvalidArgument($"placements[{index}] must be an object."),
                    index, $"placements[{index}]."));
            }
        }
        else if (args.Has("prefab"))
        {
            placements.Add(Placement(args, 0, string.Empty));
        }
        else
        {
            throw ApiErrors.InvalidArgument("Pass placements, or prefab and at for one placement.");
        }

        bool confirmed = Confirmed(args);
        return new BuildForm<PlaceArguments>.Run(
            new PlaceArguments(placements, args.OptionalThingId("from_id"), args.OptionalBool("free") ?? false,
                args.OptionalBool("allow_door_keepout") ?? false),
            confirmed);
    }

    internal static BuildForm<RemoveArguments> ParseRemove(Args args)
    {
        if (args.Has("job_id"))
        {
            return Poll<RemoveArguments>(args, "reference_ids", "allow_contents", "allow_breach", "allow_broken",
                "allow_burst", "refund_to", "from_id");
        }

        if (!args.Has("reference_ids"))
        {
            throw ApiErrors.InvalidArgument("Pass reference_ids.");
        }

        List<ThingId> ids = args.ThingIds("reference_ids", MaximumRemovals);
        RemovalAllowance allow = new RemovalAllowance(args.OptionalBool("allow_contents") ?? false,
            args.OptionalBool("allow_breach") ?? false, args.OptionalBool("allow_broken") ?? false,
            args.OptionalBool("allow_burst") ?? false);
        RefundTo refundTo = RefundToOf(args.OptionalString("refund_to"));
        bool confirmed = Confirmed(args);
        return new BuildForm<RemoveArguments>.Run(
            new RemoveArguments(ids, allow, refundTo, args.OptionalThingId("from_id")), confirmed);
    }

    internal static PlacementArgs Placement(Args item, int index, string prefix)
    {
        PrefabRef prefab = PrefabOf(item.Optional("prefab"), prefix + "prefab");
        JToken at = item.Optional("at") ?? throw ApiErrors.InvalidArgument($"{prefix}at is required.");
        JObject? orient = item.OptionalObject("orient");
        if (orient != null && (item.Has("rotation") || item.Has("facing") || item.Has("face") || item.Has("up")))
        {
            throw ApiErrors.InvalidArgument($"{prefix}orient chooses the turn: leave out rotation, facing, face and up.");
        }

        if (orient != null)
        {
            MountWordOf(orient, prefix);
        }

        NamedFacing? named = NamedFacingOf(item, prefix);
        double? above = item.OptionalDouble("above_floor_m");
        if (above.HasValue && (above.Value < 0 || above.Value > 20))
        {
            throw ApiErrors.InvalidArgument($"{prefix}above_floor_m must be 0 to 20.");
        }

        return new PlacementArgs(index, prefab, AtOf(at, prefix + "at"),
            named != null ? RotationSpec.Default : RotationOf(item, prefix),
            StateOf(item.Optional("build_state"), prefix + "build_state"), Text(item, "label", prefix),
            ColorOf(item.Optional("color"), prefix + "color"), orient, named, above);
    }

    // orient.mount's shape, checked with the other arguments (the rest of orient is read against the world).
    private static void MountWordOf(JObject orient, string prefix)
    {
        JToken? mount = orient["mount"];
        string? word = mount?.Type == JTokenType.String ? mount.Value<string>()!.Trim().ToLowerInvariant() : null;
        if (mount != null && (word == null ||
                              !(word == "wall" || word == "floor" || word == "ceiling" || GridStep.TryParse(word, out _))))
        {
            throw ApiErrors.InvalidArgument($"{prefix}orient.mount must be wall, floor, ceiling or an axis (+x .. -z).");
        }
    }

    /// <summary>at: a point ([x, y, z] or {x, y, z}), or an object the world resolves (AtArg.Relative).</summary>
    internal static AtArg AtOf(JToken token, string name)
    {
        if (token is JObject item && (item["crosshair"] != null || item["relative_to"] != null ||
                                      item["on_face_i_look_at"] != null))
        {
            RelativeWordsOf(new Args(item), name);
            return new AtArg.Relative(item);
        }

        return new AtArg.Absolute(PositionOf(token, name));
    }

    // A relative at's words, checked with the other arguments (structures-28: a bad from was a placement's problem
    // while every other bad argument is a top-level invalid_argument); the rest is read against the world.
    private static void RelativeWordsOf(Args at, string name)
    {
        if (RelativeMath.AnchorOf(at.OptionalString("from")) == null)
        {
            throw ApiErrors.InvalidArgument($"{name}.from must be origin, top, bottom, left, right, front or back.");
        }

        string? frame = at.OptionalString("frame")?.Trim().ToLowerInvariant();
        if (frame != null && frame != "player" && frame != "world" && frame != "target")
        {
            throw ApiErrors.InvalidArgument($"{name}.frame must be player, world or target.");
        }
    }

    // facing given as a word (toward_player, ...); null when it is an axis or absent.
    private static NamedFacing? NamedFacingOf(Args item, string prefix)
    {
        string? word = item.OptionalString("facing")?.Trim().ToLowerInvariant();
        if (word == null || System.Array.IndexOf(NamedFacing.Words, word) < 0)
        {
            return null;
        }

        if (item.Has("rotation") || item.Has("face"))
        {
            throw ApiErrors.InvalidArgument($"Give at most one of {prefix}rotation, {prefix}facing and {prefix}face.");
        }

        GridStep? up = item.Has("up") ? Step(item.OptionalString("up"), prefix + "up") : (GridStep?)null;
        return new NamedFacing(word, up);
    }

    /// <summary>The farthest a position may lie from the world's origin on any axis, in metres.</summary>
    internal const double MaximumCoordinateM = 100000.0;

    internal static Metres PositionOf(JToken token, string name)
    {
        if (token is JArray array && array.Count == 3 && Number(array[0], out double x) &&
            Number(array[1], out double y) && Number(array[2], out double z))
        {
            return InWorld(new Metres(x, y, z), name);
        }

        if (token is JObject item && item["x"] != null && Number(item["x"]!, out double ox) && item["y"] != null &&
            Number(item["y"]!, out double oy) && item["z"] != null && Number(item["z"]!, out double oz))
        {
            return InWorld(new Metres(ox, oy, oz), name);
        }

        throw ApiErrors.InvalidArgument($"{name} must be a position: [x, y, z] or {{x, y, z}} in metres.");
    }

    // A coordinate past MaximumCoordinateM is no place in a world; the game's floats lose the 0.5 m grid long before.
    private static Metres InWorld(Metres point, string name) =>
        System.Math.Abs(point.X) <= MaximumCoordinateM && System.Math.Abs(point.Y) <= MaximumCoordinateM &&
        System.Math.Abs(point.Z) <= MaximumCoordinateM
            ? point
            : throw ApiErrors.InvalidArgument(
                $"{name} lies more than {MaximumCoordinateM:0} m from the world's origin on an axis: {point}.");

    internal static RotationSpec RotationOf(Args item, string prefix)
    {
        int forms = (item.Has("rotation") ? 1 : 0) + (item.Has("facing") ? 1 : 0) + (item.Has("face") ? 1 : 0);
        if (forms > 1)
        {
            throw ApiErrors.InvalidArgument($"Give at most one of {prefix}rotation, {prefix}facing and {prefix}face.");
        }

        GridStep? up = item.Has("up") ? Step(item.OptionalString("up"), prefix + "up") : (GridStep?)null;
        if (item.Has("rotation"))
        {
            if (up.HasValue)
            {
                throw ApiErrors.InvalidArgument($"{prefix}up goes with facing or face, not rotation.");
            }

            return EulerOf(item.Optional("rotation")!, prefix + "rotation");
        }

        if (item.Has("facing"))
        {
            return new RotationSpec.Facing(Step(item.OptionalString("facing"), prefix + "facing"), up);
        }

        if (item.Has("face"))
        {
            return new RotationSpec.OnFace(Step(item.OptionalString("face"), prefix + "face"), up);
        }

        return up.HasValue
            ? throw ApiErrors.InvalidArgument($"{prefix}up goes with facing or face.")
            : RotationSpec.Default;
    }

    internal static BuildStatePick StateOf(JToken? token, string name)
    {
        if (token == null || token.Type == JTokenType.Null)
        {
            return BuildStatePick.Finished;
        }

        if (token.Type == JTokenType.Integer)
        {
            long value = token.Value<long>();
            return value >= 0 && value < 64
                ? new BuildStatePick.Index((int)value)
                : throw ApiErrors.InvalidArgument($"{name} must be \"finished\", \"first\" or 0 to 63.");
        }

        string word = token.Type == JTokenType.String ? token.Value<string>()!.Trim().ToLowerInvariant() : string.Empty;
        return word switch
        {
            "finished" => BuildStatePick.Finished,
            "first" => BuildStatePick.First,
            _ => throw ApiErrors.InvalidArgument($"{name} must be \"finished\", \"first\" or a state index.")
        };
    }

    private static RotationSpec EulerOf(JToken token, string name)
    {
        if (token is JArray array && array.Count == 3 && Number(array[0], out double x) &&
            Number(array[1], out double y) && Number(array[2], out double z) &&
            CubeRotation.TryQuarterTurns(x, out int xt) && CubeRotation.TryQuarterTurns(y, out int yt) &&
            CubeRotation.TryQuarterTurns(z, out int zt))
        {
            return new RotationSpec.Euler(xt, yt, zt);
        }

        throw ApiErrors.InvalidArgument(
            $"{name} must be [x, y, z] in degrees, each a multiple of 90 (0, 90, 180, 270).");
    }

    private static GridStep Step(string? text, string name) =>
        GridStep.TryParse(text, out GridStep step)
            ? step
            : throw ApiErrors.InvalidArgument($"{name} must be one of +x, -x, +y, -y, +z, -z.");

    internal static PrefabRef PrefabOf(JToken? token, string name)
    {
        if (token != null && token.Type == JTokenType.Integer)
        {
            long hash = token.Value<long>();
            return hash >= int.MinValue && hash <= int.MaxValue
                ? new PrefabRef.Hashed((int)hash)
                : throw ApiErrors.InvalidArgument($"{name} is not a 32-bit prefab hash.");
        }

        string text = token != null && token.Type == JTokenType.String ? token.Value<string>()!.Trim() : string.Empty;
        if (text.Length == 0)
        {
            throw ApiErrors.InvalidArgument($"{name} is required: a prefab name or a prefab hash.");
        }

        return int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int parsed)
            ? new PrefabRef.Hashed(parsed)
            : new PrefabRef.Named(text);
    }

    private static string? ColorOf(JToken? token, string name)
    {
        if (token == null || token.Type == JTokenType.Null)
        {
            return null;
        }

        return token.Type == JTokenType.String || token.Type == JTokenType.Integer
            ? token.ToString()
            : throw ApiErrors.InvalidArgument($"{name} must be a colour name or index.");
    }

    private static string? Text(Args item, string field, string prefix)
    {
        JToken? token = item.Optional(field);
        if (token == null)
        {
            return null;
        }

        return token.Type == JTokenType.String
            ? token.Value<string>()
            : throw ApiErrors.InvalidArgument($"{prefix}{field} must be a string.");
    }

    private static RefundTo RefundToOf(string? text) => (text ?? "source").Trim().ToLowerInvariant() switch
    {
        "source" => RefundTo.Source,
        "ground" => RefundTo.Ground,
        "none" => RefundTo.None,
        _ => throw ApiErrors.InvalidArgument("refund_to must be source, ground or none.")
    };

    private static BuildForm<T> Poll<T>(Args args, params string[] others)
    {
        args.Reject("job_id", others);
        args.Reject("job_id", "dry_run", "confirm", GasHoldVerdict.AcknowledgeArgument);
        return new BuildForm<T>.Poll(args.String("job_id").Trim());
    }

    // A real run needs dry_run false and confirm true; confirm with a dry run is a contradiction.
    private static bool Confirmed(Args args)
    {
        bool dryRun = args.OptionalBool("dry_run") ?? true;
        bool confirm = args.OptionalBool("confirm") ?? false;
        if (dryRun && confirm)
        {
            throw ApiErrors.InvalidArgument("confirm: true needs dry_run: false; nothing was changed.");
        }

        if (!dryRun && !confirm)
        {
            throw ApiErrors.Refused("confirm_required",
                "A real run needs dry_run: false and confirm: true; nothing was changed.");
        }

        return !dryRun;
    }

    private static bool Number(JToken token, out double value)
    {
        value = 0.0;
        if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
        {
            return false;
        }

        value = token.Value<double>();
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
