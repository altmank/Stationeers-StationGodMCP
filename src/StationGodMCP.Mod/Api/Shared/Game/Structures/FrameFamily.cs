#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Objects.Structures;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game.Structures;

/// <summary>
/// replace_frames: frames (iron, steel, corner, side). A swap takes and builds exactly Frame, so rocket towers are
/// never touched. Without a target a frame is replaced by its own prefab at its final state, which finishes an
/// unfinished frame in place. A frame fills its cell's centre: it affects that cell and its six neighbours. A frame
/// that starts blocking air or gravity (finishing it) needs its cell empty of everything else but the walls on its
/// faces, which must still stand after the swap.
/// </summary>
internal sealed class FrameFamily : StructureFamily
{
    private const float HalfCellInside = 0.95f;

    // The small-grid (0.5 m) positions strictly inside a 2 m cell, relative to its centre: small cells sit at every
    // 0.5 m (Grid3 with SmallGridSize 0.5, SmallGridOffset 0.25), and those at +-1 m lie on the cell's faces.
    private static readonly float[] SmallOffsets = { -0.5f, 0f, 0.5f };

    internal override string Tool => "replace_frames";

    internal override string Noun => "frame";

    internal override bool TargetRequired => false;

    internal override string PlainClasses => "Frame";

    internal override bool IsMember(Thing thing) => thing is Frame;

    internal override bool IsPlain(Structure structure) => structure.GetType() == typeof(Frame);

    // A frame that lets gravity pass can be inside the room; any frame can wall it, from a neighbour cell.
    internal override void CollectInRoom(Room room, List<Structure> into, HashSet<long> seen)
    {
        GridController grid = GridController.World;
        foreach (WorldGrid worldGrid in new List<WorldGrid>(room.Grids))
        {
            GridPoint cell = StructureSlots.PointOf(worldGrid.Value);
            AddFrameAt(grid, cell, into, seen);
            foreach (GridPoint neighbour in FaceMath.Neighbours(cell))
            {
                AddFrameAt(grid, neighbour, into, seen);
            }
        }
    }

    internal override Structure? DefaultTarget(Structure old) => StructureTargets.Registered(old.PrefabHash);

    internal override List<GridPoint> AffectedCells(PlannedStructureSwap swap)
    {
        List<GridPoint> cells = new List<GridPoint>();
        foreach (StructureSlot slot in swap.Slots)
        {
            AddCell(cells, slot.Cell);
            foreach (GridPoint neighbour in FaceMath.Neighbours(slot.Cell))
            {
                AddCell(cells, neighbour);
            }
        }

        return cells;
    }

    internal override void Assess(PlannedStructureSwap swap, StructureSwapPlan plan)
    {
        GridController grid = GridController.World;
        foreach (StructureSlot slot in swap.Slots)
        {
            foreach (GridPoint face in FaceMath.FacesOf(slot.Cell))
            {
                swap.GuardedFaces.Add(new GuardedFace(face, StructureSlots.FaceHolders(face)));
            }

            if (swap.Change != AirChange.Seals)
            {
                continue;
            }

            if (!swap.Before.Gravity && swap.After.Gravity)
            {
                swap.SealedCells.Add(slot.Cell);
            }

            if (!swap.Before.Air && swap.After.Air)
            {
                swap.DividedCells.Add(slot.Cell);
            }

            string? occupant = OccupantOf(grid, slot.Cell, swap.Old);
            if (occupant != null)
            {
                plan.Problem("cell_occupied",
                    $"{swap.Old.PrefabName} {swap.OldId}: finishing it would close its cell, which holds {occupant}. " +
                    "Clear the cell first.", swap.Old);
            }
        }
    }

    // What else is in the frame's cell: another structure of the cell (walls on its faces excepted), a small-grid
    // piece inside it, or a player, creature or loose item there (found by the physics colliders in the cell and the
    // players' positions; no heap scan).
    private static string? OccupantOf(GridController grid, GridPoint cellPoint, Structure frame)
    {
        Cell? cell = grid.GetCell(StructureSlots.GridOf(cellPoint));
        if (cell != null)
        {
            foreach (Structure structure in new List<Structure>(cell.AllStructures))
            {
                if (structure != null && structure != frame && !structure.IsBeingDestroyed &&
                    structure.StructureCollisionType != CollisionType.BlockFace)
                {
                    return $"{structure.PrefabName} {structure.ReferenceId}";
                }
            }
        }

        Vector3 centre = StructureSlots.MetresOf(cellPoint);
        return SmallGridOccupant(grid, centre) ?? DynamicOccupant(centre);
    }

    private static string? SmallGridOccupant(GridController grid, Vector3 centre)
    {
        foreach (float x in SmallOffsets)
        {
            foreach (float y in SmallOffsets)
            {
                foreach (float z in SmallOffsets)
                {
                    SmallCell? small = grid.GetSmallCell(centre + new Vector3(x, y, z));
                    Thing? occupant = small == null ? null
                        : small.Device != null ? small.Device
                        : small.Pipe != null ? small.Pipe
                        : small.Cable != null ? small.Cable
                        : small.Chute != null ? small.Chute
                        : small.Other != null ? small.Other
                        : null;
                    if (occupant != null)
                    {
                        return $"{occupant.PrefabName} {occupant.ReferenceId}";
                    }

                    if (small?.Rail != null)
                    {
                        return "a robotic arm rail";
                    }
                }
            }
        }

        return null;
    }

    private static string? DynamicOccupant(Vector3 centre)
    {
        foreach (Human human in new List<Human>(Human.AllHumans))
        {
            if (human != null && !human.IsBeingDestroyed && Inside(human.Position, centre))
            {
                return $"{human.DisplayName} {human.ReferenceId}";
            }
        }

        foreach (Collider collider in Physics.OverlapBox(centre, Vector3.one * HalfCellInside))
        {
            DynamicThing? thing = collider != null ? collider.GetComponentInParent<DynamicThing>() : null;
            if (thing != null && !thing.IsBeingDestroyed && thing.ParentSlot == null)
            {
                return $"{thing.PrefabName} {thing.ReferenceId}";
            }
        }

        return null;
    }

    private static bool Inside(Vector3 position, Vector3 centre) =>
        Mathf.Abs(position.x - centre.x) < 1f && Mathf.Abs(position.y - centre.y) < 1f &&
        Mathf.Abs(position.z - centre.z) < 1f;

    private static void AddFrameAt(GridController grid, GridPoint cellPoint, List<Structure> into, HashSet<long> seen)
    {
        Cell? cell = grid.GetCell(StructureSlots.GridOf(cellPoint));
        if (cell?.Lookup[StructureElement.Center] is Frame frame)
        {
            AddOnce(frame, into, seen);
        }
    }

    private static void AddCell(List<GridPoint> cells, GridPoint cell)
    {
        if (!cells.Contains(cell))
        {
            cells.Add(cell);
        }
    }
}
