#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using Objects.Structures;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game.Build;

/// <summary>A layout preview: the view, and every finding also as a conflict the planner turns into issues.</summary>
internal sealed class LayoutPreview
{
    internal LayoutPreview(PlacementLayoutView view, List<LayoutConflict> conflicts, MountRect? mount,
        List<GridCell> smallCells, Box3 render)
    {
        View = view;
        Conflicts = conflicts;
        Mount = mount;
        SmallCells = smallCells;
        Render = render;
    }

    internal PlacementLayoutView View { get; }

    internal List<LayoutConflict> Conflicts { get; }

    internal MountRect? Mount { get; }

    internal List<GridCell> SmallCells { get; }

    internal Box3 Render { get; }

    /// <summary>A score for choosing among candidates: problems weigh most, then warnings; lower is better.</summary>
    internal int Penalty
    {
        get
        {
            int penalty = 0;
            foreach (LayoutConflict conflict in Conflicts)
            {
                penalty += conflict.Level == ConflictLevel.Problem ? 100
                    : conflict.Level == ConflictLevel.Warning ? 10
                    : 1;
            }

            return penalty;
        }
    }
}

/// <summary>
/// place_structure's layout preview of one placement, from the game's own data for the prefab at that position and
/// turn: the small cells it would be registered in (GridBounds, as the game's cursor reads them), its render box
/// (Thing.Bounds), the face plane it rests on and the 2 m wall sections it spans there, and the conflicts with what
/// stands around it now: visual_overlap (render boxes run into each other by more than 0.1 m; neighbours flush on a
/// wall only touch), crosses_section_seam, in_door_keepout, crosses_window, blocks_route_cells (it would stand in the
/// cell a free port of a device beside it needs), front_blocked, faces_out_of_room, not_upright; and each port checked
/// against its joining cell (what stands there, whether it joins on build, which network). Read only.
/// </summary>
internal static class PlacementLayout
{
    private const int PortTypes = (int)(NetworkType.PowerAndData | NetworkType.Pipe | NetworkType.PipeLiquid |
                                        NetworkType.Chute);

    private const int ListedCells = 64;
    private const int MaximumScannedCells = 4096;

    internal static LayoutPreview Of(Structure prefab, Vector3 position, Quaternion rotation, CubeRotation? turn,
        GridFacts facts, bool allowDoorKeepOut, HashSet<long> ignore)
    {
        List<GridCell> small = Bodies.SmallCells(prefab, position, rotation);
        List<GridCell> large = prefab.PlacementType == PlacementSnap.Grid && !(prefab is SmallGrid)
            ? Bodies.LargeCells(prefab, position, rotation)
            : new List<GridCell>();
        Box3 render = Bodies.RenderBox(prefab, position, rotation);
        MountRect? mount = small.Count > 0 && turn != null ? MountOf(prefab, small, turn) : null;
        List<LayoutConflict> conflicts = new List<LayoutConflict>();
        SectionsView? sections = mount != null ? Sections(mount, facts, conflicts) : null;
        HashSet<GridCell> own = new HashSet<GridCell>(small);
        if (prefab is SmallGrid && !Openings.IsDoor(prefab))
        {
            OpeningConflicts(small, facts, allowDoorKeepOut, conflicts);
        }

        if (small.Count > 0)
        {
            Surroundings(render, own, ignore, facts, conflicts);
        }

        if (mount != null && prefab.PlacementType == PlacementSnap.FaceMount)
        {
            FrontAndRoom(mount, small, own, facts, conflicts);
        }

        if (turn != null && prefab is Device)
        {
            VisualUp up = VisualUp.Of(prefab.PrefabName);
            string? upright = Uprightness.Problem(turn, up.LocalUp);
            if (upright != null)
            {
                conflicts.Add(new LayoutConflict(ConflictCodes.NotUpright,
                    up.LyingAllowed ? ConflictLevel.Info : ConflictLevel.Warning,
                    $"{prefab.PrefabName} would not stand upright: {upright}" +
                    (up.LyingAllowed ? " (built lying down is normal for it)." : ".")));
            }
        }

        List<PortCheckView>? ports = PortChecks(prefab, position, rotation, own, facts, ignore);
        FootprintView footprint = new FootprintView(new CellListView(small, ListedCells),
            large.ConvertAll(cell => GameLookup.ViewOf(PieceShapes.CentreOf(cell))),
            Bodies.ViewOf(prefab, position, rotation), mount != null ? new MountView(mount) : null);
        PlacementLayoutView view = new PlacementLayoutView(footprint, sections,
            conflicts.ConvertAll(conflict => new ConflictView(conflict)), ports);
        return new LayoutPreview(view, conflicts, mount, small, render);
    }

    // The plane behind it: behind its back for a mounted piece (placement face_mount), under its bottom otherwise.
    private static MountRect? MountOf(Structure prefab, List<GridCell> small, CubeRotation turn) =>
        MountRect.Of(Box3.OfSmallCells(small),
            prefab.PlacementType == PlacementSnap.FaceMount ? turn.Forward : turn.Up);

    private static SectionsView Sections(MountRect mount, GridFacts facts, List<LayoutConflict> conflicts)
    {
        List<SectionWallView> walls = new List<SectionWallView>();
        List<GridCell> faces = mount.Faces();
        foreach (GridCell face in faces)
        {
            List<Structure> structures = facts.FaceStructuresAt(face);
            if (structures.Count == 0)
            {
                walls.Add(new SectionWallView(PointView.OfCell(face), null, "none"));
            }

            foreach (Structure structure in structures)
            {
                OpeningKind kind = Openings.KindOf(structure);
                walls.Add(new SectionWallView(PointView.OfCell(face), GameLookup.ViewOf(structure),
                    kind.ToString().ToLowerInvariant()));
                if (kind == OpeningKind.Window)
                {
                    conflicts.Add(new LayoutConflict(ConflictCodes.CrossesWindow, ConflictLevel.Warning,
                        $"It rests on window {structure.ReferenceId} ({structure.PrefabName}).",
                        structure.ReferenceId));
                }
            }
        }

        bool crosses = faces.Count > 1;
        if (crosses)
        {
            conflicts.Add(new LayoutConflict(ConflictCodes.CrossesSeam, ConflictLevel.Warning,
                $"It spans {faces.Count} wall sections on {mount.Plane} ({mount}): it crosses the seam between " +
                "them. Shift it so it covers one 2 m section."));
        }

        return new SectionsView(walls, crosses);
    }

    private static void OpeningConflicts(List<GridCell> small, GridFacts facts, bool allow,
        List<LayoutConflict> conflicts)
    {
        long? door = null;
        long? window = null;
        int inKeepOut = 0;
        foreach (GridCell cell in small)
        {
            OpeningZone zone = facts.Opening(cell);
            if (zone.IsDoor)
            {
                door ??= zone.Id;
                inKeepOut++;
            }
            else if (zone.IsWindow)
            {
                window ??= zone.Id;
            }
        }

        if (door.HasValue)
        {
            conflicts.Add(new LayoutConflict(ConflictCodes.InDoorKeepOut,
                allow ? ConflictLevel.Warning : ConflictLevel.Problem,
                $"{inKeepOut} of its cells stand in the keep-out of door {door} (its face and {facts.Band.Metres} m " +
                "either side, inside its rectangle)" +
                (allow ? "; allowed (allow_door_keepout)." : "; move it, or pass allow_door_keepout."), door));
        }

        if (window.HasValue)
        {
            conflicts.Add(new LayoutConflict(ConflictCodes.CrossesWindow, ConflictLevel.Warning,
                $"It stands on the face of window {window}.", window));
        }
    }

    // Things near it: whose render box it runs into (visual_overlap) and whose free port cell it would take
    // (blocks_route_cells). A thing sharing one of its cells stands there by design (a device on a pipe) and is skipped.
    private static void Surroundings(Box3 render, HashSet<GridCell> own, HashSet<long> ignore,
        GridFacts facts, List<LayoutConflict> conflicts)
    {
        Dictionary<long, SmallGrid> near = new Dictionary<long, SmallGrid>();
        int scanned = 0;
        for (int x = Floor(render.Min.X - 0.5); x <= Ceil(render.Max.X + 0.5); x += GridStep.CellSize)
        {
            for (int y = Floor(render.Min.Y - 0.5); y <= Ceil(render.Max.Y + 0.5); y += GridStep.CellSize)
            {
                for (int z = Floor(render.Min.Z - 0.5); z <= Ceil(render.Max.Z + 0.5); z += GridStep.CellSize)
                {
                    if (++scanned > MaximumScannedCells)
                    {
                        break;
                    }

                    SmallCell? cell = facts.SmallAt(new GridCell(x, y, z));
                    if (cell == null)
                    {
                        continue;
                    }

                    Add(near, cell.Device, ignore);
                    Add(near, cell.Other, ignore);
                    Add(near, cell.Pipe, ignore);
                    Add(near, cell.Cable, ignore);
                    Add(near, cell.Chute, ignore);
                }
            }
        }

        foreach (SmallGrid thing in near.Values)
        {
            List<GridCell> cells = Bodies.SmallCells(thing);
            if (cells.Exists(own.Contains))
            {
                continue;
            }

            Box3 box = Bodies.RenderBox(thing);
            double depth = render.Penetration(box);
            if (depth > ConflictCodes.OverlapToleranceM)
            {
                conflicts.Add(new LayoutConflict(ConflictCodes.VisualOverlap, ConflictLevel.Warning,
                    $"Its body runs {depth:0.00} m into {thing.DisplayName} ({thing.PrefabName} {thing.ReferenceId}) " +
                    "at " + box + ".", thing.ReferenceId));
            }

            if (thing is Device device)
            {
                FreePortsTaken(device, own, conflicts);
            }
        }
    }


    private static void FreePortsTaken(Device device, HashSet<GridCell> own, List<LayoutConflict> conflicts)
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
            if (own.Contains(joining) && PieceFor(world.GetSmallCell(end.GetLocalGrid()), (int)end.ConnectionType) == null)
            {
                conflicts.Add(new LayoutConflict(ConflictCodes.BlocksRouteCells, ConflictLevel.Warning,
                    $"It would stand in the joining cell {PieceShapes.CentreOf(joining)} of {device.DisplayName}'s " +
                    $"free port {index} ({end.ConnectionType}), which then cannot be connected.", device.ReferenceId));
            }
        }
    }

    // In front of a mounted piece: the small cells one step out, and the rooms in front and behind.
    private static void FrontAndRoom(MountRect mount, List<GridCell> small, HashSet<GridCell> own, GridFacts facts,
        List<LayoutConflict> conflicts)
    {
        GridStep outward = mount.Outward;
        foreach (GridCell cell in small)
        {
            GridCell front = outward.From(cell);
            if (own.Contains(front))
            {
                continue;
            }

            SmallOccupancy occupancy = facts.Occupancy(front);
            SmallCell? there = facts.SmallAt(front);
            if (occupancy.Device || occupancy.Other || occupancy.Chute)
            {
                SmallGrid? thing = Blocker(there);
                conflicts.Add(new LayoutConflict(ConflictCodes.FrontBlocked, ConflictLevel.Warning,
                    $"Right in front of it, at {PieceShapes.CentreOf(front)}, stands " +
                    $"{(thing != null ? $"{thing.DisplayName} ({thing.PrefabName} {thing.ReferenceId})" : "something")}.",
                    thing != null ? thing.ReferenceId : (long?)null));
                break;
            }

            if (facts.Visibility(front) == CellVisibility.Inside)
            {
                conflicts.Add(new LayoutConflict(ConflictCodes.FrontBlocked, ConflictLevel.Warning,
                    $"Its front faces into a frame's body at {PieceShapes.CentreOf(front)}."));
                break;
            }
        }

        Box3 box = Box3.OfSmallCells(small);
        Vec3 centre = box.Centre.With(mount.Plane.Axis, mount.Plane.Metres);
        Vec3 step = Vec3.Of(outward);
        bool frontRoom = facts.RoomAt(LargeAt(centre + step * 0.6)) != null;
        bool backRoom = facts.RoomAt(LargeAt(centre - step * 0.6)) != null;
        if (backRoom && !frontRoom)
        {
            conflicts.Add(new LayoutConflict(ConflictCodes.FacesOutOfRoom, ConflictLevel.Warning,
                $"It faces {outward.Name}, out of the room behind it: its front is outside (or in no room). Turn it " +
                $"to face {outward.Opposite.Name} and mount it on the room's side of {mount.Plane}."));
        }
    }

    private static List<PortCheckView>? PortChecks(Structure prefab, Vector3 position, Quaternion rotation,
        HashSet<GridCell> own, GridFacts facts, HashSet<long> ignore)
    {
        if (!(prefab is SmallGrid) || new CableFamily().IsPiece(prefab) || new PipeFamily().IsPiece(prefab) ||
            new ChuteFamily().IsPiece(prefab))
        {
            return null;
        }

        PieceModel? model = PieceShapes.Placed(prefab, position, rotation, 0);
        if (model == null || model.Ends.Count == 0)
        {
            return null;
        }

        GridController world = GridController.World;
        List<PortCheckView> checks = new List<PortCheckView>();
        foreach (PortCell port in PortCells.Of(model.Ends, PortTypes))
        {
            SmallCell? cell = world.GetSmallCell(PieceShapes.Grid(port.Cell));
            SmallGrid? piece = PieceFor(cell, port.Type);
            if (piece != null && ignore.Contains(piece.ReferenceId))
            {
                piece = null;
            }

            bool joins = false;
            ThingId? network = null;
            string? blocked = null;
            ThingView? occupant = null;
            if (piece != null)
            {
                occupant = GameLookup.ViewOf(piece);
                joins = port.Toward.HasValue && EndSet.AtCell(PieceShapes.Live(piece), port.Cell).Contains(port.Toward.Value);
                IReferencable? net = piece is Cable cable ? cable.CableNetwork
                    : piece is Pipe pipe ? pipe.PipeNetwork
                    : piece is Chute chute ? chute.ChuteNetwork
                    : null;
                network = joins && net != null ? new ThingId(net.ReferenceId) : (ThingId?)null;
                blocked = joins ? null : $"{piece.PrefabName} {piece.ReferenceId} stands there without an end toward " +
                                         "the port (a place_* run through the cell makes it a junction)";
            }
            else
            {
                SmallGrid? other = Blocker(cell);
                if (other != null && !ignore.Contains(other.ReferenceId))
                {
                    occupant = GameLookup.ViewOf(other);
                    blocked = $"{other.DisplayName} ({other.PrefabName} {other.ReferenceId}) stands in the cell";
                }
                else if (own.Contains(port.Cell))
                {
                    blocked = "its own body takes the cell";
                }
            }

            string role = ((ConnectionRole)port.Role).ToString();
            checks.Add(new PortCheckView(port.Index, GameLookup.ViewOf(PieceShapes.CentreOf(port.Cell)),
                port.Toward?.Name ?? "?", ((NetworkType)port.Type).ToString(), role, PortFlow.Of(role), occupant,
                joins, network, blocked, facts.Opening(port.Cell).IsDoor));
        }

        return checks;
    }

    // What stands in a cell's device, other or chute slot, in that order; null when none (or being destroyed).
    private static SmallGrid? Blocker(SmallCell? cell)
    {
        if (cell == null)
        {
            return null;
        }

        foreach (SmallGrid? slot in new SmallGrid?[] { cell.Device, cell.Other, cell.Chute })
        {
            if (slot != null && !slot.IsBeingDestroyed)
            {
                return slot;
            }
        }

        return null;
    }

    // The piece of a port's kind in a cell: a pipe for pipe ports, a chute for chute ports, else a cable.
    private static SmallGrid? PieceFor(SmallCell? cell, int type)
    {
        if (cell == null)
        {
            return null;
        }

        SmallGrid? piece = (type & (int)(NetworkType.Pipe | NetworkType.PipeLiquid)) != 0 ? cell.Pipe
            : (type & (int)NetworkType.Chute) != 0 ? cell.Chute
            : (SmallGrid?)cell.Cable;
        return piece != null && !piece.IsBeingDestroyed ? piece : null;
    }

    private static void Add(Dictionary<long, SmallGrid> things, SmallGrid? thing, HashSet<long> ignore)
    {
        if (thing != null && !thing.IsBeingDestroyed && !ignore.Contains(thing.ReferenceId))
        {
            things[thing.ReferenceId] = thing;
        }
    }

    private static GridCell LargeAt(Vec3 point) =>
        SmallCellCode.LargeOf(new GridCell((int)System.Math.Round(point.X * 10.0),
            (int)System.Math.Round(point.Y * 10.0), (int)System.Math.Round(point.Z * 10.0)));

    private static int Floor(double metres) => (int)System.Math.Floor(metres * 2.0) * GridStep.CellSize;

    private static int Ceil(double metres) => (int)System.Math.Ceiling(metres * 2.0) * GridStep.CellSize;
}
