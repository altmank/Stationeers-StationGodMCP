#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;

namespace StationGodMCP.Pure;

/// <summary>What a solid near a body is: a wall, floor or ceiling plate on a face plane, a frame's body, another thing.</summary>
internal enum SolidKind
{
    Plate,
    Frame,
    Body
}

/// <summary>
/// Something solid near a body, as the box its mesh fills (Thing.Bounds; a frame's 2 m cell): a plate on a face plane
/// (a wall, floor or ceiling), a frame's body, or another thing's mesh.
/// </summary>
internal sealed class Solid
{
    internal Solid(SolidKind kind, string name, long? id, Box3 box, FacePlane? plane = null)
    {
        Kind = kind;
        Name = name;
        Id = id;
        Box = box;
        Plane = plane;
    }

    internal SolidKind Kind { get; }

    /// <summary>Its shown name and prefab with id, "Composite Wall (StructureCompositeWall 44)".</summary>
    internal string Name { get; }

    internal long? Id { get; }

    internal Box3 Box { get; }

    /// <summary>The face plane a plate stands on; null for a frame or a body.</summary>
    internal FacePlane? Plane { get; }

    /// <summary>The solid in words, from where it lies against a body: "the floor (...) on y=212".</summary>
    internal string Describe(Box3 body) => Kind switch
    {
        SolidKind.Frame => $"a frame's body ({Name})",
        SolidKind.Body => Name,
        _ when Plane is FacePlane plane => $"{PlateWord(plane, body)} ({Name}) on {plane}",
        _ => Name
    };

    private static string PlateWord(FacePlane plane, Box3 body) =>
        plane.Axis != 1 ? "a wall"
        : plane.Metres <= body.Centre.Y ? "the floor"
        : "the ceiling";
}

/// <summary>
/// clips_surface: a body that runs into a wall, floor or ceiling plate or a frame's body by more than
/// ConflictCodes.OverlapToleranceM (Box3.ClashDepth on the mesh boxes; the depth reported is Box3.Penetration),
/// leaving out the surface it rests on: a plate on its mount plane (MountRect, the face plane behind its footprint
/// along -outward) and a frame wholly behind that plane. A small-grid device stands with its small cells on the plane
/// it rests on, so its mesh always reaches into that surface; anywhere else it is sunk into the surface. A plate first
/// (what a player sees), then the deepest, then the lowest id.
/// </summary>
internal static class SurfaceClip
{
    private const double Edge = 1e-6;

    internal static (Solid Solid, double Depth)? First(Box3 body, MountRect? mount, IReadOnlyList<Solid> solids)
    {
        (Solid Solid, double Depth)? found = null;
        foreach (Solid solid in solids)
        {
            if (solid.Kind == SolidKind.Body || RestsOn(solid, mount) ||
                body.ClashDepth(solid.Box) <= ConflictCodes.OverlapToleranceM)
            {
                continue;
            }

            double depth = body.Penetration(solid.Box);
            if (found == null || Before(solid, depth, found.Value.Solid, found.Value.Depth))
            {
                found = (solid, depth);
            }
        }

        return found;
    }

    // A plate before a frame (the floor or wall a player sees), then the deeper, then the lower id.
    private static bool Before(Solid solid, double depth, Solid other, double otherDepth)
    {
        int rank = solid.Kind == SolidKind.Plate ? 0 : 1;
        int otherRank = other.Kind == SolidKind.Plate ? 0 : 1;
        if (rank != otherRank)
        {
            return rank < otherRank;
        }

        if (Math.Abs(depth - otherDepth) > Edge)
        {
            return depth > otherDepth;
        }

        return (solid.Id ?? long.MaxValue) < (other.Id ?? long.MaxValue);
    }

    /// <summary>The text clips_surface reports: the solid and how deep, "the floor (...) on y=212, 0.25 m deep".</summary>
    internal static string Describe(Solid solid, double depth, Box3 body) =>
        solid.Describe(body) + string.Format(CultureInfo.InvariantCulture, ", {0:0.00} m deep", depth);

    // The surface the body rests on: a plate on its mount plane, or a frame wholly on the far side of that plane.
    internal static bool RestsOn(Solid solid, MountRect? mount)
    {
        if (mount == null)
        {
            return false;
        }

        FacePlane plane = mount.Plane;
        if (solid.Kind == SolidKind.Plate)
        {
            return solid.Plane is FacePlane own && own.Equals(plane);
        }

        int axis = plane.Axis;
        return mount.Outward.Dx + mount.Outward.Dy + mount.Outward.Dz > 0
            ? solid.Box.Max[axis] <= plane.Metres + Edge
            : solid.Box.Min[axis] >= plane.Metres - Edge;
    }
}

/// <summary>
/// controls_blocked's mesh check: the side a body's controls face needs ClearanceM of room in front of it. A solid
/// covering at least CoverShare of that side's rectangle and reaching into the room in front (or across the side
/// itself: controls sunk in a floor) blocks it; the surface it rests on (SurfaceClip.RestsOn) only when the controls
/// face into or out of it, not for a side beside it. Nearest first, then the lowest id.
/// </summary>
internal static class ControlsReach
{
    /// <summary>The room a hand needs in front of the controls: one small cell (the cell check's depth).</summary>
    internal const double ClearanceM = 0.5;

    /// <summary>The share of the controls side a solid must cover to block it: a floor under a side panel does not.</summary>
    internal const double CoverShare = 0.25;

    private const double Edge = 1e-6;

    internal static (Solid Solid, double Distance)? Blocker(Box3 body, GridStep face, IReadOnlyList<Solid> solids,
        MountRect? mount = null)
    {
        bool sideways = mount != null && mount.Plane.Axis != face.Axis;
        int axis = face.Axis;
        bool positive = face.Dx + face.Dy + face.Dz > 0;
        double side = positive ? body.Max[axis] : body.Min[axis];
        int u = axis == 0 ? 1 : 0;
        int v = axis == 2 ? 1 : 2;
        double area = Math.Max(body.Max[u] - body.Min[u], Edge) * Math.Max(body.Max[v] - body.Min[v], Edge);
        (Solid Solid, double Distance)? found = null;
        foreach (Solid solid in solids)
        {
            if (sideways && solid.Kind != SolidKind.Body && SurfaceClip.RestsOn(solid, mount))
            {
                continue;
            }

            Box3 box = solid.Box;
            bool reachesPast = positive ? box.Max[axis] > side + Edge : box.Min[axis] < side - Edge;
            double distance = Math.Max(0.0, positive ? box.Min[axis] - side : side - box.Max[axis]);
            if (!reachesPast || distance >= ClearanceM)
            {
                continue;
            }

            double covered = Overlap(body, box, u) * Overlap(body, box, v);
            if (covered < CoverShare * area)
            {
                continue;
            }

            if (found == null || distance < found.Value.Distance - Edge ||
                (Math.Abs(distance - found.Value.Distance) <= Edge &&
                 (solid.Id ?? long.MaxValue) < (found.Value.Solid.Id ?? long.MaxValue)))
            {
                found = (solid, distance);
            }
        }

        return found;
    }

    /// <summary>The text controls_blocked reports: "a frame's body (...) 0.00 m in front".</summary>
    internal static string Describe(Solid solid, double distance, Box3 body) =>
        solid.Describe(body) + string.Format(CultureInfo.InvariantCulture, " {0:0.00} m in front", distance);

    private static double Overlap(Box3 a, Box3 b, int axis) =>
        Math.Max(0.0, Math.Min(a.Max[axis], b.Max[axis]) - Math.Max(a.Min[axis], b.Min[axis]));
}

/// <summary>A body's box at a quarter turn and position, from the box it fills in its own frame (Thing.Bounds).</summary>
internal static class TurnedBox
{
    internal static Box3 Of(Box3 local, CubeRotation turn, Vec3 position)
    {
        List<Vec3> corners = new List<Vec3>(8);
        for (int index = 0; index < 8; index++)
        {
            double x = (index & 1) != 0 ? local.Max.X : local.Min.X;
            double y = (index & 2) != 0 ? local.Max.Y : local.Min.Y;
            double z = (index & 4) != 0 ? local.Max.Z : local.Min.Z;
            corners.Add(position + Vec3.Of(turn.Right) * x + Vec3.Of(turn.Up) * y + Vec3.Of(turn.Forward) * z);
        }

        return Box3.Around(corners);
    }
}

/// <summary>
/// A run piece that is a device's port stub: it stands in the joining cell of a device's port of its kind, or one small
/// cell from it (the piece that turns the run out of the frames toward a device standing outside them).
/// </summary>
internal static class PortStubs
{
    internal static bool IsStub(IReadOnlyList<GridCell> pieceCells, IEnumerable<GridCell> joiningCells)
    {
        foreach (GridCell joining in joiningCells)
        {
            foreach (GridCell cell in pieceCells)
            {
                int steps = (Math.Abs(cell.X - joining.X) + Math.Abs(cell.Y - joining.Y) + Math.Abs(cell.Z - joining.Z)) /
                            GridStep.CellSize;
                if (steps <= 1)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
