#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game.Structures;

/// <summary>
/// Where a large structure registers, as GridController.AddGridStructure places it. A structure placed on the grid
/// (PlacementSnap.Grid: frames) takes the Center slot of each cell of its GridBounds at its position and rotation. Any
/// other (walls) takes, for each BlockingGrids point, the slot at that point in the cell on its CenterPosition's side
/// (Grid3.GridCenter(CenterPosition), FaceMath.CellOnOriginSide). A prefab not yet placed has no BlockingGrids; they
/// are its protected blockingGrids turned by the rotation and moved to the position, as Structure.OnAssignedReference
/// makes them (one point at the position when it has none), and its CenterPosition is the position plus the rotated
/// Bounds.center (Wall.CenterPosition).
/// </summary>
internal static class StructureSlots
{
    internal static List<StructureSlot> Live(Structure structure)
    {
        List<StructureSlot> slots = new List<StructureSlot>();
        if (structure.PlacementType == PlacementSnap.Grid)
        {
            AddGridCells(slots, structure.GridBounds, structure.RegisteredPosition, structure.RegisteredRotation);
            return slots;
        }

        Vector3 centre = structure.CenterPosition;
        foreach (Grid3 point in structure.BlockingGrids ?? new Grid3[0])
        {
            GridPoint face = PointOf(point);
            slots.Add(new StructureSlot(FaceMath.CellOnOriginSide(face, centre.x, centre.y, centre.z), face));
        }

        return slots;
    }

    /// <summary>The slots the prefab would take where the old piece stands; null when unreadable.</summary>
    internal static List<StructureSlot>? Predicted(Structure prefab, Structure old)
    {
        Vector3 position = old.ThingTransformPosition;
        Quaternion rotation = old.ThingTransformRotation;
        List<StructureSlot> slots = new List<StructureSlot>();
        if (prefab.PlacementType == PlacementSnap.Grid)
        {
            if (prefab.GridBounds == null || !prefab.GridBounds.IsValid())
            {
                return null;
            }

            AddGridCells(slots, prefab.GridBounds, position, rotation);
            return slots;
        }

        Vector3 centre = position + rotation * prefab.Bounds.center;
        Vector3[]? local = GameMembers.StructureBlockingGrids.GetValue(prefab) as Vector3[];
        if (local == null || local.Length == 0)
        {
            GridPoint only = PointOf(new Grid3(position));
            slots.Add(new StructureSlot(FaceMath.CellOnOriginSide(only, centre.x, centre.y, centre.z), only));
            return slots;
        }

        foreach (Vector3 offset in local)
        {
            GridPoint face = PointOf(new Grid3(rotation * offset + position));
            slots.Add(new StructureSlot(FaceMath.CellOnOriginSide(face, centre.x, centre.y, centre.z), face));
        }

        return slots;
    }

    /// <summary>What holds the slot now: the cell's StructuralArray element, or null (no cell, empty slot).</summary>
    internal static Structure? Holder(StructureSlot slot)
    {
        Cell? cell = GridController.World.GetCell(GridOf(slot.Cell));
        if (cell == null)
        {
            return null;
        }

        Structure holder = cell.Lookup[GridOf(slot.Offset)];
        return holder != null ? holder : null;
    }

    internal static bool Holds(StructureSlot slot, Structure structure)
    {
        Cell? cell = GridController.World.GetCell(GridOf(slot.Cell));
        return cell != null && cell.Lookup[GridOf(slot.Offset)] == structure && cell.AllStructures.Contains(structure);
    }

    internal static bool HoldsAll(List<StructureSlot> slots, Structure structure)
    {
        foreach (StructureSlot slot in slots)
        {
            if (!Holds(slot, structure))
            {
                return false;
            }
        }

        return slots.Count > 0;
    }

    /// <summary>
    /// Puts the old piece back into every slot it no longer holds, the way registration does (Cell.Add, which hands a
    /// slot over only when its holder is being destroyed), then refreshes its air state. A slot held by anything else
    /// that is not being destroyed is left alone: Cell.Add would refuse it and destroy the old piece. True when the
    /// old piece holds every slot afterwards.
    /// </summary>
    internal static bool Restore(Structure old, List<StructureSlot> slots)
    {
        GridController grid = GridController.World;
        foreach (StructureSlot slot in slots)
        {
            if (Holds(slot, old))
            {
                continue;
            }

            Cell? cell = grid.GetCell(GridOf(slot.Cell));
            Structure? holder = Holder(slot);
            if (cell == null || (holder != null && !holder.IsBeingDestroyed))
            {
                continue;
            }

            cell.Add(old, GridOf(slot.Point));
        }

        grid.UpdateAirState(old);
        return HoldsAll(slots, old);
    }

    /// <summary>The structures registered on a face point (GridController.FaceLookup), by reference id.</summary>
    internal static HashSet<long> FaceHolders(GridPoint face)
    {
        HashSet<long> holders = new HashSet<long>();
        foreach (Structure structure in new List<Structure>(GridController.World.GetFaceStructures(GridOf(face))))
        {
            if (structure != null && !structure.IsBeingDestroyed)
            {
                holders.Add(structure.ReferenceId);
            }
        }

        return holders;
    }

    internal static GridPoint PointOf(Grid3 grid) => new GridPoint(grid.x, grid.y, grid.z);

    internal static Grid3 GridOf(GridPoint point) => new Grid3(point.X, point.Y, point.Z);

    internal static Vector3 MetresOf(GridPoint point) => new Vector3(point.X / 10f, point.Y / 10f, point.Z / 10f);

    // GridBounds.GetLocalGrids, which takes a Span the mod cannot reference: each bounds cell turned and moved, then
    // snapped to its 2 m cell (GridController.WorldToLocalGrid).
    private static void AddGridCells(List<StructureSlot> slots, GridBounds? bounds, Vector3 position,
        Quaternion rotation)
    {
        if (bounds == null || bounds._grids == null)
        {
            return;
        }

        foreach (Grid3 local in bounds._grids)
        {
            GridPoint cell = PointOf(GridController.World.WorldToLocalGrid(rotation * local.ToVector3() + position));
            slots.Add(new StructureSlot(cell, cell));
        }
    }
}
