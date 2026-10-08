#nullable enable

using System;
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
/// stands around it now: visual_overlap (render boxes run into each other by more than 0.1 m, VisualClash; neighbours
/// flush on a wall only touch), crosses_section_seam (its render box's rectangle spans more than one 2 m section though
/// it is small enough to fit one: MountRect.CrossesAvoidableSeam),
/// in_door_keepout, crosses_window, blocks_route_cells (it would stand in the cell a free port of a device beside it
/// needs), front_blocked, faces_out_of_room, not_upright; and each port checked
/// against its joining cell (what stands there, whether it joins on build, which network). Read only.
/// </summary>
internal static class PlacementLayout
{
    private const int PortTypes = (int)(NetworkType.PowerAndData | NetworkType.Pipe | NetworkType.PipeLiquid |
                                        NetworkType.Chute);

    private const int ListedCells = 64;

    internal static LayoutPreview Of(Structure prefab, Vector3 position, Quaternion rotation, CubeRotation? turn,
        GridFacts facts, bool allowDoorKeepOut, HashSet<long> ignore)
    {
        List<GridCell> small = Bodies.SmallCells(prefab, position, rotation);
        List<GridCell> large = prefab.PlacementType == PlacementSnap.Grid && !(prefab is SmallGrid)
            ? Bodies.LargeCells(prefab, position, rotation)
            : new List<GridCell>();
        Box3 render = Bodies.RenderBox(prefab, position, rotation);
        MountRect? mount = small.Count > 0 && turn != null ? MountOf(prefab, small, turn, render) : null;
        List<LayoutConflict> conflicts = new List<LayoutConflict>();
        SectionsView? sections = mount != null ? Sections(mount, facts, conflicts, !IsRunPiece(prefab)) : null;
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

        if (turn != null)
        {
            ControlsAhead(prefab, turn, mount, small, large, render, facts, ignore, conflicts);
        }

        string? clips = ClipsSurface(prefab, mount, small, render, facts, structure => ignore.Contains(structure.ReferenceId),
            out long? clipped);
        if (clips != null)
        {
            conflicts.Add(new LayoutConflict(ConflictCodes.ClipsSurface, ConflictLevel.Warning,
                $"Its body runs into {clips}: it does not rest on that surface. Turn it to stand on it, or move it off.",
                clipped));
        }

        if (turn != null && prefab is Device)
        {
            VisualUp up = PlacePlanner.VisualUpOf(prefab);
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

    /// <summary>
    /// The way a piece stands out of the surface it rests on: its top for a grid-placed piece (it stands on a floor),
    /// its front for a face-placed or mounted one (its back is on the face). Every placement tool reads it here.
    /// </summary>
    internal static GridStep MountOutward(Structure prefab, CubeRotation turn) =>
        prefab.PlacementType == PlacementSnap.Grid ? turn.Up : turn.Forward;

    // A cable, pipe or chute piece, or a small-grid member of a pipe line that sits in the line (an in-line tank, a
    // passive vent): it rests on no wall section, so crossing a seam means nothing for it (pipes-27, pipes-29).
    private static bool IsRunPiece(Structure prefab) =>
        new CableFamily().IsPiece(prefab) || new PipeFamily().IsPiece(prefab) || new ChuteFamily().IsPiece(prefab) ||
        prefab is InLineTank || prefab is StructureInLineTank || prefab is PassiveVent;

    private static SectionsView Sections(MountRect mount, GridFacts facts, List<LayoutConflict> conflicts,
        bool seamMatters)
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
        if (crosses && seamMatters && mount.FitsOneSection)
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
                System.FormattableString.Invariant($"{inKeepOut} of its cells stand in the keep-out of door {door} (its face and {facts.Band.Metres} m ") +
                "either side, inside its rectangle)" +
                (allow ? "; allowed (allow_door_keepout)." : "; move it, or pass allow_door_keepout."), door));
        }

        // Once per window: the wall sections a mounted piece rests on may have named it already.
        if (window.HasValue && !conflicts.Exists(conflict =>
                conflict.Code == ConflictCodes.CrossesWindow && conflict.OtherId == window))
        {
            conflicts.Add(new LayoutConflict(ConflictCodes.CrossesWindow, ConflictLevel.Warning,
                $"It stands on the face of window {window}.", window));
        }
    }

    // Things near it: whose body it clashes with (visual_overlap, VisualClash on the mesh boxes) and whose free port
    // cell it would take (blocks_route_cells). A thing sharing one of its cells stands there by design (a device on a
    // pipe) and is skipped.
    private static void Surroundings(Box3 render, HashSet<GridCell> own, HashSet<long> ignore, GridFacts facts,
        List<LayoutConflict> conflicts)
    {
        foreach (NearBody body in NearBodies.Around(render, facts, ignore, NearKinds.AnyPiece))
        {
            if (body.Shares(own))
            {
                continue;
            }

            double depth = VisualClash.Depth(render, body.Render);
            if (depth > ConflictCodes.OverlapToleranceM)
            {
                SmallGrid thing = body.Thing;
                conflicts.Add(new LayoutConflict(ConflictCodes.VisualOverlap, ConflictLevel.Warning,
                    $"Its body and {Names.Of(thing)} ({thing.PrefabName} {thing.ReferenceId}, at {body.Render}) " +
                    (double.IsPositiveInfinity(depth)
                        ? "overlap: one lies inside the other."
                        : System.FormattableString.Invariant($"run {depth:0.00} m into each other.")),
                    thing.ReferenceId));
            }

            if (body.Thing is Device device)
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
                    $"It would stand in the joining cell {PieceShapes.CentreOf(joining)} of {Names.Of(device)}'s " +
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
                    $"{(thing != null ? $"{Names.Of(thing)} ({thing.PrefabName} {thing.ReferenceId})" : "something")}.",
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

    /// <summary>
    /// What stands right in front of the side its controls face (PrefabControls): another device or small-grid thing,
    /// a chute, a frame's body, a frame or a wall on the plane in front of it. controls_blocked warns (info only when
    /// the side is the forward fallback). A face-mounted piece whose controls face its front is front_blocked's.
    /// </summary>
    private static void ControlsAhead(Structure prefab, CubeRotation turn, MountRect? mount, List<GridCell> small,
        List<GridCell> large, Box3 render, GridFacts facts, HashSet<long> ignore, List<LayoutConflict> conflicts)
    {
        string? blocked = ControlsBlockedBy(prefab, turn, mount, small, large, render, facts, ignore, _ => false,
            out ControlFace? controls, out long? id);
        if (blocked != null && controls != null)
        {
            conflicts.Add(new LayoutConflict(ConflictCodes.ControlsBlocked,
                controls.Fallback ? ConflictLevel.Info : ConflictLevel.Warning,
                $"The side its controls face, {turn.Turn(controls.Local).Name} ({controls.Source}), has {blocked} right " +
                "in front of it.", id));
        }
    }

    /// <summary>
    /// What stands right in front of the side the prefab's controls face at this turn (PrefabControls), as text, with
    /// its id; null when nothing does, when it has no control face, or when the side is a face-mounted piece's front
    /// (front_blocked's). small and large are the cells it registers in; the thing itself is never in front of itself.
    /// First the cell in front (a thing in it, a wall or frame on the plane, a frame's body), then the mesh boxes
    /// (ControlsReach): a plate, frame or thing within ControlsReach.ClearanceM of the side, or across it (controls sunk
    /// in a floor), covering a quarter of it. render is its mesh box; ignore and skip: things a plan removes.
    /// </summary>
    internal static string? ControlsBlockedBy(Structure prefab, CubeRotation turn, MountRect? mount,
        List<GridCell> small, List<GridCell> large, Box3 render, GridFacts facts, HashSet<long> ignore,
        Func<Structure, bool> skip, out ControlFace? controls, out long? id)
    {
        id = null;
        controls = PrefabControls.Of(prefab);
        if (controls == null)
        {
            return null;
        }

        GridStep face = turn.Turn(controls.Local);
        if (mount != null && prefab.PlacementType == PlacementSnap.FaceMount && face.Index == mount.Outward.Index)
        {
            return null;
        }

        string? ahead = small.Count > 0 ? SmallAhead(face, small, facts, ref id)
            : large.Count > 0 ? LargeAhead(face, large, facts, ref id)
            : null;
        return ahead ?? MeshAhead(face, mount, small, render, facts, ignore, skip, ref id);
    }

    private static string? MeshAhead(GridStep face, MountRect? mount, List<GridCell> small, Box3 render,
        GridFacts facts, HashSet<long> ignore, Func<Structure, bool> skip, ref long? id)
    {
        Vec3 reach = Vec3.Of(face) * ControlsReach.ClearanceM;
        Box3 room = new Box3(Vec3Min(render.Min, render.Min + reach), Vec3Max(render.Max, render.Max + reach));
        List<Solid> solids = NearSolids.Around(room, facts, new HashSet<GridCell>(small), ignore, skip);
        if (!(ControlsReach.Blocker(render, face, solids, mount) is (Solid solid, double distance)))
        {
            return null;
        }

        id = solid.Id;
        return ControlsReach.Describe(solid, distance, render);
    }

    /// <summary>
    /// clips_surface (SurfaceClip): the plate or frame a small-grid device's mesh box runs into, other than the surface
    /// it rests on (its mount plane), as text with its id; null when none, and for anything but a small-grid device
    /// (a piece, an in-line tank or a passive vent lies in its run; 2 m structures fill their cells).
    /// </summary>
    internal static string? ClipsSurface(Structure prefab, MountRect? mount, List<GridCell> small, Box3 render,
        GridFacts facts, Func<Structure, bool> skip, out long? id)
    {
        id = null;
        if (!(prefab is Device) || small.Count == 0 || IsRunPiece(prefab))
        {
            return null;
        }

        if (!(SurfaceClip.First(render, mount, NearSolids.Surfaces(render, facts, skip)) is (Solid solid, double depth)))
        {
            return null;
        }

        id = solid.Id;
        return SurfaceClip.Describe(solid, depth, render);
    }

    private static Vec3 Vec3Min(Vec3 a, Vec3 b) =>
        new Vec3(System.Math.Min(a.X, b.X), System.Math.Min(a.Y, b.Y), System.Math.Min(a.Z, b.Z));

    private static Vec3 Vec3Max(Vec3 a, Vec3 b) =>
        new Vec3(System.Math.Max(a.X, b.X), System.Math.Max(a.Y, b.Y), System.Math.Max(a.Z, b.Z));

    /// <summary>The mount rectangle a prefab at a turn has over its small cells and render box (null when it has none).</summary>
    internal static MountRect? MountOf(Structure prefab, List<GridCell> small, CubeRotation turn, Box3 render) =>
        MountRect.Of(Box3.OfSmallCells(small), MountOutward(prefab, turn), render);

    private static string? SmallAhead(GridStep face, List<GridCell> small, GridFacts facts, ref long? id)
    {
        HashSet<GridCell> own = new HashSet<GridCell>(small);
        foreach (GridCell cell in small)
        {
            GridCell front = face.From(cell);
            if (own.Contains(front))
            {
                continue;
            }

            string? found = ThingIn(front, facts, ref id) ?? PlaneIn(front, face, facts, ref id);
            if (found != null)
            {
                return found;
            }

            if (facts.Visibility(front) == CellVisibility.Inside)
            {
                return $"a frame's body (at {PieceShapes.CentreOf(front)})";
            }
        }

        return null;
    }

    private static string? LargeAhead(GridStep face, List<GridCell> large, GridFacts facts, ref long? id)
    {
        HashSet<GridCell> own = new HashSet<GridCell>(large);
        int nearIndex = face.Index % 2 == 0 ? 0 : SmallCellCode.PerAxis - 1;
        int axis = face.Index / 2;
        foreach (GridCell cell in large)
        {
            GridCell next = face.From(cell, SmallCellCode.PerAxis);
            if (own.Contains(next))
            {
                continue;
            }

            string? wall = WallOn(facts.FaceStructures(cell, face), ref id);
            if (wall != null)
            {
                return wall;
            }

            Frame? frame = facts.FrameAt(next);
            if (frame != null)
            {
                id = frame.ReferenceId;
                return $"a frame ({frame.PrefabName} {frame.ReferenceId})";
            }

            for (int index = 0; index < SmallCellCode.PerCell; index++)
            {
                GridCell nearSmall = SmallCellCode.SmallAt(next, index);
                if (SmallCellCode.IndexOnAxis(Along(nearSmall, axis)) == nearIndex)
                {
                    string? thing = ThingIn(nearSmall, facts, ref id);
                    if (thing != null)
                    {
                        return thing;
                    }
                }
            }
        }

        return null;
    }

    private static string? ThingIn(GridCell small, GridFacts facts, ref long? id)
    {
        SmallOccupancy occupancy = facts.Occupancy(small);
        if (!occupancy.Device && !occupancy.Other && !occupancy.Chute)
        {
            return null;
        }

        SmallGrid? thing = Blocker(facts.SmallAt(small));
        id = thing != null ? thing.ReferenceId : (long?)null;
        return thing != null ? $"{Names.Of(thing)} ({thing.PrefabName} {thing.ReferenceId})" : "something";
    }

    // A small cell on the plane across the face's axis: a wall on that plane, or a frame on its far side.
    private static string? PlaneIn(GridCell small, GridStep face, GridFacts facts, ref long? id)
    {
        int axis = face.Index / 2;
        if (SmallCellCode.IndexOnAxis(Along(small, axis)) != 0)
        {
            return null;
        }

        GridCell above = SmallCellCode.LargeOf(small);
        GridStep minus = GridStep.All[axis * 2 + 1];
        string? wall = WallOn(facts.FaceStructures(above, minus), ref id);
        if (wall != null)
        {
            return wall;
        }

        Frame? frame = facts.FrameAt(face.Index % 2 == 0 ? above : minus.From(above, SmallCellCode.PerAxis));
        if (frame == null)
        {
            return null;
        }

        id = frame.ReferenceId;
        return $"a frame ({frame.PrefabName} {frame.ReferenceId})";
    }

    private static string? WallOn(List<Structure> structures, ref long? id)
    {
        foreach (Structure structure in structures)
        {
            if (!Openings.IsDoor(structure))
            {
                id = structure.ReferenceId;
                return $"a wall ({structure.PrefabName} {structure.ReferenceId})";
            }
        }

        return null;
    }

    private static int Along(GridCell cell, int axis) => axis == 0 ? cell.X : axis == 1 ? cell.Y : cell.Z;

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
                    blocked = $"{Names.Of(other)} ({other.PrefabName} {other.ReferenceId}) stands in the cell";
                }
                else if (own.Contains(port.Cell))
                {
                    blocked = "its own body takes the cell";
                }
            }

            string role = ((ConnectionRole)port.Role).ToString();
            checks.Add(new PortCheckView(port.Index, GameLookup.ViewOf(PieceShapes.CentreOf(port.Cell)),
                port.Toward?.Name ?? "?", ((NetworkType)port.Type).ToString(), role, TwoWayChutePorts.FlowOf(prefab, port.Type, role), occupant,
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

    private static GridCell LargeAt(Vec3 point) =>
        SmallCellCode.LargeOf(new GridCell((int)System.Math.Round(point.X * 10.0),
            (int)System.Math.Round(point.Y * 10.0), (int)System.Math.Round(point.Z * 10.0)));
}
