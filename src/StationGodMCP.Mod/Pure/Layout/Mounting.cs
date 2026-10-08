#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;

namespace StationGodMCP.Pure;

/// <summary>
/// Where a piece sits against a surface: the face plane behind it (the plane its back or bottom rests on) and the
/// rectangle its footprint covers on that plane, in the plane's two other axes (U the lower axis index, V the higher).
/// </summary>
internal sealed class MountRect
{
    /// <summary>How far a footprint's back face may lie from a face plane and still rest on it (half a small cell).</summary>
    internal const double OnPlaneM = 0.3;

    private const double Edge = 1e-6;

    private MountRect(FacePlane plane, GridStep outward, double minU, double maxU, double minV, double maxV)
    {
        Plane = plane;
        Outward = outward;
        MinU = minU;
        MaxU = maxU;
        MinV = minV;
        MaxV = maxV;
    }

    internal FacePlane Plane { get; }

    /// <summary>The way the piece stands out of the plane: its front for a mounted piece, its top for a standing one.</summary>
    internal GridStep Outward { get; }

    internal int U => Plane.Axis == 0 ? 1 : 0;

    internal int V => Plane.Axis == 2 ? 1 : 2;

    internal double MinU { get; }

    internal double MaxU { get; }

    internal double MinV { get; }

    internal double MaxV { get; }

    /// <summary>
    /// The rectangle a piece covers on the face plane behind its footprint along -outward; null when the footprint's
    /// back face is not within OnPlaneM of a face plane (it stands on nothing the grid knows). The plane comes from the
    /// footprint (the small cells the game registers); the rectangle from body, the box its meshes fill, when given:
    /// a mesh overhanging its cells covers the wall it hangs over.
    /// </summary>
    internal static MountRect? Of(Box3 footprint, GridStep outward, Box3? body = null)
    {
        int axis = outward.Axis;
        double back = outward.Dx + outward.Dy + outward.Dz > 0 ? footprint.Min[axis] : footprint.Max[axis];
        double plane = Math.Round(back / 2.0) * 2.0;
        if (Math.Abs(back - plane) > OnPlaneM)
        {
            return null;
        }

        Box3 covers = body ?? footprint;
        int u = axis == 0 ? 1 : 0;
        int v = axis == 2 ? 1 : 2;
        return new MountRect(FacePlane.Of(axis, (int)Math.Round(plane * 10.0)), outward, covers.Min[u],
            covers.Max[u], covers.Min[v], covers.Max[v]);
    }

    /// <summary>
    /// The 2 m faces of the plane the rectangle lies on (face points, decimetres), every one it covers by more than
    /// ConflictCodes.OverlapToleranceM: a mesh's rim a few centimetres past a seam does not count.
    /// </summary>
    internal List<GridCell> Faces()
    {
        List<GridCell> faces = new List<GridCell>();
        foreach (int u in Centres(MinU, MaxU))
        {
            foreach (int v in Centres(MinV, MaxV))
            {
                int[] point = new int[3];
                point[Plane.Axis] = Plane.Coordinate;
                point[U] = u;
                point[V] = v;
                faces.Add(new GridCell(point[0], point[1], point[2]));
            }
        }

        return faces;
    }

    /// <summary>More than one face: the piece spans a seam between wall sections.</summary>
    internal bool CrossesSeam => Faces().Count > 1;

    /// <summary>
    /// The widest rectangle side that still fits one 2 m section: centred on it, it reaches past each seam by at most
    /// ConflictCodes.OverlapToleranceM, which Faces does not count.
    /// </summary>
    internal const double OneSectionM = 2.0 + 2.0 * ConflictCodes.OverlapToleranceM;

    /// <summary>Whether some shift puts the rectangle on one section: neither side is wider than OneSectionM.</summary>
    internal bool FitsOneSection => MaxU - MinU <= OneSectionM + Edge && MaxV - MinV <= OneSectionM + Edge;

    /// <summary>
    /// It spans a seam that shifting it would avoid: crosses_section_seam and device_crosses_seam. A piece wider than a
    /// section (a medium dish, a landing pad part) crosses a seam wherever it stands, so there is nothing to fix.
    /// </summary>
    internal bool CrossesAvoidableSeam => CrossesSeam && FitsOneSection;

    /// <summary>
    /// The face centres (decimetres, odd metres) whose 2 m span the interval overlaps by more than
    /// ConflictCodes.OverlapToleranceM; an interval that overlaps none by that much (a small piece on a seam) is on the
    /// face it overlaps most.
    /// </summary>
    internal static List<int> Centres(double min, double max)
    {
        List<int> centres = new List<int>();
        int best = 0;
        double bestOverlap = Edge;
        int first = (int)Math.Floor((min + Edge - 1.0) / 2.0) * 2 + 1;
        for (int centre = first; centre - 1.0 < max - Edge; centre += 2)
        {
            double overlap = Math.Min(max, centre + 1.0) - Math.Max(min, centre - 1.0);
            if (overlap > ConflictCodes.OverlapToleranceM)
            {
                centres.Add(centre * 10);
            }

            if (overlap > bestOverlap)
            {
                best = centre * 10;
                bestOverlap = overlap;
            }
        }

        if (centres.Count == 0 && bestOverlap > Edge)
        {
            centres.Add(best);
        }

        return centres;
    }

    public override string ToString() =>
        string.Format(CultureInfo.InvariantCulture, "{0} facing {1}: {2} {3:0.##}..{4:0.##}, {5} {6:0.##}..{7:0.##}",
            Plane, Outward, "xyz"[U], MinU, MaxU, "xyz"[V], MinV, MaxV);
}

/// <summary>How serious a layout finding is: a warning to read, or a problem that refuses the run.</summary>
internal enum ConflictLevel
{
    Info,
    Warning,
    Problem
}

/// <summary>One layout finding about a planned or standing piece: a code, how serious, what, and who else.</summary>
internal sealed class LayoutConflict
{
    internal LayoutConflict(string code, ConflictLevel level, string message, long? otherId = null)
    {
        Code = code;
        Level = level;
        Message = message;
        OtherId = otherId;
    }

    internal string Code { get; }

    internal ConflictLevel Level { get; }

    internal string Message { get; }

    internal long? OtherId { get; }
}

/// <summary>The codes the layout checks report.</summary>
internal static class ConflictCodes
{
    internal const string VisualOverlap = "visual_overlap";
    internal const string CrossesSeam = "crosses_section_seam";
    internal const string InDoorKeepOut = "in_door_keepout";
    internal const string CrossesWindow = "crosses_window";
    internal const string BlocksRouteCells = "blocks_route_cells";
    internal const string FrontBlocked = "front_blocked";
    internal const string ControlsBlocked = "controls_blocked";
    internal const string ClipsSurface = "clips_surface";
    internal const string FacesOutOfRoom = "faces_out_of_room";
    internal const string NotUpright = "not_upright";

    /// <summary>
    /// visual_overlap's tolerance: render boxes of neighbours flush on one surface touch or overlap by a few
    /// centimetres (a mesh's rim past its cell); only more than this is a real clash.
    /// </summary>
    internal const double OverlapToleranceM = 0.1;
}

/// <summary>
/// How far two bodies clash, from the boxes their meshes fill (Thing.Bounds): Box3.ClashDepth, so a thin body wholly
/// inside another clashes however thin it is. A clash counts past ConflictCodes.OverlapToleranceM: neighbours flush on
/// one surface only touch, or overlap by a mesh's rim. One mesh overhanging another's small cells is a clash when it
/// reaches that body's mesh: the cells the game registers (Bounds * 0.9, rounded) can be far smaller than the mesh (a
/// 3x3 console registers 1 x 1 m and draws 1.5 x 1.5 m), and a device placed under the overhang is hidden by it.
/// </summary>
internal static class VisualClash
{
    internal static double Depth(Box3 renderA, Box3 renderB) => renderA.ClashDepth(renderB);

    internal static bool Clashes(Box3 renderA, Box3 renderB) =>
        Depth(renderA, renderB) > ConflictCodes.OverlapToleranceM;
}

/// <summary>Which way a port moves what flows through it, from its connection role's name.</summary>
internal static class PortFlow
{
    /// <summary>"in" for inputs, "out" for outputs and waste, null for a role with no direction.</summary>
    internal static string? Of(string role) =>
        role.StartsWith("Input", StringComparison.Ordinal) ? "in"
        : role.StartsWith("Output", StringComparison.Ordinal) || role == "Waste" ? "out"
        : null;
}

/// <summary>Whether a piece stands the way players read it: up is up unless it lies on a floor or ceiling.</summary>
internal static class Uprightness
{
    /// <summary>
    /// Why a turn is not upright, or null. localUp is the prefab's own axis that looks like its top (+y for almost
    /// everything; describe_prefab's visual_up): turned, it should point +y. A piece whose top is its local +y and whose
    /// front faces up or down lies on a floor or ceiling and may turn freely about it.
    /// </summary>
    internal static string? Problem(CubeRotation turn, GridStep localUp)
    {
        (int x, int y, int z) = turn.Apply(localUp.Dx, localUp.Dy, localUp.Dz);
        GridStep world = ViewBasis.Nearest(new Vec3(x, y, z));
        if (world.Equals(GridStep.All[2]) || (turn.Forward.IsVertical && localUp.Equals(GridStep.All[2])))
        {
            return null;
        }

        return $"its top points {world.Name}, not +y";
    }
}
