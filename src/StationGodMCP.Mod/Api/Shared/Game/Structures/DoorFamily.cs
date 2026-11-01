#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Structures;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game.Structures;

/// <summary>
/// swap door (2.8.0): replaces doors in place with another door prefab (StructureGlassDoor to StructureAirlock)
/// without opening the doorway, as swap wall does for walls. A swap takes and builds exactly Door, Airlock or
/// UnPoweredDoor. Docks, manual hatches and window shutters are Door subclasses with behaviour of their own, so they are
/// never touched. A door that sits on faces is judged as a wall is (WallFamily.AssessFaces). Its cell and the cells
/// around it are the ones whose air the swap can touch. The new door is planned in the state the old one is left in:
/// a door that stands open is opened again, so it is planned as letting air through. Just before the old door goes,
/// the swap hands over (DoorHandover) everything that points at it, its label, Open and Lock.
/// </summary>
internal sealed class DoorFamily : StructureFamily
{
    internal override string Tool => "swap door";

    internal override string Noun => "door";

    internal override bool TargetRequired => true;

    internal override string PlainClasses => "Door, Airlock or UnPoweredDoor";

    internal override bool IsMember(Thing thing) => thing is Door;

    internal override bool IsPlain(Structure structure)
    {
        Type type = structure.GetType();
        return type == typeof(Door) || type == typeof(Airlock) || type == typeof(UnPoweredDoor);
    }

    // Every door in one of the room's cells or a neighbour, or on one of its cells' faces: a doorway is where the
    // room meets the next.
    internal override IEnumerable<object?> CollectingInRoom(Room room, List<Structure> into, HashSet<long> seen)
    {
        GridController grid = GridController.World;
        List<WorldGrid> cells = new List<WorldGrid>(room.Grids);
        for (int index = 0; index < cells.Count; index++)
        {
            GridPoint cell = StructureSlots.PointOf(cells[index].Value);
            AddDoorsIn(grid, cell, into, seen);
            foreach (GridPoint neighbour in FaceMath.Neighbours(cell))
            {
                AddDoorsIn(grid, neighbour, into, seen);
            }

            foreach (GridPoint face in FaceMath.FacesOf(cell))
            {
                foreach (Structure structure in new List<Structure>(grid.GetFaceStructures(StructureSlots.GridOf(face))))
                {
                    if (structure is Door door)
                    {
                        AddOnce(door, into, seen);
                    }
                }
            }

            if (index % CellsAStep == CellsAStep - 1)
            {
                yield return null;
            }
        }
    }

    internal override Structure? DefaultTarget(Structure old) => null;

    // A door left open is opened again after the swap: planned as letting air through, as it does now.
    internal override PieceBlocking AfterBlocking(Structure old, BuildState last) =>
        old is Door door && door.IsOpen
            ? new PieceBlocking(false, last.BlockGravity)
            : base.AfterBlocking(old, last);

    internal override List<GridPoint> AffectedCells(PlannedStructureSwap swap)
    {
        List<GridPoint> cells = new List<GridPoint>();
        foreach (StructureSlot slot in swap.Slots)
        {
            if (FaceMath.TrySplitFace(slot.Point, out GridPoint a, out GridPoint b))
            {
                AddCell(cells, a);
                AddCell(cells, b);
                continue;
            }

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
        WallFamily.AssessFaces(swap, plan, "door");
        if (swap.Old is Door old)
        {
            swap.Handover = DoorHandover.Plan(old, swap.Target);
        }
    }

    internal override void Handover(PlannedStructureSwap swap, Structure built, StructureSwapLogView log)
    {
        if (!(swap.Old is Door old) || !(built is Door door))
        {
            return;
        }

        log.Handover ??= new List<DoorHandoverView>();
        log.Handover.AddRange(DoorHandover.Act(old, door));
    }

    private static void AddDoorsIn(GridController grid, GridPoint cellPoint, List<Structure> into,
        HashSet<long> seen)
    {
        Cell? cell = grid.GetCell(StructureSlots.GridOf(cellPoint));
        if (cell == null)
        {
            return;
        }

        foreach (Structure structure in new List<Structure>(cell.AllStructures))
        {
            if (structure is Door door)
            {
                AddOnce(door, into, seen);
            }
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
