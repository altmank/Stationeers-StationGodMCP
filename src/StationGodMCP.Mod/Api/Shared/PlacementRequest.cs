#nullable enable

using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Api.Shared;

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
internal abstract class AtForm
{
    private AtForm()
    {
    }

    internal sealed class Absolute : AtForm
    {
        internal Absolute(Metres point)
        {
            Point = point;
        }

        internal Metres Point { get; }
    }

    internal sealed class Relative : AtForm
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

/// <summary>One placement as a call gives it.</summary>
internal sealed class PlacementRequest
{
    internal PlacementRequest(int index, PrefabRef prefab, AtForm at, RotationSpec rotation, BuildStatePick state,
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

    internal AtForm At { get; }

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

/// <summary>
/// What a build report lists beyond its verdict: notes (include_notes: the tool's fixed explanations) and, on a report
/// that is not ready, each placement's layout preview (verbose).
/// </summary>
internal sealed class ReportDetail
{
    internal ReportDetail(bool notes, bool layoutsWhenRefused)
    {
        Notes = notes;
        LayoutsWhenRefused = layoutsWhenRefused;
    }

    internal static ReportDetail Brief { get; } = new ReportDetail(false, false);

    internal bool Notes { get; }

    internal bool LayoutsWhenRefused { get; }
}

internal sealed class PlaceRequest
{
    /// <summary>The most placements one call gives (the options' maxItems).</summary>
    internal const int MaximumPlacements = 64;

    internal PlaceRequest(List<PlacementRequest> placements, ThingId? from, bool free, bool allowDoorKeepOut = false,
        bool footprintCells = false, ReportDetail? detail = null, List<ThingId>? moreFrom = null,
        bool allowLint = false, bool allowPad = false)
    {
        AllowLint = allowLint;
        AllowPad = allowPad;
        MoreFrom = moreFrom ?? new List<ThingId>();
        Detail = detail ?? ReportDetail.Brief;
        FootprintCells = footprintCells;
        AllowDoorKeepOut = allowDoorKeepOut;
        Placements = placements;
        From = from;
        Free = free;
    }

    internal List<PlacementRequest> Placements { get; }

    /// <summary>The thing materials come from; null for the player.</summary>
    internal ThingId? From { get; }

    /// <summary>from_id's further things, tried after From for each material.</summary>
    internal List<ThingId> MoreFrom { get; }

    /// <summary>Place without materials (creative worlds only).</summary>
    internal bool Free { get; }

    /// <summary>allow_door_keepout: a piece in a door's keep-out is a warning instead of a problem.</summary>
    internal bool AllowDoorKeepOut { get; }

    /// <summary>allow lint: a lint problem is a warning, and the report marks what it is about PASSED.</summary>
    internal bool AllowLint { get; }

    /// <summary>allow pad: a placement in a landing pad's approach is a warning, and the report marks it PASSED.</summary>
    internal bool AllowPad { get; }

    /// <summary>include_footprint_cells: each layout preview lists its footprint's cells, not only a count.</summary>
    internal bool FootprintCells { get; }

    internal ReportDetail Detail { get; }
}

internal sealed class RemoveRequest
{
    /// <summary>The most ids one remove call gives.</summary>
    internal const int MaximumRemovals = 256;

    internal RemoveRequest(List<ThingId> ids, RemovalAllowance allow, RefundRoute refundTo, ThingId? from,
        bool includeNotes = false, GasTarget? gasTo = null, bool splitDetail = false)
    {
        SplitDetail = splitDetail;
        GasTo = gasTo;
        IncludeNotes = includeNotes;
        Ids = ids;
        Allow = allow;
        RefundTo = refundTo;
        From = from;
    }

    internal List<ThingId> Ids { get; }

    internal RemovalAllowance Allow { get; }

    /// <summary>refund_to: source, ground or none as before, or a chain of targets (the default).</summary>
    internal RefundRoute RefundTo { get; }

    internal ThingId? From { get; }

    /// <summary>include_notes: the report carries the tool's fixed explanations.</summary>
    internal bool IncludeNotes { get; }

    /// <summary>gas_to: where a removed device's own gas goes instead of being deleted; null to leave it to the game.</summary>
    internal GasTarget? GasTo { get; }

    /// <summary>verbose: every port a split cuts as its own warning; else one per device.</summary>
    internal bool SplitDetail { get; }
}

/// <summary>remove_structure gas_to: a device's connected pipe networks (the first that can take it), or one network.</summary>
internal sealed class GasTarget
{
    private GasTarget(ThingId? network)
    {
        Network = network;
    }

    internal static GasTarget AnyConnected { get; } = new GasTarget(null);

    /// <summary>A pipe network id or a pipe piece on it; null for the connected networks.</summary>
    internal ThingId? Network { get; }

    /// <summary>A verb's gas_to: connected, or a pipe network's id or a pipe on it; null when left out.</summary>
    internal static GasTarget? Of(string? gasTo, List<OptionProblem> problems)
    {
        if (gasTo == null)
        {
            return null;
        }

        if (string.Equals(gasTo.Trim(), "connected", System.StringComparison.OrdinalIgnoreCase))
        {
            return AnyConnected;
        }

        if (ThingId.TryRead(new JValue(gasTo), out ThingId network))
        {
            return new GasTarget(network);
        }

        problems.Add(new OptionProblem("gas_to", "is connected, or a pipe network's id or a pipe on it"));
        return null;
    }
}

/// <summary>
/// The readers of one placement as a call gives it: prefab (a name, or a prefab hash as a number), at ([x, y, z] or
/// {x, y, z} in metres, a point in the cell), at most one of rotation ([x, y, z] degrees, multiples of 90,
/// Quaternion.Euler order), facing (+x, -x, +y, -y, +z, -z, or a word the world resolves, with an optional up) and
/// face (for pieces placed on a cell face, with an optional up), build_state ("finished", "first" or an index), label,
/// color, orient and above_floor_m.
/// </summary>
internal static class PlacementReader
{
    internal static PlacementRequest Placement(Args item, int index, string prefix)
    {
        PrefabRef prefab = PrefabOf(item.Optional("prefab"), prefix + "prefab");
        JToken at = item.Optional("at") ??
                    throw ApiErrors.InvalidArgument($"{prefix}at is required.");
        JObject? orient = item.OptionalObject("orient");
        if (orient != null && (item.Has("rotation") || item.Has("facing") || item.Has("face") || item.Has("up")))
        {
            throw ApiErrors.InvalidArgument($"{prefix}orient chooses the turn: " +
                "leave out rotation, facing, face and " +
                "up.");
        }

        if (orient != null)
        {
            MountWordOf(orient, prefix);
        }

        NamedFacing? named = NamedFacingOf(item, prefix);
        double? above = item.OptionalDouble("above_floor_m");
        if (above.HasValue && (above.Value < 0 || above.Value > 20))
        {
            throw ApiErrors.InvalidArgument($"{prefix}above_floor_m must be 0 " +
                "to 20.");
        }

        return new PlacementRequest(index, prefab, AtOf(at, prefix + "at"),
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
            throw ApiErrors.InvalidArgument($"{prefix}orient." +
                "mount must be wall, floor, ceiling or an axis (+x .. -z).");
        }
    }

    /// <summary>at: a point ([x, y, z] or {x, y, z}), or an object the world resolves (AtForm.Relative).</summary>
    internal static AtForm AtOf(JToken token, string name)
    {
        if (token is JObject item && (item["crosshair"] != null || item["relative_to"] != null ||
                                      item["on_face_i_look_at"] != null))
        {
            RelativeWordsOf(new Args(item), name);
            return new AtForm.Relative(item);
        }

        return new AtForm.Absolute(PositionOf(token, name));
    }

    // A relative at's words, checked with the other arguments (structures-28: a bad from was a placement's problem
    // while every other bad argument is a top-level invalid_argument); the rest is read against the world.
    private static void RelativeWordsOf(Args at, string name)
    {
        if (RelativeMath.AnchorOf(at.OptionalString("from")) == null)
        {
            throw ApiErrors.InvalidArgument($"{name}.from must be origin, top, " +
                "bottom, left, right, front or back.");
        }

        string? frame = at.OptionalString("frame")?.Trim().ToLowerInvariant();
        if (frame != null && frame != "player" && frame != "world" && frame != "target")
        {
            throw ApiErrors.InvalidArgument($"{name}.frame must be player, " +
                "world or target.");
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
            throw ApiErrors.InvalidArgument("Give at most one of " +
                $"{prefix}rotation, " +
                $"{prefix}facing and " +
                $"{prefix}face.");
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

        throw ApiErrors.InvalidArgument($"{name} must be a position: [x, y, z] or {{x, y, z}} in " +
            "metres.");
    }

    // A coordinate past MaximumCoordinateM is no place in a world; the game's floats lose the 0.5 m grid long before.
    private static Metres InWorld(Metres point, string name) =>
        System.Math.Abs(point.X) <= MaximumCoordinateM && System.Math.Abs(point.Y) <= MaximumCoordinateM &&
        System.Math.Abs(point.Z) <= MaximumCoordinateM
            ? point
            : throw ApiErrors.InvalidArgument(
                $"{name} lies more than {MaximumCoordinateM:0} m from the world's origin on an " +
                $"axis: {point}.");

    internal static RotationSpec RotationOf(Args item, string prefix)
    {
        int forms = (item.Has("rotation") ? 1 : 0) + (item.Has("facing") ? 1 : 0) + (item.Has("face") ? 1 : 0);
        if (forms > 1)
        {
            throw ApiErrors.InvalidArgument("Give at most one of " +
                $"{prefix}rotation, " +
                $"{prefix}facing and " +
                $"{prefix}face.");
        }

        GridStep? up = item.Has("up") ? Step(item.OptionalString("up"), prefix + "up") : (GridStep?)null;
        if (item.Has("rotation"))
        {
            if (up.HasValue)
            {
                throw ApiErrors.InvalidArgument($"{prefix}up goes with " +
                    "facing or face, not rotation.");
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
            ? throw ApiErrors.InvalidArgument($"{prefix}up goes with " +
                "facing or face.")
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
                : throw ApiErrors.InvalidArgument($"{name} must be \"finished\", \"first\" or 0 to " +
                    "63.");
        }

        string word = token.Type == JTokenType.String ? token.Value<string>()!.Trim().ToLowerInvariant() : string.Empty;
        return word switch
        {
            "finished" => BuildStatePick.Finished,
            "first" => BuildStatePick.First,
            _ => throw ApiErrors.InvalidArgument($"{name} must be \"finished\", \"first\" or a " +
                "state index.")
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
