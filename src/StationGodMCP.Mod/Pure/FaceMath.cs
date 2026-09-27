#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// A point of the large grid in decimetres (the game's Grid3 units). Large cells are 2 m with centres at odd metres
/// (10 modulo 20 on every axis); a face point lies on an even metre on exactly one axis and at a cell centre on the
/// other two.
/// </summary>
internal readonly struct GridPoint : IEquatable<GridPoint>
{
    internal GridPoint(int x, int y, int z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    internal int X { get; }

    internal int Y { get; }

    internal int Z { get; }

    public static GridPoint operator +(GridPoint a, GridPoint b) => new GridPoint(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static GridPoint operator -(GridPoint a, GridPoint b) => new GridPoint(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public bool Equals(GridPoint other) => X == other.X && Y == other.Y && Z == other.Z;

    public override bool Equals(object? obj) => obj is GridPoint other && Equals(other);

    public override int GetHashCode() => unchecked((X * 73856093) ^ (Y * 19349663) ^ (Z * 83492791));

    public override string ToString() => $"({X}, {Y}, {Z})";
}

/// <summary>
/// One slot a structure registers in (GridController.AddGridStructure): the cell whose StructuralArray holds it and
/// the point it was added at (Cell.Add's relativePosition). A frame's point is the cell itself (the Center slot); a
/// wall's is its BlockingGrids point on a face, in the cell on its CenterPosition's side.
/// </summary>
internal readonly struct StructureSlot : IEquatable<StructureSlot>
{
    internal StructureSlot(GridPoint cell, GridPoint point)
    {
        Cell = cell;
        Point = point;
    }

    internal GridPoint Cell { get; }

    internal GridPoint Point { get; }

    /// <summary>The point relative to the cell centre: the StructuralArray element it fills.</summary>
    internal GridPoint Offset => Point - Cell;

    public bool Equals(StructureSlot other) => Cell.Equals(other.Cell) && Point.Equals(other.Point);

    public override bool Equals(object? obj) => obj is StructureSlot other && Equals(other);

    public override int GetHashCode() => unchecked(Cell.GetHashCode() * 31 + Point.GetHashCode());

    public override string ToString() => $"{Point} in cell {Cell}";
}

/// <summary>The large grid's cell and face arithmetic, in decimetres.</summary>
internal static class FaceMath
{
    internal const int CellSize = 20;
    internal const int HalfCell = 10;

    /// <summary>The six steps to a neighbour cell: +x, -x, +y, -y, +z, -z.</summary>
    internal static readonly GridPoint[] Steps =
    {
        new GridPoint(CellSize, 0, 0), new GridPoint(-CellSize, 0, 0),
        new GridPoint(0, CellSize, 0), new GridPoint(0, -CellSize, 0),
        new GridPoint(0, 0, CellSize), new GridPoint(0, 0, -CellSize)
    };

    internal static bool IsCellCentre(GridPoint point) =>
        IsCentreCoordinate(point.X) && IsCentreCoordinate(point.Y) && IsCentreCoordinate(point.Z);

    internal static List<GridPoint> Neighbours(GridPoint cell)
    {
        List<GridPoint> neighbours = new List<GridPoint>(Steps.Length);
        foreach (GridPoint step in Steps)
        {
            neighbours.Add(cell + step);
        }

        return neighbours;
    }

    /// <summary>The six face points of a cell, in the order of Steps.</summary>
    internal static List<GridPoint> FacesOf(GridPoint cell)
    {
        List<GridPoint> faces = new List<GridPoint>(Steps.Length);
        foreach (GridPoint step in Steps)
        {
            faces.Add(new GridPoint(cell.X + step.X / 2, cell.Y + step.Y / 2, cell.Z + step.Z / 2));
        }

        return faces;
    }

    /// <summary>
    /// The two cells a face point separates (as Structure.WorldChangeChecks finds them for a wall): the face's
    /// cell and its mirror across the face. False when the point is not a face point.
    /// </summary>
    internal static bool TrySplitFace(GridPoint face, out GridPoint first, out GridPoint second)
    {
        first = default;
        second = default;
        bool onX = IsFaceCoordinate(face.X);
        bool onY = IsFaceCoordinate(face.Y);
        bool onZ = IsFaceCoordinate(face.Z);
        int faceAxes = (onX ? 1 : 0) + (onY ? 1 : 0) + (onZ ? 1 : 0);
        if (faceAxes != 1 ||
            (!onX && !IsCentreCoordinate(face.X)) ||
            (!onY && !IsCentreCoordinate(face.Y)) ||
            (!onZ && !IsCentreCoordinate(face.Z)))
        {
            return false;
        }

        GridPoint half = new GridPoint(onX ? HalfCell : 0, onY ? HalfCell : 0, onZ ? HalfCell : 0);
        first = face - half;
        second = face + half;
        return true;
    }

    /// <summary>
    /// Grid3.GridCenter(origin): the cell centre a point registers in, taking the cell on the origin's side on any
    /// axis where the point lies on a face. The origin is in metres (a structure's CenterPosition), as the game has it.
    /// </summary>
    internal static GridPoint CellOnOriginSide(GridPoint point, float originX, float originY, float originZ) =>
        new GridPoint(CentreOnSide(point.X, originX), CentreOnSide(point.Y, originY), CentreOnSide(point.Z, originZ));

    /// <summary>Grid3's metres to decimetres: times 10, rounded half away from zero (Grid3.Round).</summary>
    internal static int Decimetres(float metres)
    {
        float value = metres * 10f;
        return value >= 0f ? (int)(value + 0.5f) : (int)(value - 0.5f);
    }

    /// <summary>Whether two slot lists name the same slots, in any order.</summary>
    internal static bool SameSlots(IReadOnlyCollection<StructureSlot> a, IReadOnlyCollection<StructureSlot> b)
    {
        HashSet<StructureSlot> left = new HashSet<StructureSlot>(a);
        HashSet<StructureSlot> right = new HashSet<StructureSlot>(b);
        return left.SetEquals(right);
    }

    private static int CentreOnSide(int decimetres, float origin)
    {
        float metres = decimetres / 10f;
        float halfSteps = (metres - 1f) / 2f;
        float centre = (metres <= origin ? MathF.Ceiling(halfSteps) : MathF.Floor(halfSteps)) * 2f + 1f;
        return Decimetres(centre);
    }

    private static int Modulo(int value) => ((value % CellSize) + CellSize) % CellSize;

    private static bool IsCentreCoordinate(int value) => Modulo(value) == HalfCell;

    private static bool IsFaceCoordinate(int value) => Modulo(value) == 0;
}
