#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game.Build;

/// <summary>Where a relative at landed, how it was read, and the face it was on when there was one.</summary>
internal sealed class ResolvedAt
{
    internal ResolvedAt(Metres point, string how, GridStep? faceOutward)
    {
        Point = point;
        How = how;
        FaceOutward = faceOutward;
    }

    internal Metres Point { get; }

    internal string How { get; }

    /// <summary>The outward normal of the face the point was taken on (crosshair, on_face_i_look_at); null otherwise.</summary>
    internal GridStep? FaceOutward { get; }
}

/// <summary>
/// Reads place_structure's relative at and named facings against the world. at: {crosshair: true} (where the look ray
/// hits, within 10 m), {relative_to: "player" | "crosshair" | a reference id | {reference_id}, frame: player | world |
/// target, right_m, up_m, forward_m, from: origin | top | bottom | left | right | front | back} (from a thing's body:
/// the middle of that side of its footprint box, sides read in the frame), or {on_face_i_look_at: true, along_right_m,
/// along_up_m} (on the face the look ray hits, right and up as the viewer sees them). The player frame is level: right
/// and forward are the world axes nearest the player's own (ambiguous_axis within 10 degrees of a diagonal).
/// </summary>
internal static class AtResolver
{
    private const double ReachM = 10.0;

    internal static ResolvedAt Resolve(AtArg at, GridFacts facts, string name)
    {
        switch (at)
        {
            case AtArg.Absolute absolute:
                return new ResolvedAt(absolute.Point, "as given", null);
            case AtArg.Relative relative:
                return Relative(new Args(relative.Spec), facts, name);
            default:
                throw ApiErrors.InvalidArgument($"{name} cannot be read.");
        }
    }

    private static ResolvedAt Relative(Args spec, GridFacts facts, string name)
    {
        if (spec.OptionalBool("on_face_i_look_at") ?? false)
        {
            return OnFace(spec, name);
        }

        if ((spec.OptionalBool("crosshair") ?? false) && !spec.Has("relative_to"))
        {
            Hit hit = Crosshair(name);
            Vec3 offset = OffsetIn(spec, PlayerFrame(spec, name), hit.Point);
            return new ResolvedAt(ToMetres(offset), $"the crosshair's hit {hit.Point}" + OffsetText(spec),
                hit.Outward);
        }

        JToken reference = spec.Optional("relative_to") ??
                           throw ApiErrors.InvalidArgument($"{name} needs crosshair, relative_to or on_face_i_look_at.");
        BodyAnchor anchor = RelativeMath.AnchorOf(spec.OptionalString("from")) ??
                            throw ApiErrors.InvalidArgument(
                                $"{name}.from must be origin, top, bottom, left, right, front or back.");
        if (reference.Type == JTokenType.String && reference.Value<string>()!.Trim().ToLowerInvariant() == "player")
        {
            Human human = PlayerOrigin.RequireHuman();
            Frame3 frame = FrameOf(spec, "player", null, name);
            Vec3 origin = Bodies.V(human.Position);
            return new ResolvedAt(ToMetres(OffsetIn(spec, frame, origin)),
                $"the player at {origin} in the {frame.Name} frame" + OffsetText(spec), null);
        }

        if (reference.Type == JTokenType.String && reference.Value<string>()!.Trim().ToLowerInvariant() == "crosshair")
        {
            Hit hit = Crosshair(name);
            Frame3 frame = FrameOf(spec, "player", null, name);
            return new ResolvedAt(ToMetres(OffsetIn(spec, frame, hit.Point)),
                $"the crosshair's hit {hit.Point} in the {frame.Name} frame" + OffsetText(spec), hit.Outward);
        }

        ThingId id = reference is JObject item
            ? new Args(item).ThingId("reference_id")
            : ThingId.TryRead(reference, out ThingId read)
                ? read
                : throw ApiErrors.InvalidArgument(
                    $"{name}.relative_to must be \"player\", \"crosshair\", a reference id or {{reference_id}}.");
        Thing thing = GameLookup.RequireThing(id);
        Frame3 thingFrame = FrameOf(spec, "target", thing, name);
        Vec3 start = Bodies.V(thing.ThingTransformPosition);
        if (anchor != BodyAnchor.Origin)
        {
            Box3 box = BodyBox(thing);
            start = RelativeMath.Anchor(start, box, thingFrame, anchor);
        }

        return new ResolvedAt(ToMetres(OffsetIn(spec, thingFrame, start)),
            $"{Names.Of(thing)} {thing.ReferenceId} ({anchor.ToString().ToLowerInvariant()} {start}) in the " +
            $"{thingFrame.Name} frame" + OffsetText(spec), null);
    }

    private static ResolvedAt OnFace(Args spec, string name)
    {
        Hit hit = Crosshair(name);
        if (!hit.Outward.HasValue || hit.Plane == null)
        {
            throw ApiErrors.Refused("no_face", $"{name}: the look ray hits {hit.Point}, which is not on a 2 m face " +
                                               "plane; look straight at a wall, floor or ceiling.");
        }

        ViewBasis basis = Look.Basis(out _) ?? throw NoCamera(name);
        (GridStep right, GridStep up) = RelativeMath.FaceAxes(hit.Outward.Value, basis.LevelForward, basis.LevelRight);
        Vec3 point = hit.Point + Vec3.Of(right) * (spec.OptionalDouble("along_right_m") ?? 0.0) +
                     Vec3.Of(up) * (spec.OptionalDouble("along_up_m") ?? 0.0);
        point = point.With(hit.Plane.Value.Axis, hit.Plane.Value.Metres);
        return new ResolvedAt(ToMetres(point),
            $"on the face {hit.Plane} facing {hit.Outward.Value.Name} (right {right.Name}, up {up.Name}) from the " +
            $"crosshair's hit {hit.Point}", hit.Outward);
    }

    /// <summary>A named facing as an axis: toward_player, away_from_player, out_of_face, into_room.</summary>
    internal static GridStep Facing(NamedFacing named, ResolvedAt at, GridFacts facts, string name, out string how)
    {
        Vec3 point = new Vec3(at.Point.X, at.Point.Y, at.Point.Z);
        switch (named.Word)
        {
            case "toward_player":
            case "away_from_player":
            {
                Vec3 player = Bodies.V(PlayerOrigin.RequireHuman().Position);
                GridStep toward = RelativeMath.LevelAxis(player - point, out string? ambiguous) ??
                                  throw Ambiguous(name, ambiguous!);
                GridStep facing = named.Word == "toward_player" ? toward : toward.Opposite;
                how = $"{named.Word}: the player is toward {toward.Name}";
                return facing;
            }
            case "out_of_face":
            {
                GridStep? outward = at.FaceOutward ?? Crosshair(name).Outward;
                if (!outward.HasValue)
                {
                    throw ApiErrors.Refused("no_face", $"{name}: out_of_face needs a face: look at a wall, floor or " +
                                                       "ceiling (or give at on it with on_face_i_look_at).");
                }

                how = $"out_of_face: the face looked at faces {outward.Value.Name}";
                return outward.Value;
            }
            default:
            {
                List<GridStep> into = new List<GridStep>();
                System.Func<Vec3, GridStep, bool> ahead = Orienter.RoomAhead(facts);
                foreach (GridStep step in GridStep.All)
                {
                    if (!step.IsVertical && ahead(point, step) && !ahead(point, step.Opposite))
                    {
                        into.Add(step);
                    }
                }

                if (into.Count != 1)
                {
                    throw Ambiguous(name, into.Count == 0
                        ? "no side of the point opens into a room while the other does not (it is not on a room's wall)"
                        : $"the room lies toward {string.Join(" and ", into.ConvertAll(step => step.Name))}");
                }

                how = $"into_room: the room is toward {into[0].Name}";
                return into[0];
            }
        }
    }

    /// <summary>
    /// The face plane under a point: the first even metre at or below it, down to 10 m, with a floor structure on it or
    /// a frame below it; null when there is none (structures-24: the plane below was taken as the floor silently).
    /// </summary>
    internal static double? FloorBelow(Metres point, GridFacts facts)
    {
        double first = FirstPlaneBelow(point);
        for (int step = 0; step < FloorSearchPlanes; step++)
        {
            double plane = first - step * 2.0;
            GridCell below = SmallCellCode.LargeOf(new GridCell((int)System.Math.Round(point.X * 10.0),
                (int)System.Math.Round((plane - 1.0) * 10.0), (int)System.Math.Round(point.Z * 10.0)));
            GridCell face = new GridCell(below.X, below.Y + SmallCellCode.Large / 2, below.Z);
            if (facts.FrameAt(below) != null || facts.FaceStructuresAt(face).Count > 0)
            {
                return plane;
            }
        }

        return null;
    }

    /// <summary>How far down FloorBelow looks, in metres.</summary>
    internal const double FloorSearchM = FloorSearchPlanes * 2.0;

    private const int FloorSearchPlanes = 5;

    /// <summary>The first even metre at or below a point: a point up to half a small cell under a plane (a footprint's
    /// bottom on the floor) rests on that plane.</summary>
    internal static double FirstPlaneBelow(Metres point) =>
        System.Math.Floor((point.Y + MountRect.OnPlaneM) / 2.0) * 2.0;

    private readonly struct Hit
    {
        internal Hit(Vec3 point, GridStep? outward, FacePlane? plane)
        {
            Point = point;
            Outward = outward;
            Plane = plane;
        }

        internal Vec3 Point { get; }

        internal GridStep? Outward { get; }

        internal FacePlane? Plane { get; }
    }

    private static Hit Crosshair(string name)
    {
        if (!Look.HasCamera)
        {
            throw NoCamera(name);
        }

        CursorManager cursor = CursorManager.Instance;
        if (cursor == null || !Look.Cast(cursor, ReachM, out RaycastHit hit))
        {
            throw ApiErrors.Refused("no_crosshair_hit", $"{name}: the look ray hits nothing within {ReachM} m.");
        }

        Vec3 point = Bodies.V(hit.point);
        GridStep? outward = ViewBasis.Along(Bodies.V(hit.normal), 10.0);
        FacePlane? plane = null;
        if (outward.HasValue)
        {
            double along = point[outward.Value.Axis];
            double onPlane = System.Math.Round(along / 2.0) * 2.0;
            plane = System.Math.Abs(along - onPlane) < 0.1
                ? FacePlane.Of(outward.Value.Axis, (int)System.Math.Round(onPlane * 10.0))
                : (FacePlane?)null;
        }

        return new Hit(point, outward, plane);
    }

    // The frame a relative at is measured in: player (level, snapped), world, or the target's own turn.
    private static Frame3 FrameOf(Args spec, string fallback, Thing? target, string name)
    {
        string frame = (spec.OptionalString("frame") ?? fallback).Trim().ToLowerInvariant();
        switch (frame)
        {
            case "world":
                return Frame3.World;
            case "player":
                return PlayerFrame(spec, name);
            case "target":
                if (target == null)
                {
                    throw ApiErrors.InvalidArgument($"{name}.frame target needs relative_to a thing.");
                }

                Quaternion rotation = target.ThingTransformRotation;
                CubeRotation? turn = CubeRotation.FromQuaternion(rotation.x, rotation.y, rotation.z, rotation.w);
                if (turn == null)
                {
                    throw ApiErrors.Refused("ambiguous_axis",
                        $"{name}: {Names.Of(target)} is turned off the grid's axes; use frame world or player.");
                }

                return Frame3.Of(turn.Right, turn.Up, turn.Forward, "target");
            default:
                throw ApiErrors.InvalidArgument($"{name}.frame must be player, world or target.");
        }
    }

    private static Frame3 PlayerFrame(Args spec, string name)
    {
        ViewBasis basis = Look.Basis(out _) ?? throw NoCamera(name);
        bool moves = (spec.OptionalDouble("right_m") ?? 0.0) != 0.0 || (spec.OptionalDouble("forward_m") ?? 0.0) != 0.0;
        if (basis.Ambiguous && moves)
        {
            throw Ambiguous(name, System.FormattableString.Invariant($"you look {basis.YawDegrees:0} degrees round, near a diagonal between ") +
                                  $"{basis.LevelForward.Name} and the next axis");
        }

        return Frame3.Of(basis.LevelRight, GridStep.All[2], basis.LevelForward, "player");
    }

    private static Vec3 OffsetIn(Args spec, Frame3 frame, Vec3 origin) =>
        frame.Offset(origin, spec.OptionalDouble("right_m") ?? 0.0, spec.OptionalDouble("up_m") ?? 0.0,
            spec.OptionalDouble("forward_m") ?? 0.0);

    private static string OffsetText(Args spec)
    {
        double right = spec.OptionalDouble("right_m") ?? 0.0;
        double up = spec.OptionalDouble("up_m") ?? 0.0;
        double forward = spec.OptionalDouble("forward_m") ?? 0.0;
        return right == 0 && up == 0 && forward == 0
            ? string.Empty
            : string.Format(System.Globalization.CultureInfo.InvariantCulture,
                ", moved {0:0.##} m right, {1:0.##} m up, {2:0.##} m forward", right, up, forward);
    }

    // A thing's box for anchors: its small-grid footprint when it has one, else its render box.
    private static Box3 BodyBox(Thing thing)
    {
        if (thing is Structure structure)
        {
            List<GridCell> cells = Bodies.SmallCells(structure);
            if (cells.Count > 0)
            {
                return Box3.OfSmallCells(cells);
            }
        }

        return Bodies.RenderBox(thing);
    }

    private static Metres ToMetres(Vec3 v) => new Metres(v.X, v.Y, v.Z);

    private static ApiException Ambiguous(string name, string why) =>
        ApiErrors.Refused("ambiguous_axis", $"{name}: {why}; give a world axis or frame world instead.");

    private static ApiException NoCamera(string name) =>
        ApiErrors.Refused("no_camera", $"{name}: there is no player camera (a dedicated server has none: a remote " +
                                       "player's camera stays on their own machine).");
}
