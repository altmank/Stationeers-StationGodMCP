#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// A structure's body as the game holds it. Thing.Bounds is the box its renderers fill in its own frame (made with the
/// thing at the origin, unturned, so it always holds the origin); turned and moved it gives the visual extent. The
/// footprint is what the game registers: a small-grid piece's GridBounds small cells turned and moved
/// (GridBounds.GetLocalSmallGrid, as SmallGrid.CanConstruct and the grid read them), a 2 m structure's GridBounds
/// cells. Both work for a prefab at a planned position and turn, so a preview and a built piece agree.
/// </summary>
internal static class Bodies
{
    internal static Vec3 V(Vector3 v) => new Vec3(v.x, v.y, v.z);

    internal static Vector3 U(Vec3 v) => new Vector3((float)v.X, (float)v.Y, (float)v.Z);

    /// <summary>The world box the thing's renderers fill at a position and turn.</summary>
    internal static Box3 RenderBox(Thing thing, Vector3 position, Quaternion rotation)
    {
        Bounds local = thing.Bounds;
        List<Vec3> corners = new List<Vec3>(8);
        for (int index = 0; index < 8; index++)
        {
            Vector3 corner = local.center + new Vector3(
                (index & 1) != 0 ? local.extents.x : -local.extents.x,
                (index & 2) != 0 ? local.extents.y : -local.extents.y,
                (index & 4) != 0 ? local.extents.z : -local.extents.z);
            corners.Add(V(rotation * corner + position));
        }

        return Box3.Around(corners);
    }

    internal static Box3 RenderBox(Thing thing) =>
        RenderBox(thing, thing.ThingTransformPosition, thing.ThingTransformRotation);

    /// <summary>The small cells a small-grid structure takes at a position and turn; empty for anything else.</summary>
    internal static List<GridCell> SmallCells(Structure structure, Vector3 position, Quaternion rotation)
    {
        List<GridCell> cells = new List<GridCell>();
        if (!(structure is SmallGrid) || structure.GridBounds == null || !structure.GridBounds.IsValid())
        {
            return cells;
        }

        foreach (Grid3 grid in (Grid3[])structure.GridBounds.GetLocalSmallGrid(position, rotation))
        {
            GridCell cell = PieceShapes.Cell(grid);
            if (!cells.Contains(cell))
            {
                cells.Add(cell);
            }
        }

        return cells;
    }

    internal static List<GridCell> SmallCells(Structure structure) =>
        SmallCells(structure, structure.ThingTransformPosition, structure.ThingTransformRotation);

    /// <summary>The 2 m cells a grid-placed structure takes at a position and turn (GridBounds cells, turned).</summary>
    internal static List<GridCell> LargeCells(Structure structure, Vector3 position, Quaternion rotation)
    {
        List<GridCell> cells = new List<GridCell>();
        if (structure.GridBounds?._grids == null)
        {
            return cells;
        }

        foreach (Grid3 grid in structure.GridBounds._grids)
        {
            Vector3 world = rotation * grid.ToVector3() + position;
            GridCell cell = PieceShapes.Cell(GridController.World.WorldToLocalGrid(world));
            if (!cells.Contains(cell))
            {
                cells.Add(cell);
            }
        }

        return cells;
    }

    /// <summary>The body of a standing structure, or of a prefab at a planned position and turn.</summary>
    internal static BodyView ViewOf(Structure structure, Vector3 position, Quaternion rotation)
    {
        List<GridCell> small = SmallCells(structure, position, rotation);
        return new BodyView(V(position), RenderBox(structure, position, rotation),
            small.Count > 0 ? Box3.OfSmallCells(small) : (Box3?)null, small.Count);
    }

    internal static BodyView ViewOf(Structure structure) =>
        ViewOf(structure, structure.ThingTransformPosition, structure.ThingTransformRotation);

    /// <summary>A point in a thing's own frame: metres right, up and forward of its origin.</summary>
    internal static LocalOffsetView Local(Thing thing, Vector3 world)
    {
        Vector3 local = Quaternion.Inverse(thing.ThingTransformRotation) * (world - thing.ThingTransformPosition);
        return new LocalOffsetView(local.x, local.y, local.z);
    }
}
