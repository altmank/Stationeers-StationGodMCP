#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Build;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Shared.Game.Structures;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api;

/// <summary>
/// lint_layout: the layout rules over a room (room_id) or a box (min, max), from what stands there now: runs floating
/// in air or across a window's face, runs and device ports in a door's keep-out, a device port whose cell holds a piece of its kind not joined to
/// it (another network's), runs hugging a door's jambs, mounted devices crossing a wall seam or facing out of the room,
/// devices whose bodies run into each other, and controls not on a wall. Read only.
/// </summary>
internal static class LintLayoutApi
{
    private const int DefaultLimit = 100;
    private const int MaximumLimit = 500;
    private const long MaximumCells = 4000;
    private const int PortTypes = (int)(NetworkType.PowerAndData | NetworkType.Pipe | NetworkType.PipeLiquid |
                                        NetworkType.Chute);
    private static readonly CableFamily Cables = new CableFamily();
    private static readonly PipeFamily Pipes = new PipeFamily();
    private static readonly ChuteFamily Chutes = new ChuteFamily();

    internal static LintLayoutView Handle(Args args)
    {
        GridFacts facts = new GridFacts(new CableRunKind(), SmallGridBlock.None, new HashSet<long>());
        List<GridCell> region = Region(args, out string described);
        Dictionary<long, SmallGrid> pieces = new Dictionary<long, SmallGrid>();
        Dictionary<long, SmallGrid> devices = new Dictionary<long, SmallGrid>();
        Dictionary<long, Structure> doors = new Dictionary<long, Structure>();
        foreach (GridCell large in region)
        {
            for (int index = 0; index < SmallCellCode.PerCell; index++)
            {
                SmallCell? small = facts.SmallAt(SmallCellCode.SmallAt(large, index));
                if (small == null)
                {
                    continue;
                }

                Add(pieces, small.Cable);
                Add(pieces, small.Pipe);
                Add(pieces, small.Chute);
                Add(devices, small.Device);
                Add(devices, small.Other);
            }

            foreach (GridStep face in GridStep.All)
            {
                foreach (Structure structure in facts.FaceStructures(large, face))
                {
                    if (Openings.IsDoor(structure))
                    {
                        doors[structure.ReferenceId] = structure;
                    }
                }
            }
        }

        List<LintFinding> findings = new List<LintFinding>();
        Runs(pieces.Values, facts, findings);
        foreach (Structure door in doors.Values)
        {
            AlongDoor(door, pieces.Values, facts, findings);
        }

        List<SmallGrid> deviceList = new List<SmallGrid>(devices.Values);
        deviceList.Sort(static (a, b) => a.ReferenceId.CompareTo(b.ReferenceId));
        foreach (SmallGrid device in deviceList)
        {
            Mounting(device, facts, findings);
            Ports(device, facts, findings);
        }

        Overlaps(deviceList, findings);
        List<LintFinding> ordered = LintReport.Ordered(findings);
        int limit = args.OptionalInt("limit", 1, MaximumLimit) ?? DefaultLimit;
        List<LintFindingView> views = new List<LintFindingView>();
        for (int index = 0; index < ordered.Count && index < limit; index++)
        {
            views.Add(new LintFindingView(ordered[index]));
        }

        return new LintLayoutView(described, region.Count, pieces.Count, devices.Count, doors.Count,
            LintReport.Counts(findings), views, ordered.Count);
    }

    private static List<GridCell> Region(Args args, out string described)
    {
        if (args.Has("room_id") == (args.Has("min") || args.Has("max")))
        {
            throw ApiErrors.InvalidArgument("Pass room_id (from rooms) or min and max (a box, in metres).");
        }

        if (args.Has("room_id"))
        {
            if (!ThingId.TryRead(args.Optional("room_id"), out ThingId id))
            {
                throw ApiErrors.InvalidArgument("room_id must be a room id as rooms reports it.");
            }

            Room room = StructureAirRecord.FindRoom(id.Value) ??
                        throw ApiErrors.Refused("room_not_found", $"No room has id {id}.");
            List<GridCell> cells = new List<GridCell>();
            foreach (WorldGrid grid in new List<WorldGrid>(room.Grids))
            {
                cells.Add(PieceShapes.Cell(grid.Value));
            }

            described = $"room {id} ({cells.Count} cells)";
            return cells;
        }

        Vec3 min = PointOf(args.Optional("min")!, "min");
        Vec3 max = PointOf(args.Optional("max") ?? throw ApiErrors.InvalidArgument("Pass max too."), "max");
        long count = LargeCells.CountInBox(min, max);
        if (count > MaximumCells)
        {
            throw ApiErrors.InvalidArgument($"The box holds {count} 2 m cells; at most {MaximumCells}.");
        }

        described = $"box {min} to {max}";
        return LargeCells.InBox(min, max);
    }

    private static Vec3 PointOf(JToken token, string name)
    {
        Metres point = BuildArgs.PositionOf(token, name);
        return new Vec3(point.X, point.Y, point.Z);
    }

    // floating_run and run_in_door_keepout, one finding per piece.
    private static void Runs(IEnumerable<SmallGrid> pieces, GridFacts facts, List<LintFinding> findings)
    {
        foreach (SmallGrid piece in pieces)
        {
            IReadOnlyList<GridCell> cells = PieceShapes.Live(piece).Cells;
            int air = 0;
            long? door = null;
            long? window = null;
            foreach (GridCell cell in cells)
            {
                air += facts.Support(cell) == CellSupport.Air ? 1 : 0;
                OpeningZone zone = facts.Opening(cell);
                door ??= zone.IsDoor ? zone.Id : (long?)null;
                window ??= zone.IsWindow ? zone.Id : (long?)null;
            }

            Vec3 at = Bodies.V(piece.Position);
            if (air > 0 && IsRunPiece(piece))
            {
                findings.Add(new LintFinding(LintCodes.FloatingRun,
                    $"{piece.PrefabName} {piece.ReferenceId} floats in air ({air} of {cells.Count} cells on no frame " +
                    "and no wall plane).", piece.ReferenceId, at));
            }

            if (window.HasValue)
            {
                findings.Add(new LintFinding(LintCodes.RunCrossesWindow,
                    $"{piece.PrefabName} {piece.ReferenceId} runs across the face of window {window}.",
                    piece.ReferenceId, at, window));
            }

            if (door.HasValue)
            {
                findings.Add(new LintFinding(LintCodes.RunInDoorKeepOut,
                    $"{piece.PrefabName} {piece.ReferenceId} stands in the keep-out of door {door}.", piece.ReferenceId,
                    at, door));
            }
        }
    }

    // A cable, pipe or chute piece a run is made of; not an in-line tank or passive vent that stands in a pipe's slot.
    private static bool IsRunPiece(SmallGrid piece) =>
        Cables.IsPiece(piece) || Pipes.IsPiece(piece) || Chutes.IsPiece(piece);

    // run_along_door: a run cell on the door's plane band just outside its side edges (hugging a jamb), within its
    // height. Doors on floors or ceilings have no jambs and are skipped.
    private static void AlongDoor(Structure door, IEnumerable<SmallGrid> pieces, GridFacts facts,
        List<LintFinding> findings)
    {
        List<GridCell> faces = Openings.FacesOf(door);
        if (faces.Count == 0)
        {
            return;
        }

        int axis = FacePoints.AxisOf(faces[0]);
        if (axis == 1)
        {
            return;
        }

        int side = axis == 0 ? 2 : 0;
        int plane = FacePlane.Component(faces[0], axis);
        int sideMin = int.MaxValue, sideMax = int.MinValue, yMin = int.MaxValue, yMax = int.MinValue;
        foreach (GridCell face in faces)
        {
            sideMin = System.Math.Min(sideMin, FacePlane.Component(face, side) - 10);
            sideMax = System.Math.Max(sideMax, FacePlane.Component(face, side) + 10);
            yMin = System.Math.Min(yMin, face.Y - 10);
            yMax = System.Math.Max(yMax, face.Y + 10);
        }

        int reach = facts.Band.Cells * GridStep.CellSize;
        foreach (SmallGrid piece in pieces)
        {
            foreach (GridCell cell in PieceShapes.Live(piece).Cells)
            {
                int across = FacePlane.Component(cell, side);
                bool hugs = System.Math.Abs(FacePlane.Component(cell, axis) - plane) <= reach &&
                            cell.Y >= yMin && cell.Y <= yMax &&
                            (across == sideMin - GridStep.CellSize || across == sideMax + GridStep.CellSize) &&
                            facts.Visibility(cell) != CellVisibility.Inside;
                if (hugs)
                {
                    findings.Add(new LintFinding(LintCodes.RunAlongDoor,
                        $"{piece.PrefabName} {piece.ReferenceId} runs along the jamb of door {door.ReferenceId} " +
                        $"({door.DisplayName}).", piece.ReferenceId, Bodies.V(piece.Position), door.ReferenceId));
                    break;
                }
            }
        }
    }

    // device_crosses_seam, mounted_faces_out_of_room and controls_not_on_wall.
    private static void Mounting(SmallGrid device, GridFacts facts, List<LintFinding> findings)
    {
        List<GridCell> cells = Bodies.SmallCells(device);
        Quaternion rotation = device.ThingTransformRotation;
        CubeRotation? turn = CubeRotation.FromQuaternion(rotation.x, rotation.y, rotation.z, rotation.w);
        if (cells.Count == 0 || turn == null)
        {
            return;
        }

        Vec3 at = Bodies.V(device.Position);
        bool mounted = device.PlacementType == PlacementSnap.FaceMount;
        MountRect? mount = MountRect.Of(Box3.OfSmallCells(cells), mounted ? turn.Forward : turn.Up,
            Bodies.RenderBox(device));
        if (mounted && mount != null && mount.CrossesSeam)
        {
            findings.Add(new LintFinding(LintCodes.DeviceCrossesSeam,
                $"{device.DisplayName} ({device.PrefabName} {device.ReferenceId}) spans {mount.Faces().Count} wall " +
                $"sections on {mount.Plane}.", device.ReferenceId, at));
        }

        if (mounted && mount != null)
        {
            Vec3 centre = Box3.OfSmallCells(cells).Centre.With(mount.Plane.Axis, mount.Plane.Metres);
            Vec3 step = Vec3.Of(mount.Outward);
            bool front = facts.RoomAt(PlaneView.LargeAt(centre + step * 0.6)) != null;
            bool back = facts.RoomAt(PlaneView.LargeAt(centre - step * 0.6)) != null;
            if (back && !front)
            {
                findings.Add(new LintFinding(LintCodes.MountedFacesOutOfRoom,
                    $"{device.DisplayName} ({device.PrefabName} {device.ReferenceId}) faces {mount.Outward.Name}, " +
                    "out of the room behind it.", device.ReferenceId, at));
            }
        }

        if (Controls.Has(device.PrefabName) && (mount == null || !mounted || mount.Outward.IsVertical))
        {
            findings.Add(new LintFinding(LintCodes.ControlsNotOnWall,
                $"{device.DisplayName} ({device.PrefabName} {device.ReferenceId}) has controls and is not mounted on " +
                "a wall.", device.ReferenceId, at));
        }
    }

    // port_into_doorway and port_cell_foreign_network.
    private static void Ports(SmallGrid device, GridFacts facts, List<LintFinding> findings)
    {
        if (device.OpenEnds == null)
        {
            return;
        }

        GridController world = GridController.World;
        for (int index = 0; index < device.OpenEnds.Count; index++)
        {
            Connection end = device.OpenEnds[index];
            if (end?.Transform == null || ((int)end.ConnectionType & PortTypes) == 0)
            {
                continue;
            }

            GridCell joining = PieceShapes.Cell(end.GetLocalGrid());
            Vec3 at = Bodies.V(PieceShapes.CentreOf(joining));
            OpeningZone zone = facts.Opening(joining);
            if (zone.IsDoor)
            {
                findings.Add(new LintFinding(LintCodes.PortIntoDoorway,
                    $"{device.DisplayName} ({device.ReferenceId}) port {index} ({end.ConnectionType}) joins in the " +
                    $"keep-out of door {zone.Id}.", device.ReferenceId, at, zone.Id));
            }

            SmallCell? cell = world.GetSmallCell(end.GetLocalGrid());
            if (cell == null)
            {
                continue;
            }

            SmallGrid? piece = (end.ConnectionType & (NetworkType.Pipe | NetworkType.PipeLiquid)) != 0 ? cell.Pipe
                : (end.ConnectionType & NetworkType.Chute) != 0 ? cell.Chute
                : (SmallGrid?)cell.Cable;
            if (piece == null || piece.IsBeingDestroyed || piece.IsConnected(end))
            {
                continue;
            }

            findings.Add(new LintFinding(LintCodes.PortCellForeignNetwork,
                $"{device.DisplayName} ({device.ReferenceId}) port {index} ({end.ConnectionType}): its joining cell " +
                $"holds {piece.PrefabName} {piece.ReferenceId}, which does not join it, so nothing can.",
                device.ReferenceId, at, piece.ReferenceId));
        }
    }

    // device_visual_overlap: mesh boxes clashing by more than the tolerance (VisualClash: one body's mesh over the
    // other's is enough, a device under a console's overhang included), once per pair; things sharing a small cell (a
    // device on a pipe) are skipped.
    private static void Overlaps(List<SmallGrid> devices, List<LintFinding> findings)
    {
        List<Box3> boxes = devices.ConvertAll(device => Bodies.RenderBox(device));
        List<HashSet<GridCell>> cells = devices.ConvertAll(device => new HashSet<GridCell>(Bodies.SmallCells(device)));
        for (int a = 0; a < devices.Count; a++)
        {
            for (int b = a + 1; b < devices.Count; b++)
            {
                double depth = VisualClash.Depth(boxes[a], boxes[b]);
                if (depth <= ConflictCodes.OverlapToleranceM || cells[a].Overlaps(cells[b]))
                {
                    continue;
                }

                string how = double.IsPositiveInfinity(depth)
                    ? "overlap: one lies inside the other"
                    : System.FormattableString.Invariant($"run {depth:0.00} m into each other");
                findings.Add(new LintFinding(LintCodes.DeviceVisualOverlap,
                    $"{devices[a].DisplayName} ({devices[a].ReferenceId}) and {devices[b].DisplayName} " +
                    $"({devices[b].ReferenceId}) {how}.", devices[a].ReferenceId, Bodies.V(devices[a].Position),
                    devices[b].ReferenceId));
            }
        }
    }

    private static void Add(Dictionary<long, SmallGrid> things, SmallGrid? thing)
    {
        if (thing != null && !thing.IsBeingDestroyed)
        {
            things[thing.ReferenceId] = thing;
        }
    }
}
