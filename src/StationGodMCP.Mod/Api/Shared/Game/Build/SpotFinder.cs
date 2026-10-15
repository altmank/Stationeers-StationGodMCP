#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Shared.Game.Structures;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Profiling;
using StationGodMCP.Pure.Shaping;
using StationGodMCP.Pure.Verbs;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game.Build;

/// <summary>
/// plan spot's search: ranked places for a prefab on one face plane (plane and side, or the face looked at) or on the
/// walls of a room, near a point. Every 0.5 m spot within the radius is aimed as the cursor snaps it, filtered on
/// geometry first (SpotSearch.Filter: its cells free, its mesh box clear of every other thing's, out of door keep-outs,
/// one wall section by its mesh box, high enough above the floor, clear in front), then, nearest first and at most
/// max_checks of them, checked with the game's own cursor and the layout preview (PlacementLayout) for the rest (no
/// visual overlap, ports reachable). Returned best first with build structure's options for each. Read only.
/// </summary>
internal static class SpotFinder
{
    private const string Label = "plan spot";
    private const int MaximumChecks = 200;
    private const int MaximumLimit = 20;
    private const int MaximumCandidates = 4000;
    private const int MaximumBodyCells = 40000;
    private const int MaximumReasons = 8;

    /// <summary>How many candidate spots one step of Finding aims, and how many it checks.</summary>
    internal const int CandidatesAStep = 64;

    internal const int ChecksAStep = 4;

    /// <summary>
    /// The spots as work over frames (A3): the planes and their spots a step each, the candidates aimed
    /// CandidatesAStep a step, the passed ones checked ChecksAStep a step.
    /// </summary>
    [Profiled]
    internal static IEnumerable<object?> Finding(SpotOrder order, StationGodMCP.Pure.Scheduling.Answer<PlanSpotView> answer)
    {
        GridFacts facts = new GridFacts(new CableRunKind(), SmallGridBlock.None, new HashSet<long>());
        BuildCatalogue catalogue = WorldModel.Prefabs.Build;
        Structure? found = catalogue.Find(order.Prefab, out string? issue);
        if (found == null)
        {
            throw ApiErrors.Refused("invalid_prefab", $"{Label}: {issue ?? "no such prefab"}");
        }

        Structure prefab = found;
        Structure? cursor = catalogue.CursorOf(prefab);
        if (cursor == null)
        {
            throw ApiErrors.Refused("no_cursor", $"{Label}: the game has no placement cursor for {prefab.PrefabName}.");
        }

        CameraUse camera = new CameraUse();
        Vec3 near = Near(order.Near, camera);
        double radius = order.RadiusM;
        SpotRequirements require = order.Require;
        List<SidedPlane> planes = Planes(order, facts, camera, out Room? room, out SidedPlane? named);
        int limit = order.Limit;
        int maxChecks = order.MaxChecks;
        GridStep? facing = order.Facing;

        // Every plane's spots within the radius that face into the room, nearest first across all planes; at most
        // MaximumCandidates of them are aimed, which bounds the frame's work without starving a plane that happens to
        // come later in the list. The plane named with room is seen from side as asked: seen from the side away from
        // the room (under its floor, over its ceiling), its spots are those with the room behind them.
        List<(PlaneSpots Plane, double U, double V, double Distance)> tries =
            new List<(PlaneSpots, double, double, double)>();
        foreach (SidedPlane plane in planes)
        {
            PlaneSpots search = new PlaneSpots(plane, TurnFor(prefab, plane, facing));
            (double qu, double qv) = plane.Project(near);
            foreach ((double u, double v, double distance) in SpotSearch.Within(qu, qv,
                         near[plane.Plane.Axis] - plane.Plane.Metres, radius))
            {
                if (room == null || plane.RoomOnSide(u, v, room, facts) ||
                    (plane == named && plane.RoomBehind(u, v, room, facts)))
                {
                    tries.Add((search, u, v, distance));
                }
            }

            yield return null;
        }

        tries.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));
        List<Candidate> passed = new List<Candidate>();
        HashSet<Vector3> seen = new HashSet<Vector3>();
        SpotReasons reasons = new SpotReasons();
        int filtered = 0;
        for (int index = 0; index < tries.Count && index < MaximumCandidates; index++)
        {
            (PlaneSpots on, double u, double v, _) = tries[index];
            on.Bodies ??= NearBodies.Around(Region(on.View, near, radius), facts, new HashSet<long>(),
                NearKinds.AnyPiece, MaximumBodyCells);
            Candidate? candidate = Candidate.At(prefab, cursor, on.View, on.Turn, u, v, near, facts, require, seen,
                on.Bodies);
            if (index % CandidatesAStep == CandidatesAStep - 1)
            {
                yield return null;
            }

            if (candidate == null)
            {
                continue;
            }

            if (candidate.Failed.Count > 0)
            {
                filtered++;
                reasons.Add(candidate.Failed[0]);
                continue;
            }

            passed.Add(candidate);
        }

        passed.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));
        List<Candidate> checkedSpots = new List<Candidate>();
        int rejected = 0;
        foreach (Candidate candidate in passed)
        {
            if (checkedSpots.Count + rejected >= maxChecks)
            {
                break;
            }

            string? rejection = candidate.Check(prefab, cursor, facts, require);
            if (rejection == null)
            {
                checkedSpots.Add(candidate);
            }
            else
            {
                rejected++;
                reasons.Add(rejection);
            }

            if ((checkedSpots.Count + rejected) % ChecksAStep == 0)
            {
                yield return null;
            }
        }

        if (checkedSpots.Count + rejected < passed.Count)
        {
            Truncations.Note("spots", System.Math.Min(limit, checkedSpots.Count), checkedSpots.Count + 1,
                $"pass max_checks (max {MaximumChecks}): " +
                $"{passed.Count - checkedSpots.Count - rejected} spots that passed the geometry filter were not " +
                "checked", atLeast: true);
        }
        else
        {
            Truncations.Capped("spots", System.Math.Min(limit, checkedSpots.Count), checkedSpots.Count, "limit", MaximumLimit);
        }

        Truncations.Note("reasons", System.Math.Min(MaximumReasons, reasons.Distinct), reasons.Distinct,
            "no option lists more: the most frequent reasons are listed");
        List<(int, double)> scores = checkedSpots.ConvertAll(spot => (spot.Penalty, spot.Distance));
        List<SpotView> spots = new List<SpotView>();
        foreach (int index in SpotSearch.Rank(scores))
        {
            if (spots.Count >= limit)
            {
                break;
            }

            spots.Add(checkedSpots[index].View(prefab));
        }

        answer.Value = new PlanSpotView(prefab.PrefabName,
            planes.ConvertAll(plane => $"{plane.Plane} seen from {plane.Side.Name}"), spots, passed.Count + filtered,
            filtered, checkedSpots.Count + rejected, rejected,
            reasons.Top(MaximumReasons).ConvertAll(static reason => new SpotReasonView(reason.Reason, reason.Count)),
            camera.Source);
    }

    // The search window on the plane, a metre deep either side: where the things a spot could clash with stand.
    private static Box3 Region(SidedPlane plane, Vec3 near, double radius)
    {
        (double u, double v) = plane.Project(near);
        Vec3 depth = Vec3.Of(plane.Side);
        return new Box3(plane.PointAt(u - radius, v - radius) - depth, plane.PointAt(u + radius, v + radius) + depth);
    }

    // One plane of the search: the turn its spots are tried in, and the bodies near it once a spot there is aimed.
    private sealed class PlaneSpots
    {
        internal PlaneSpots(SidedPlane view, CubeRotation turn)
        {
            View = view;
            Turn = turn;
        }

        internal SidedPlane View { get; }

        internal CubeRotation Turn { get; }

        internal List<NearBody>? Bodies { get; set; }
    }

    private static Vec3 Near(NearPoint near, CameraUse camera) => near switch
    {
        NearPoint.At at => at.Point,
        NearPoint.Player => Bodies.V(PlayerOrigin.RequireHuman().Position),
        NearPoint.Thing thing => Bodies.V(TargetIds.Thing(thing.Id, Label).Position),
        _ => (camera.Require("near looking_at (or give near)").HitWithin(20.0) ??
              throw ApiErrors.Refused("no_crosshair_hit",
                  $"{Label}: near looking_at: the look ray hits nothing within 20 m; give near.")).Point
    };

    // One plane (plane and side, or looking) or the walls of a room: every face of a room cell toward a cell outside
    // the room that carries a wall or window, seen from inside.
    private static List<SidedPlane> Planes(SpotOrder order, GridFacts facts, CameraUse camera, out Room? room,
        out SidedPlane? named)
    {
        room = null;
        named = null;
        if (order.Room is not { } id)
        {
            return new List<SidedPlane> { PlaneOf(order, facts, camera) };
        }

        Room found = StructureAirRecord.FindRoom(id) ??
                     throw ApiErrors.Refused("room_not_found", $"{Label}: no closed room has id {id}; find room lists them.");
        room = found;
        HashSet<GridCell> cells = new HashSet<GridCell>();
        foreach (WorldGrid grid in new List<WorldGrid>(found.Grids))
        {
            cells.Add(Upgrades.PieceShapes.Cell(grid.Value));
        }

        List<SidedPlane> planes = new List<SidedPlane>();
        HashSet<(int, int, int)> seen = new HashSet<(int, int, int)>();
        foreach (GridCell cell in cells)
        {
            foreach (GridStep step in GridStep.All)
            {
                if (step.IsVertical)
                {
                    continue;
                }

                GridCell next = new GridCell(cell.X + step.Dx * SmallCellCode.Large,
                    cell.Y + step.Dy * SmallCellCode.Large, cell.Z + step.Dz * SmallCellCode.Large);
                if (cells.Contains(next) || !facts.FaceStructures(cell, step).Exists(s => !Openings.IsDoor(s)))
                {
                    continue;
                }

                int coordinate = FacePlane.Component(cell, step.Axis) + (step.Dx + step.Dy + step.Dz) * 10;
                if (seen.Add((step.Axis, coordinate, step.Opposite.Index)))
                {
                    planes.Add(new SidedPlane(FacePlane.Of(step.Axis, coordinate), step.Opposite));
                }
            }
        }

        if (order.NamesPlane)
        {
            SidedPlane asked = NamedPlane(order, facts, cells, id, camera);
            named = planes.Find(plane => plane.Plane.Axis == asked.Plane.Axis &&
                                         plane.Plane.Coordinate == asked.Plane.Coordinate &&
                                         plane.Side.Index == asked.Side.Index);
            if (named == null)
            {
                named = asked;
                seen.Add((asked.Plane.Axis, asked.Plane.Coordinate, asked.Side.Index));
                planes.Add(asked);
            }
        }

        return planes;
    }

    private static SidedPlane PlaneOf(SpotOrder order, GridFacts facts, CameraUse camera) =>
        SidedPlane.Of(order.Looking, order.Plane, order.Side, order.Around, facts, camera, out _);

    // plane (or looking) with room: that plane too, e.g. the room's floor or ceiling. Seen from side when given, else
    // from the room's side of it; a plane no room cell touches is refused.
    private static SidedPlane NamedPlane(SpotOrder order, GridFacts facts, HashSet<GridCell> cells, long room,
        CameraUse camera)
    {
        if (order.Side != null || order.Looking)
        {
            return PlaneOf(order, facts, camera);
        }

        string text = order.Plane!;
        FacePlane plane = SidedPlane.ParsePlane(text);
        GridStep plus = GridStep.All[plane.Axis * 2];
        foreach (GridCell cell in cells)
        {
            int component = FacePlane.Component(cell, plane.Axis);
            if (component - 10 == plane.Coordinate || component + 10 == plane.Coordinate)
            {
                return new SidedPlane(plane, component > plane.Coordinate ? plus : plus.Opposite);
            }
        }

        throw ApiErrors.InvalidArgument($"{Label}: plane {text} does not touch room {room}: no cell of the room lies " +
                                        "against it. Name a face plane of the room, or give side.");
    }

    // The turn a spot is tried in. A mounted piece faces out of the plane (or facing), its top up. A grid-placed piece
    // stands: on a floor its top is the plane's side and it faces facing (default +z); against a wall its top is +y
    // and it faces out of the wall (or facing).
    private static CubeRotation TurnFor(Structure prefab, SidedPlane plane, GridStep? facing)
    {
        if (prefab.PlacementType != PlacementSnap.Grid)
        {
            GridStep forward = facing ?? plane.Side;
            return CubeRotation.FromFacing(forward, RotationSpec.DefaultUp(forward))!;
        }

        GridStep up = plane.Side.IsVertical ? plane.Side : GridStep.All[2];
        GridStep front = facing ?? (plane.Side.IsVertical ? GridStep.All[4] : plane.Side);
        return CubeRotation.FromFacing(front, up) ?? CubeRotation.FromFacing(front, RotationSpec.DefaultUp(front))!;
    }

    private sealed class Candidate
    {
        private Candidate(SidedPlane plane, CubeRotation turn, Quaternion rotation, Vector3 position,
            List<GridCell> cells, double distance, List<string> failed)
        {
            Plane = plane;
            Turn = turn;
            Rotation = rotation;
            Position = position;
            Cells = cells;
            Distance = distance;
            Failed = failed;
        }

        internal SidedPlane Plane { get; }

        internal CubeRotation Turn { get; }

        internal Quaternion Rotation { get; }

        internal Vector3 Position { get; }

        internal List<GridCell> Cells { get; }

        internal double Distance { get; }

        internal List<string> Failed { get; }

        internal int Penalty { get; private set; }

        internal List<LayoutConflict> Conflicts { get; private set; } = new List<LayoutConflict>();

        internal static Candidate? At(Structure prefab, Structure cursor, SidedPlane plane, CubeRotation turn, double u,
            double v, Vec3 near, GridFacts facts, SpotRequirements require, HashSet<Vector3> seen,
            List<NearBody> bodies)
        {
            (double x, double y, double z, double w) = turn.ToQuaternion();
            Quaternion rotation = new Quaternion((float)x, (float)y, (float)z, (float)w);
            Vector3 position = CursorCheck.Snap(cursor, Bodies.U(plane.PointAt(u, v)), rotation);
            if (!seen.Add(position))
            {
                return null;
            }

            List<GridCell> cells = Bodies.SmallCells(prefab, position, rotation);
            int occupied = 0;
            int keepOut = 0;
            foreach (GridCell cell in cells)
            {
                SmallOccupancy occupancy = facts.Occupancy(cell);
                occupied += occupancy.Device || occupancy.Other || occupancy.Chute ? 1 : 0;
                keepOut += facts.Opening(cell).IsDoor ? 1 : 0;
            }

            Box3 render = Bodies.RenderBox(prefab, position, rotation);
            MountRect? mount = cells.Count > 0
                ? MountRect.Of(Box3.OfSmallCells(cells), PlacementLayout.MountOutward(prefab, turn), render)
                : null;
            double bottom = cells.Count > 0 ? Box3.OfSmallCells(cells).Min.Y : position.y;
            Metres foot = new Metres(position.x, bottom, position.z);
            double floor = AtResolver.FloorBelow(foot, facts) ?? AtResolver.FirstPlaneBelow(foot);
            SpotGeometry geometry = new SpotGeometry(cells.Count, occupied, keepOut,
                mount?.Faces().Count ?? 0, bottom - floor,
                FrontBlocked(cells, PrefabControls.FrontOf(prefab, turn), require, facts),
                Clashes(render, cells, bodies));
            return new Candidate(plane, turn, rotation, position, cells, (Bodies.V(position) - near).Length,
                SpotSearch.Filter(geometry, require));
        }

        // The things whose mesh box its own clashes with (VisualClash), those sharing one of its cells skipped.
        private static int Clashes(Box3 render, List<GridCell> cells, List<NearBody> bodies)
        {
            int clashes = 0;
            foreach (NearBody body in bodies)
            {
                clashes += !body.Shares(cells) && VisualClash.Clashes(render, body.Render) ? 1 : 0;
            }

            return clashes;
        }

        private static int FrontBlocked(List<GridCell> cells, GridStep front, SpotRequirements require,
            GridFacts facts)
        {
            HashSet<GridCell> own = new HashSet<GridCell>(cells);
            int blocked = 0;
            for (int layer = 1; layer <= require.FrontClearCells; layer++)
            {
                foreach (GridCell cell in cells)
                {
                    GridCell ahead = front.From(cell, layer);
                    if (own.Contains(ahead))
                    {
                        continue;
                    }

                    SmallOccupancy occupancy = facts.Occupancy(ahead);
                    blocked += occupancy.Device || occupancy.Other || occupancy.Chute ? 1 : 0;
                }
            }

            return blocked;
        }

        /// <summary>The cursor check and the layout preview: why either rules the spot out; null when it passes.</summary>
        internal string? Check(Structure prefab, Structure cursor, GridFacts facts, SpotRequirements require)
        {
            string? refusal = PlayerPlacement.Refusal(prefab, cursor, Position, Rotation);
            if (refusal != null)
            {
                return "the game refuses it: " + refusal + PlacePlanner.FrameNote(Position, Rotation, refusal);
            }

            LayoutPreview layout = PlacementLayout.Of(prefab, Position, Rotation, Turn, facts, !require.AvoidDoors,
                new HashSet<long>());
            Conflicts = layout.Conflicts;
            Penalty = layout.Penalty;
            foreach (LayoutConflict conflict in layout.Conflicts)
            {
                if (conflict.Level == ConflictLevel.Problem ||
                    (require.NoVisualOverlap && conflict.Code == ConflictCodes.VisualOverlap) ||
                    (require.OneSection && conflict.Code == ConflictCodes.CrossesSeam))
                {
                    return $"{conflict.Code}: {conflict.Message}";
                }
            }

            if (require.PortsReachable && layout.View.PortChecks != null &&
                layout.View.PortChecks.Exists(port => port.Blocked != null && !port.Joins))
            {
                return "a port's joining cell is blocked (require.ports_reachable)";
            }

            return null;
        }

        internal SpotView View(Structure prefab)
        {
            JObject build = new JObject
            {
                ["what"] = "structure",
                ["prefab"] = prefab.PrefabName,
                ["at"] = new JArray(Round(Position.x), Round(Position.y), Round(Position.z)),
                ["facing"] = Turn.Forward.Name,
                ["up"] = Turn.Up.Name
            };
            return new SpotView(PointView.Of(Bodies.V(Position)), OrientationView.Of(Turn),
                System.Math.Round(Distance, 2), Penalty, $"{Plane.Plane} seen from {Plane.Side.Name}",
                Conflicts.ConvertAll(conflict => new ConflictView(conflict)), build);
        }

        private static double Round(float value) => System.Math.Round(value * 100.0) / 100.0;
    }
}
