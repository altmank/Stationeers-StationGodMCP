#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;

namespace StationGodMCP.Pure;

/// <summary>What a face structure is to a layout: a plain wall, a window a run should keep off, or a door.</summary>
internal enum OpeningKind
{
    Wall,

    /// <summary>Any see-through face: glass and composite windows, shuttered windows, window shutters.</summary>
    Window,

    /// <summary>A door, airlock, hatch, force-field door or robot arm door: people walk through it.</summary>
    Door
}

/// <summary>One face structure on a 2 m face: its kind and reference id.</summary>
internal readonly struct FaceOpening
{
    internal FaceOpening(OpeningKind kind, long id)
    {
        Kind = kind;
        Id = id;
    }

    internal OpeningKind Kind { get; }

    internal long Id { get; }
}

/// <summary>
/// Where a small cell stands against the doors and windows around it: clear, in a door's keep-out (the door's face
/// and a band either side of it, where a piece would block the doorway or its frame), or on a window's face.
/// </summary>
internal readonly struct OpeningZone : IEquatable<OpeningZone>
{
    private OpeningZone(OpeningKind? kind, long id)
    {
        Kind = kind;
        Id = id;
    }

    internal static OpeningZone Clear => new OpeningZone(null, 0);

    internal static OpeningZone DoorKeepOut(long doorId) => new OpeningZone(OpeningKind.Door, doorId);

    internal static OpeningZone OnWindow(long windowId) => new OpeningZone(OpeningKind.Window, windowId);

    /// <summary>Door or Window; null when the cell is clear.</summary>
    internal OpeningKind? Kind { get; }

    /// <summary>The door's or window's reference id; 0 when clear.</summary>
    internal long Id { get; }

    internal bool IsDoor => Kind == OpeningKind.Door;

    internal bool IsWindow => Kind == OpeningKind.Window;

    public bool Equals(OpeningZone other) => Kind == other.Kind && Id == other.Id;

    public override bool Equals(object? obj) => obj is OpeningZone other && Equals(other);

    public override int GetHashCode() => ((int?)Kind ?? -1) * 397 ^ Id.GetHashCode();

    public override string ToString() => Kind == null ? "clear" : $"{Kind} {Id}";
}

/// <summary>
/// How far a door's keep-out reaches from its face plane on either side: 0 to 2 m in 0.5 m steps (one small cell
/// each). The face plane itself is always in it.
/// </summary>
internal readonly struct DoorBand
{
    internal const double DefaultMetres = 0.5;
    internal const double MaximumMetres = 2.0;

    private DoorBand(int cells)
    {
        Cells = cells;
    }

    internal static DoorBand Default => new DoorBand(1);

    /// <summary>Small cells on each side of the face plane.</summary>
    internal int Cells { get; }

    internal double Metres => Cells * 0.5;

    /// <summary>The band for a width in metres, or null with the reason when it is not 0 to 2 m in 0.5 m steps.</summary>
    internal static DoorBand? FromMetres(double metres, out string? error)
    {
        double cells = metres / 0.5;
        if (double.IsNaN(metres) || metres < 0.0 || metres > MaximumMetres ||
            Math.Abs(cells - Math.Round(cells)) > 1e-6)
        {
            error = string.Format(CultureInfo.InvariantCulture,
                "a door keep-out band must be 0 to {0} m in 0.5 m steps, not {1}", MaximumMetres, metres);
            return null;
        }

        error = null;
        return new DoorBand((int)Math.Round(cells));
    }
}

/// <summary>
/// Classifies small cells against the doors and windows on the 2 m faces around them. Faces are Grid3 points in
/// decimetres: on a face plane (a multiple of 20 on the plane's axis) at the face's centre (odd metres, 10 + 20k, on
/// the other two). A door keeps out every small cell on its face plane inside the closed square of each face it covers
/// (jambs, top edge and threshold included) and the cells up to band away from the plane on either side inside the
/// same squares; the faces of a multi-cell door share their edges, so the union is the door's whole rectangle. A cell
/// hidden inside a frame's body (the floor slab under a threshold, a wall's frame column) never counts. A window takes
/// only the cells on its face plane strictly inside its square: a run along the frame edge beside a window is not on
/// it. A cell that is a door's own port joining cell is released by the caller.
/// </summary>
internal static class OpeningZones
{
    private const int FaceSpan = SmallCellCode.Large / 2;

    /// <summary>The zone of a small cell, from the face structures at each face point (FaceAt).</summary>
    internal static OpeningZone At(GridCell small, DoorBand band, Func<GridCell, IReadOnlyList<FaceOpening>> faceAt,
        bool hidden)
    {
        OpeningZone window = OpeningZone.Clear;
        int[] c = { small.X, small.Y, small.Z };
        int reach = band.Cells * GridStep.CellSize;
        for (int axis = 0; axis < 3; axis++)
        {
            int b = (axis + 1) % 3;
            int d = (axis + 2) % 3;
            int first = CeilTo(c[axis] - reach, SmallCellCode.Large);
            for (int plane = first; plane <= c[axis] + reach; plane += SmallCellCode.Large)
            {
                foreach (int fb in FaceCentres(c[b]))
                {
                    foreach (int fd in FaceCentres(c[d]))
                    {
                        int[] point = new int[3];
                        point[axis] = plane;
                        point[b] = fb;
                        point[d] = fd;
                        foreach (FaceOpening opening in faceAt(new GridCell(point[0], point[1], point[2])))
                        {
                            if (opening.Kind == OpeningKind.Door && !hidden)
                            {
                                return OpeningZone.DoorKeepOut(opening.Id);
                            }

                            if (opening.Kind == OpeningKind.Window && plane == c[axis] &&
                                Math.Abs(c[b] - fb) < FaceSpan && Math.Abs(c[d] - fd) < FaceSpan)
                            {
                                window = OpeningZone.OnWindow(opening.Id);
                            }
                        }
                    }
                }
            }
        }

        return window;
    }

    /// <summary>The face centres (odd metres) whose closed 2 m square holds the coordinate: one, or two on an edge.</summary>
    internal static List<int> FaceCentres(int coordinate)
    {
        List<int> centres = new List<int>(2);
        int below = FloorTo(coordinate - FaceSpan, SmallCellCode.Large) + FaceSpan;
        for (int centre = below; centre <= coordinate + FaceSpan; centre += SmallCellCode.Large)
        {
            if (Math.Abs(coordinate - centre) <= FaceSpan)
            {
                centres.Add(centre);
            }
        }

        return centres;
    }

    /// <summary>
    /// grid_survey's support string with the opening zones laid over it: 'x' in a door's keep-out, 'g' on a window.
    /// </summary>
    internal static string Overlay(string support, IReadOnlyList<OpeningZone> zones)
    {
        char[] text = support.ToCharArray();
        for (int index = 0; index < text.Length && index < zones.Count; index++)
        {
            text[index] = zones[index].IsDoor ? 'x' : zones[index].IsWindow ? 'g' : text[index];
        }

        return new string(text);
    }

    private static int FloorTo(int value, int step) => (int)Math.Floor(value / (double)step) * step;

    private static int CeilTo(int value, int step) => (int)Math.Ceiling(value / (double)step) * step;
}

/// <summary>A face plane of the 2 m grid: an axis (0 x, 1 y, 2 z) and its coordinate in decimetres (a multiple of 20).</summary>
internal readonly struct FacePlane : IEquatable<FacePlane>
{
    private FacePlane(int axis, int coordinate)
    {
        Axis = axis;
        Coordinate = coordinate;
    }

    internal int Axis { get; }

    /// <summary>Decimetres, a multiple of 20.</summary>
    internal int Coordinate { get; }

    internal double Metres => Coordinate / 10.0;

    internal static FacePlane Of(int axis, int coordinate) => new FacePlane(axis, coordinate);

    /// <summary>
    /// The face plane on an axis nearest a coordinate (metres), when within tolerance metres of it; null otherwise. A
    /// hit on a floor plate's top or a wall plate's face lies a plate's thickness off its plane.
    /// </summary>
    internal static FacePlane? Near(int axis, double metres, double tolerance)
    {
        double plane = Math.Round(metres / 2.0) * 2.0;
        return Math.Abs(metres - plane) <= tolerance
            ? new FacePlane(axis, (int)Math.Round(plane * 10.0))
            : (FacePlane?)null;
    }

    /// <summary>The plane a face point lies on.</summary>
    internal static FacePlane Of(GridCell facePoint)
    {
        int axis = FacePoints.AxisOf(facePoint);
        if (axis < 0)
        {
            throw new ArgumentException($"{facePoint} is not a face point.", nameof(facePoint));
        }

        return new FacePlane(axis, Component(facePoint, axis));
    }

    internal static int Component(GridCell cell, int axis) => axis == 0 ? cell.X : axis == 1 ? cell.Y : cell.Z;

    public bool Equals(FacePlane other) => Axis == other.Axis && Coordinate == other.Coordinate;

    public override bool Equals(object? obj) => obj is FacePlane other && Equals(other);

    public override int GetHashCode() => Axis * 100003 ^ Coordinate;

    public override string ToString() =>
        string.Format(CultureInfo.InvariantCulture, "{0}={1:0.##}", "xyz"[Axis], Metres);
}

/// <summary>Face points: Grid3 decimetres on a face plane (a multiple of 20) at a 2 m face's centre (10 + 20k).</summary>
internal static class FacePoints
{
    /// <summary>Exactly one coordinate on a face plane and the other two at face centres.</summary>
    internal static bool IsFace(GridCell point) =>
        (OnPlane(point.X) ? 1 : 0) + (OnPlane(point.Y) ? 1 : 0) + (OnPlane(point.Z) ? 1 : 0) == 1 &&
        (OnPlane(point.X) || AtCentre(point.X)) && (OnPlane(point.Y) || AtCentre(point.Y)) &&
        (OnPlane(point.Z) || AtCentre(point.Z));

    /// <summary>The axis (0 x, 1 y, 2 z) whose coordinate is on a face plane; -1 when the point is not a face.</summary>
    internal static int AxisOf(GridCell point) =>
        !IsFace(point) ? -1 : OnPlane(point.X) ? 0 : OnPlane(point.Y) ? 1 : 2;

    private static bool OnPlane(int value) => Mod(value, SmallCellCode.Large) == 0;

    private static bool AtCentre(int value) => Mod(value, SmallCellCode.Large) == SmallCellCode.Large / 2;

    private static int Mod(int value, int by) => ((value % by) + by) % by;
}

/// <summary>
/// The door keep-out and window rules as the route search applies them: a cell in a door's keep-out is blocked unless
/// allowed or one of the route's own ends; a cell on a window costs WindowPenalty more (a warning, never a refusal).
/// </summary>
internal sealed class OpeningGuard
{
    /// <summary>The extra cost of a cell on a window: as much as three extra cells of detour on each side.</summary>
    internal const double WindowPenalty = 6.0;

    private readonly Func<GridCell, OpeningZone> _zone;
    private readonly HashSet<GridCell> _released;
    private readonly bool _allowDoors;

    internal OpeningGuard(Func<GridCell, OpeningZone> zone, IEnumerable<GridCell> ends, bool allowDoors)
    {
        _zone = zone;
        _released = new HashSet<GridCell>(ends);
        _allowDoors = allowDoors;
    }

    /// <summary>The route's own ends that stand in a door's keep-out, so were not blocked.</summary>
    internal List<GridCell> ReleasedInKeepOut()
    {
        List<GridCell> released = new List<GridCell>();
        foreach (GridCell cell in _released)
        {
            if (_zone(cell).IsDoor)
            {
                released.Add(cell);
            }
        }

        return released;
    }

    /// <summary>
    /// The route's own ends in a door's keep-out that the route stands on or beside (one small cell away, diagonals
    /// included). A target network's far ends are ends too, but a route that never comes near them passes no door
    /// there.
    /// </summary>
    internal List<GridCell> ReleasedInKeepOutBeside(IReadOnlyList<GridCell> route) =>
        ReleasedInKeepOut().FindAll(end => IsBeside(end, route));

    private static bool IsBeside(GridCell end, IReadOnlyList<GridCell> route)
    {
        foreach (GridCell cell in route)
        {
            if (Math.Abs(cell.X - end.X) <= GridStep.CellSize && Math.Abs(cell.Y - end.Y) <= GridStep.CellSize &&
                Math.Abs(cell.Z - end.Z) <= GridStep.CellSize)
            {
                return true;
            }
        }

        return false;
    }

    internal Func<GridCell, CellCost> Guard(Func<GridCell, CellCost> cost) =>
        cell =>
        {
            CellCost inner = cost(cell);
            if (!inner.Passable || _released.Contains(cell))
            {
                return inner;
            }

            OpeningZone zone = _zone(cell);
            if (zone.IsDoor && !_allowDoors)
            {
                return CellCost.Blocked;
            }

            return zone.IsWindow ? CellCost.Of(inner.Cost + WindowPenalty, inner.BlockedAxes) : inner;
        };
}
