#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;

namespace StationGodMCP.Pure;

/// <summary>
/// What makes a structure a rocket's, as the game decides it. Many ordinary station devices (batteries, transformers,
/// tanks, pipes, valves, vents, connectors) implement IRocketInternals so they may also be fitted inside a rocket;
/// that alone makes nothing a rocket part. The game keeps a strictly internal piece to rocket cells
/// (SmallGrid.CanConstruct: CannotPlaceOutsideRocket) and ties a placed piece to a rocket through its RocketNetwork or
/// Structure.RocketData; the fuselage and launch mount are the rocket's own hull.
/// </summary>
internal readonly struct RocketTraits
{
    internal RocketTraits(bool strictlyInternal, bool hull, bool onRocket)
    {
        StrictlyInternal = strictlyInternal;
        Hull = hull;
        OnRocket = onRocket;
    }

    /// <summary>IRocketInternals.StrictlyInternal: the game places it only inside a rocket.</summary>
    internal bool StrictlyInternal { get; }

    /// <summary>A fuselage piece or a launch mount.</summary>
    internal bool Hull { get; }

    /// <summary>Standing in a rocket: a RocketNetwork or RocketData ties it to one.</summary>
    internal bool OnRocket { get; }

    /// <summary>A prefab only a rocket takes; place_structure refuses to build it.</summary>
    internal bool RocketOnly => StrictlyInternal || Hull;

    /// <summary>A standing piece that belongs to a rocket; remove_structure refuses to take it down.</summary>
    internal bool PartOfRocket => RocketOnly || OnRocket;
}

/// <summary>
/// Where one placement of a place_structure request stands, as the game's slots tell pieces apart: the snapped
/// position, the slot kind, and for a piece placed on a cell face its side. The game registers a face-placed piece
/// (a wall) in the cell it faces into (Structure.GetGrid: position + forward * 0.1, snapped), so a face holds one such
/// piece per side: two plates back to back share the face and are two slots.
/// </summary>
internal readonly struct PlacementSpot : IEquatable<PlacementSpot>
{
    private readonly int _x;
    private readonly int _y;
    private readonly int _z;

    private PlacementSpot(int x, int y, int z, string slot, int side)
    {
        _x = x;
        _y = y;
        _z = z;
        Slot = slot;
        Side = side;
    }

    internal string Slot { get; }

    /// <summary>The GridStep index a face-placed piece faces; -1 for any other piece.</summary>
    internal int Side { get; }

    /// <param name="x">Snapped position, metres.</param>
    /// <param name="y">Snapped position, metres.</param>
    /// <param name="z">Snapped position, metres.</param>
    /// <param name="slot">The slot kind (a small-grid piece's slot, or the placement type).</param>
    /// <param name="facing">For a piece placed on a face, the way it faces; null otherwise.</param>
    internal static PlacementSpot Of(double x, double y, double z, string slot, GridStep? facing) =>
        new PlacementSpot(Centimetres(x), Centimetres(y), Centimetres(z), slot, facing?.Index ?? -1);

    public bool Equals(PlacementSpot other) =>
        _x == other._x && _y == other._y && _z == other._z && Side == other.Side &&
        string.Equals(Slot, other.Slot, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is PlacementSpot other && Equals(other);

    public override int GetHashCode() =>
        unchecked(((((_x * 397) ^ _y) * 397 ^ _z) * 397 ^ Side) * 397 ^ StringComparer.Ordinal.GetHashCode(Slot));

    private static int Centimetres(double metres) => (int)Math.Round(metres * 100.0, MidpointRounding.AwayFromZero);
}

/// <summary>
/// Where the game's construction cursor puts a piece aimed at a point. The cursor snaps the point its ray lands on,
/// and that point lies on a surface: a small-grid device aimed at a floor stands on the floor plane (a large-cell
/// face, an even metre), not in the air at the cell's centre; a mounted one on the face it mounts to. A point given
/// inside a cell therefore moves, against the direction pointing away from the surface, to the face plane there; a
/// point already on a face plane on that axis stays.
/// </summary>
internal static class CursorAim
{
    /// <summary>A large cell is 2 m; its faces lie on even metres.</summary>
    internal const double LargeCell = 2.0;

    private const double OnPlane = 0.001;

    /// <summary>The point moved onto the surface behind it for a piece pointing away from it in the given direction.</summary>
    internal static (double X, double Y, double Z) OntoFloor(double x, double y, double z, GridStep up) =>
        up.Axis switch
        {
            0 => (Floor(x, up.Dx), y, z),
            1 => (x, Floor(y, up.Dy), z),
            _ => (x, y, Floor(z, up.Dz))
        };

    /// <summary>Whether the coordinate already lies on a large-cell face plane.</summary>
    internal static bool OnFacePlane(double coordinate)
    {
        double offset = coordinate - Math.Round(coordinate / LargeCell) * LargeCell;
        return Math.Abs(offset) < OnPlane;
    }

    // The face plane below along -up: the next lower even metre for up +1, the next higher for up -1.
    private static double Floor(double coordinate, int upSign)
    {
        if (OnFacePlane(coordinate))
        {
            return Math.Round(coordinate / LargeCell) * LargeCell;
        }

        return upSign > 0
            ? Math.Floor(coordinate / LargeCell) * LargeCell
            : Math.Ceiling(coordinate / LargeCell) * LargeCell;
    }
}

/// <summary>
/// The faces a remove_structure request's would_breach findings have named, so a face breached by several pieces of
/// one request (two plates back to back; a wall and the frame behind it) is reported once, by the first piece that
/// opens it.
/// </summary>
internal sealed class BreachedFaces
{
    private readonly HashSet<GridPoint> _named = new HashSet<GridPoint>();

    /// <summary>
    /// Takes the faces a piece's removal opens; true when any of them is not yet named (the piece reports the breach),
    /// false when an earlier piece already named them all.
    /// </summary>
    internal bool Claim(IEnumerable<GridPoint> faces)
    {
        bool fresh = false;
        foreach (GridPoint face in faces)
        {
            fresh |= _named.Add(face);
        }

        return fresh;
    }

    /// <summary>
    /// The faces a piece's removal opens that no earlier breach named: only the spaces beside them are this piece's
    /// breach (structures-27: a wall and the frame behind it both open the wall's face; once the wall reported the
    /// room against the frame's cell, the frame reported the room again against a cell beside the frame). Name the
    /// ones a reported breach used with Claim.
    /// </summary>
    internal List<GridPoint> Unnamed(IEnumerable<GridPoint> faces)
    {
        List<GridPoint> unnamed = new List<GridPoint>();
        foreach (GridPoint face in faces)
        {
            if (!_named.Contains(face) && !unnamed.Contains(face))
            {
                unnamed.Add(face);
            }
        }

        return unnamed;
    }
}

/// <summary>Metres for messages: the game's Grid3 points and cells are decimetres.</summary>
internal static class GridText
{
    internal static string Metres(int xDecimetres, int yDecimetres, int zDecimetres) =>
        string.Format(CultureInfo.InvariantCulture, "({0:0.##}, {1:0.##}, {2:0.##})", xDecimetres / 10.0,
            yDecimetres / 10.0, zDecimetres / 10.0);
}
