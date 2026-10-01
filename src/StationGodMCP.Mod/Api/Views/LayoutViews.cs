#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Views;

/// <summary>A point in metres to 0.01 m: fine enough for 0.25 m small-cell edges.</summary>
internal sealed class PointView
{
    internal PointView(double x, double y, double z)
    {
        X = System.Math.Round(x, 2);
        Y = System.Math.Round(y, 2);
        Z = System.Math.Round(z, 2);
    }

    public double X { get; }

    public double Y { get; }

    public double Z { get; }

    internal static PointView Of(Vec3 v) => new PointView(v.X, v.Y, v.Z);

    internal static PointView OfCell(GridCell cell) => new PointView(cell.X / 10.0, cell.Y / 10.0, cell.Z / 10.0);
}

/// <summary>A direction to 0.001.</summary>
internal sealed class VectorView
{
    internal VectorView(double x, double y, double z)
    {
        X = System.Math.Round(x, 3);
        Y = System.Math.Round(y, 3);
        Z = System.Math.Round(z, 3);
    }

    public double X { get; }

    public double Y { get; }

    public double Z { get; }

    internal static VectorView Of(Vec3 v) => new VectorView(v.X, v.Y, v.Z);
}

/// <summary>A box in metres: its corners, centre and size.</summary>
internal sealed class BoxView
{
    internal BoxView(Box3 box)
    {
        Min = PointView.Of(box.Min);
        Max = PointView.Of(box.Max);
        Size = PointView.Of(box.Size);
    }

    public PointView Min { get; }

    public PointView Max { get; }

    public PointView Size { get; }
}

/// <summary>
/// The world axes nearest the player's level forward, right and up (and their opposites), and the axis nearest the
/// look direction itself.
/// </summary>
internal sealed class ViewAxesView
{
    internal ViewAxesView(ViewBasis basis)
    {
        Forward = basis.LevelForward.Name;
        Right = basis.LevelRight.Name;
        Up = "+y";
        Back = basis.LevelForward.Opposite.Name;
        Left = basis.LevelRight.Opposite.Name;
        Down = "-y";
        Look = basis.LookAxis.Name;
    }

    public string Forward { get; }

    public string Right { get; }

    public string Up { get; }

    public string Back { get; }

    public string Left { get; }

    public string Down { get; }

    /// <summary>The axis nearest the look direction, pitch included (a steep look down is -y).</summary>
    public string Look { get; }
}

/// <summary>looking_at's view: the camera's eye, basis, heading and pitch, snapped axes and the camera mode.</summary>
internal sealed class LookView
{
    internal LookView(Vec3 eye, ViewBasis basis, bool thirdPerson, bool seated)
    {
        Eye = PointView.Of(eye);
        Forward = VectorView.Of(basis.Forward);
        Right = VectorView.Of(basis.Right);
        Up = VectorView.Of(basis.Up);
        YawDeg = System.Math.Round(basis.YawDegrees, 1);
        PitchDeg = System.Math.Round(basis.PitchDegrees, 1);
        Axes = new ViewAxesView(basis);
        Ambiguous = basis.Ambiguous;
        ThirdPerson = thirdPerson;
        Seated = seated;
    }

    public PointView Eye { get; }

    public VectorView Forward { get; }

    public VectorView Right { get; }

    public VectorView Up { get; }

    /// <summary>0 looking along +z, 90 along +x, 180 along -z, 270 along -x.</summary>
    public double YawDeg { get; }

    /// <summary>Positive up.</summary>
    public double PitchDeg { get; }

    public ViewAxesView Axes { get; }

    /// <summary>The heading is within 10 degrees of a diagonal: forward and right could be either axis.</summary>
    public bool Ambiguous { get; }

    public bool ThirdPerson { get; }

    public bool Seated { get; }
}

/// <summary>An offset in a thing's own frame: metres to its right, up and forward from its origin.</summary>
internal sealed class LocalOffsetView
{
    internal LocalOffsetView(double right, double up, double forward)
    {
        RightM = System.Math.Round(right, 2);
        UpM = System.Math.Round(up, 2);
        ForwardM = System.Math.Round(forward, 2);
    }

    public double RightM { get; }

    public double UpM { get; }

    public double ForwardM { get; }
}

/// <summary>Where the look ray hits the first surface, and the grid there.</summary>
internal sealed class LookHitView
{
    internal LookHitView(Vec3 point, double distance, Vec3 normal, string? face, string? facePlane,
        PositionView cell2M, PointView smallCell, string support, ThingView? thing, LocalOffsetView? localOnTarget)
    {
        Point = PointView.Of(point);
        DistanceM = System.Math.Round(distance, 2);
        Normal = VectorView.Of(normal);
        Face = face;
        FacePlane = facePlane;
        Cell2M = cell2M;
        SmallCell = smallCell;
        Support = support;
        Thing = thing;
        LocalOnTarget = localOnTarget;
    }

    public PointView Point { get; }

    public double DistanceM { get; }

    public VectorView Normal { get; }

    /// <summary>The surface's facing as an axis (+x.. -z), null when it is not along one.</summary>
    public string? Face { get; }

    /// <summary>The 2 m face plane the point lies on ("z=668"), null when none.</summary>
    public string? FacePlane { get; }

    /// <summary>The 2 m cell on the looker's side of the surface.</summary>
    [JsonProperty("cell_2m")]
    public PositionView Cell2M { get; }

    /// <summary>The small cell a piece mounted on this surface here would stand in.</summary>
    public PointView SmallCell { get; }

    /// <summary>That small cell's grid_survey support character (i, e, f, w, a; x a door's keep-out, g a window).</summary>
    public string Support { get; }

    /// <summary>What the ray hit (may be beyond the 3 m the game's own target reaches).</summary>
    public ThingView? Thing { get; }

    /// <summary>The hit point in the target's own frame, from its origin.</summary>
    public LocalOffsetView? LocalOnTarget { get; }
}

/// <summary>
/// A structure's body: its origin, the box its meshes fill in the world (Thing.Bounds turned and moved), how far that
/// box's centre sits from the origin, and the small-grid cells the game registers it in (its collision footprint) with
/// their box.
/// </summary>
internal sealed class BodyView
{
    internal BodyView(Vec3 origin, Box3 render, Box3? grid, int gridCells)
    {
        Origin = PointView.Of(origin);
        RenderBox = new BoxView(render);
        CentreOffset = PointView.Of(render.Centre - origin);
        GridBox = grid.HasValue ? new BoxView(grid.Value) : null;
        GridCells = gridCells;
    }

    public PointView Origin { get; }

    /// <summary>The box its meshes fill (visual extent).</summary>
    public BoxView RenderBox { get; }

    /// <summary>render_box's centre minus the origin, world metres.</summary>
    public PointView CentreOffset { get; }

    /// <summary>The box of the small cells it takes (the game's footprint); null for 2 m structures.</summary>
    public BoxView? GridBox { get; }

    public int GridCells { get; }
}

/// <summary>A list of small cells, capped, with the total.</summary>
internal sealed class CellListView
{
    internal CellListView(IReadOnlyList<GridCell> cells, int cap)
    {
        Count = cells.Count;
        List<PointView> listed = new List<PointView>(System.Math.Min(cap, cells.Count));
        for (int index = 0; index < cells.Count && index < cap; index++)
        {
            listed.Add(PointView.OfCell(cells[index]));
        }

        Cells = listed;
    }

    private CellListView(int count)
    {
        Count = count;
    }

    public int Count { get; }

    /// <summary>The cells, up to the cap; left out of a count-only list.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<PointView>? Cells { get; }

    /// <summary>The total alone, the cells left out.</summary>
    internal CellListView CountOnly() => new CellListView(Count);
}
