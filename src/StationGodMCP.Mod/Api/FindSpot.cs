#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Build;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Shared.Game.Structures;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api;

/// <summary>
/// find_spot: ranked places for a prefab on one face plane (plane and side, or the face looked at) or on the walls of a
/// room, near a point. Every 0.5 m spot within radius_m is aimed as the cursor snaps it, filtered on geometry first
/// (SpotSearch.Filter: its cells free, its mesh box clear of every other thing's, out of door keep-outs, one wall
/// section by its mesh box, high enough above the floor, clear in front), then, nearest first and at most max_checks of them, checked with the game's own cursor and the layout
/// preview (PlacementLayout) for the rest (no visual overlap, ports reachable). Returned best first with ready
/// place_structure arguments. Read only.
/// </summary>
internal static class FindSpotApi
{
    private const double DefaultRadiusM = 4.0;
    private const double MaximumRadiusM = 12.0;
    private const int DefaultLimit = 5;
    private const int DefaultChecks = 40;
    private const int MaximumChecks = 200;
    private const int MaximumCandidates = 4000;
    private const int MaximumBodyCells = 40000;

    internal static FindSpotView Handle(Args args)
    {
        GridFacts facts = new GridFacts(new CableRunKind(), SmallGridBlock.None, new HashSet<long>());
        BuildCatalogue catalogue = BuildCatalogue.Load();
        Structure? found = catalogue.Find(BuildArgs.PrefabOf(args.Optional("prefab"), "prefab"), out string? issue);
        if (found == null)
        {
            throw ApiErrors.Refused("invalid_prefab", issue ?? "No such prefab.");
        }

        Structure prefab = found;
        Structure? cursor = catalogue.CursorOf(prefab);
        if (cursor == null)
        {
            throw ApiErrors.Refused("no_cursor", $"The game has no placement cursor for {prefab.PrefabName}.");
        }

        Vec3 near = Near(args);
        double radius = args.OptionalPositiveDouble("radius_m") ?? DefaultRadiusM;
        if (radius > MaximumRadiusM)
        {
            throw ApiErrors.InvalidArgument($"radius_m is at most {MaximumRadiusM}.");
        }

        SpotRequirements require = Requirements(args.OptionalObject("require"));
        List<PlaneView> planes = Planes(args, facts, out Room? room);
        int limit = args.OptionalInt("limit", 1, 20) ?? DefaultLimit;
        int maxChecks = args.OptionalInt("max_checks", 1, MaximumChecks) ?? DefaultChecks;
        GridStep? facing = args.Has("facing") ? Step(args.OptionalString("facing"), "facing") : (GridStep?)null;

        List<Candidate> passed = new List<Candidate>();
        HashSet<Vector3> seen = new HashSet<Vector3>();
        int filtered = 0;
        int aimed = 0;
        foreach (PlaneView plane in planes)
        {
            CubeRotation turn = TurnFor(prefab, plane, facing);
            (double qu, double qv) = plane.Project(near);
            List<NearBody> bodies = NearBodies.Around(Region(plane, qu, qv, radius), facts, new HashSet<long>(),
                NearKinds.AnyPiece, MaximumBodyCells);
            for (double u = System.Math.Floor((qu - radius) * 2.0) / 2.0; u <= qu + radius; u += 0.5)
            {
                for (double v = System.Math.Floor((qv - radius) * 2.0) / 2.0; v <= qv + radius; v += 0.5)
                {
                    // Every aimed spot counts, duplicates and spots outside the room included, so the cap bounds
                    // the frame's work.
                    if (++aimed > MaximumCandidates)
                    {
                        break;
                    }

                    if (room != null && !plane.RoomOnSide(u, v, room, facts))
                    {
                        continue;
                    }

                    Candidate? candidate = Candidate.At(prefab, cursor, plane, turn, u, v, near, facts, require,
                        seen, bodies);
                    if (candidate == null)
                    {
                        continue;
                    }

                    if (candidate.Failed.Count > 0)
                    {
                        filtered++;
                        continue;
                    }

                    passed.Add(candidate);
                }
            }
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

            if (candidate.Check(prefab, cursor, facts, require))
            {
                checkedSpots.Add(candidate);
            }
            else
            {
                rejected++;
            }
        }

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

        return new FindSpotView(prefab.PrefabName, planes.ConvertAll(plane => $"{plane.Plane} seen from {plane.Side.Name}"),
            spots, passed.Count + filtered, filtered, checkedSpots.Count + rejected, rejected);
    }

    // The search window on the plane, a metre deep either side: where the things a spot could clash with stand.
    private static Box3 Region(PlaneView plane, double u, double v, double radius)
    {
        Vec3 depth = Vec3.Of(plane.Side);
        return new Box3(plane.PointAt(u - radius, v - radius) - depth, plane.PointAt(u + radius, v + radius) + depth);
    }

    private static Vec3 Near(Args args)
    {
        JToken? near = args.Optional("near");
        if (near == null || (near.Type == JTokenType.String && near.Value<string>() == "crosshair"))
        {
            CursorManager cursor = CursorManager.Instance;
            if (cursor == null || !Look.Cast(cursor, 20.0, out RaycastHit hit))
            {
                throw ApiErrors.Refused("no_crosshair_hit", "near: the look ray hits nothing within 20 m; pass near.");
            }

            return Bodies.V(hit.point);
        }

        if (near.Type == JTokenType.String && near.Value<string>() == "player")
        {
            return Bodies.V(PlayerOrigin.RequireHuman().Position);
        }

        if (near is JObject item && item["reference_id"] != null)
        {
            return Bodies.V(GameLookup.RequireThing(new Args(item).ThingId("reference_id")).Position);
        }

        return PlaneView.PointOf(near);
    }

    private static SpotRequirements Requirements(JObject? spec)
    {
        Args require = new Args(spec ?? new JObject());
        return new SpotRequirements(require.OptionalBool("one_section") ?? true,
            require.OptionalDouble("min_bottom_above_floor_m"), require.OptionalBool("ports_reachable") ?? false,
            require.OptionalBool("avoid_doors") ?? true, require.OptionalDouble("front_clear_m") ?? 0.0,
            require.OptionalBool("no_visual_overlap") ?? true);
    }

    // One plane (plane/side or looking) or the walls of a room (room_id): every face of a room cell toward a cell
    // outside the room that carries a wall or window, seen from inside.
    private static List<PlaneView> Planes(Args args, GridFacts facts, out Room? room)
    {
        room = null;
        if (!args.Has("room_id"))
        {
            return new List<PlaneView> { PlaneView.Read(args, facts, out _) };
        }

        if (!ThingId.TryRead(args.Optional("room_id"), out ThingId id))
        {
            throw ApiErrors.InvalidArgument("room_id must be a room id as rooms reports it.");
        }

        Room found = StructureAirRecord.FindRoom(id.Value) ??
                     throw ApiErrors.Refused("room_not_found", $"No room has id {id}.");
        room = found;
        HashSet<GridCell> cells = new HashSet<GridCell>();
        foreach (WorldGrid grid in new List<WorldGrid>(found.Grids))
        {
            cells.Add(Shared.Game.Upgrades.PieceShapes.Cell(grid.Value));
        }

        List<PlaneView> planes = new List<PlaneView>();
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
                    planes.Add(new PlaneView(FacePlane.Of(step.Axis, coordinate), step.Opposite));
                }
            }
        }

        return planes;
    }

    // The turn a spot is tried in. A mounted piece faces out of the plane (or facing), its top up. A grid-placed piece
    // stands: on a floor its top is the plane's side and it faces facing (default +z); against a wall its top is +y
    // and it faces out of the wall (or facing).
    private static CubeRotation TurnFor(Structure prefab, PlaneView plane, GridStep? facing)
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

    private static GridStep Step(string? text, string name) =>
        GridStep.TryParse(text, out GridStep step)
            ? step
            : throw ApiErrors.InvalidArgument($"{name} must be one of +x, -x, +y, -y, +z, -z.");

    private sealed class Candidate
    {
        private Candidate(PlaneView plane, CubeRotation turn, Quaternion rotation, Vector3 position,
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

        internal PlaneView Plane { get; }

        internal CubeRotation Turn { get; }

        internal Quaternion Rotation { get; }

        internal Vector3 Position { get; }

        internal List<GridCell> Cells { get; }

        internal double Distance { get; }

        internal List<string> Failed { get; }

        internal int Penalty { get; private set; }

        internal List<LayoutConflict> Conflicts { get; private set; } = new List<LayoutConflict>();

        internal static Candidate? At(Structure prefab, Structure cursor, PlaneView plane, CubeRotation turn, double u,
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
            double floor = AtResolver.FloorBelow(new Metres(position.x, bottom, position.z), facts);
            SpotGeometry geometry = new SpotGeometry(cells.Count, occupied, keepOut,
                mount?.Faces().Count ?? 0, bottom - floor, FrontBlocked(cells, turn.Forward, require, facts),
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

        /// <summary>The cursor check and the layout preview; false when either rules the spot out.</summary>
        internal bool Check(Structure prefab, Structure cursor, GridFacts facts, SpotRequirements require)
        {
            if (CursorCheck.Refusal(cursor, Position, Rotation, new HashSet<long>()) != null)
            {
                return false;
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
                    return false;
                }
            }

            if (require.PortsReachable && layout.View.PortChecks != null &&
                layout.View.PortChecks.Exists(port => port.Blocked != null && !port.Joins))
            {
                return false;
            }

            return true;
        }

        internal SpotView View(Structure prefab)
        {
            JObject place = new JObject
            {
                ["prefab"] = prefab.PrefabName,
                ["at"] = new JArray(Round(Position.x), Round(Position.y), Round(Position.z)),
                ["facing"] = Turn.Forward.Name,
                ["up"] = Turn.Up.Name
            };
            return new SpotView(PointView.Of(Bodies.V(Position)), OrientationView.Of(Turn),
                System.Math.Round(Distance, 2), Penalty, $"{Plane.Plane} seen from {Plane.Side.Name}",
                Conflicts.ConvertAll(conflict => new ConflictView(conflict)), place);
        }

        private static double Round(float value) => System.Math.Round(value * 100.0) / 100.0;
    }
}
