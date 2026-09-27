#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game.Structures;

/// <summary>
/// replace_walls: walls and windows. A swap takes and builds exactly Wall or WallTransparent, so shuttered windows,
/// their connectors, floors, ladder platforms and crew umbilical doors (Wall subclasses with behaviour of their own)
/// are never touched. A wall sits on faces: it affects the two cells each face separates, and the new wall must bear
/// each face's pressure difference by the game's stress rule (WallStress) or the run is refused.
/// </summary>
internal sealed class WallFamily : StructureFamily
{
    internal override string Tool => "replace_walls";

    internal override string Noun => "wall";

    internal override bool TargetRequired => true;

    internal override string PlainClasses => "Wall or WallTransparent";

    internal override bool IsMember(Thing thing) => thing is Wall;

    internal override bool IsPlain(Structure structure)
    {
        Type type = structure.GetType();
        return type == typeof(Wall) || type == typeof(WallTransparent);
    }

    // Every wall registered on a face of one of the room's cells (GridController.FaceLookup holds each face point's
    // structures, whichever of the two cells they registered in).
    internal override void CollectInRoom(Room room, List<Structure> into, HashSet<long> seen)
    {
        GridController grid = GridController.World;
        foreach (WorldGrid cell in new List<WorldGrid>(room.Grids))
        {
            foreach (GridPoint face in FaceMath.FacesOf(StructureSlots.PointOf(cell.Value)))
            {
                HashSet<Structure> held = grid.GetFaceStructures(StructureSlots.GridOf(face));
                foreach (Structure structure in new List<Structure>(held))
                {
                    if (structure is Wall wall)
                    {
                        AddOnce(wall, into, seen);
                    }
                }
            }
        }
    }

    internal override Structure? DefaultTarget(Structure old) => null;

    internal override List<GridPoint> AffectedCells(PlannedStructureSwap swap)
    {
        List<GridPoint> cells = new List<GridPoint>();
        foreach (StructureSlot slot in swap.Slots)
        {
            if (FaceMath.TrySplitFace(slot.Point, out GridPoint a, out GridPoint b))
            {
                AddCell(cells, a);
                AddCell(cells, b);
            }
            else
            {
                AddCell(cells, slot.Cell);
            }
        }

        return cells;
    }

    internal override void Assess(PlannedStructureSwap swap, StructureSwapPlan plan)
    {
        AtmosphericsController air = AtmosphericsController.World;
        GridController grid = GridController.World;
        double targetDelta = swap.After.Air ? swap.Target.MaxPressureDelta.ToDouble() : 0.0;
        double oldDelta = !swap.Old.CanAirPass ? swap.Old.MaxPressureDelta.ToDouble() : 0.0;
        List<WallFace> faces = new List<WallFace>(swap.Slots.Count);
        foreach (StructureSlot slot in swap.Slots)
        {
            if (!FaceMath.TrySplitFace(slot.Point, out GridPoint a, out GridPoint b))
            {
                continue;
            }

            double pressureA = PressureOf(air, a);
            double pressureB = PressureOf(air, b);
            double others = OtherFaceDelta(grid, slot.Point, swap.Old);
            bool shielded = IsBlocked(grid, a) || IsBlocked(grid, b);
            StructureFacePressure pressure = new StructureFacePressure(pressureA, pressureB,
                others + Math.Max(0.0, oldDelta), others + Math.Max(0.0, targetDelta));
            StressVerdict verdict = WallStress.Judge(new FaceLoad(pressure.Difference, others, shielded), targetDelta,
                Thing.StressedRatio);
            faces.Add(new WallFace(slot.Point, a, b, pressure, shielded ? "shielded" : NameOf(verdict)));
            if (verdict == StressVerdict.Overstressed)
            {
                plan.Problem("would_overstress",
                    $"{swap.Old.PrefabName} {swap.OldId}: the face at {slot.Point} holds a " +
                    $"{pressure.Difference:0.#} kPa difference; with {swap.Target.PrefabName} it bears " +
                    $"{pressure.FaceSumAfter:0.#} kPa, so the game would damage the new wall until it breaks.",
                    swap.Old);
            }

            swap.Stressed |= verdict == StressVerdict.Stressed;
        }

        swap.Faces = faces;
    }

    // What SampleGlobalAtmosphere gives the cell (its own atmosphere or the planet's), as ReactWithStructures reads a
    // neighbour; none reads as 0.
    private static double PressureOf(AtmosphericsController air, GridPoint cell)
    {
        Atmosphere? atmosphere = air.SampleGlobalAtmosphere(new WorldGrid(StructureSlots.GridOf(cell)));
        return atmosphere != null ? atmosphere.PressureGassesAndLiquids.ToDouble() : 0.0;
    }

    // Atmosphere.GetFaceMaxPressureDelta without the wall being replaced: air-blocking structures with a positive
    // MaxPressureDelta.
    private static double OtherFaceDelta(GridController grid, GridPoint face, Structure old)
    {
        double sum = 0.0;
        foreach (Structure structure in new List<Structure>(grid.GetFaceStructures(StructureSlots.GridOf(face))))
        {
            if (structure != null && structure != old && !structure.CanAirPass &&
                structure.MaxPressureDelta.ToDouble() > 0.0)
            {
                sum += structure.MaxPressureDelta.ToDouble();
            }
        }

        return sum;
    }

    private static bool IsBlocked(GridController grid, GridPoint cell)
    {
        Cell? found = grid.GetCell(StructureSlots.GridOf(cell));
        return found != null && found.IsBlocked;
    }

    private static string NameOf(StressVerdict verdict) => verdict switch
    {
        StressVerdict.Overstressed => "overstressed",
        StressVerdict.Stressed => "stressed",
        _ => "ok"
    };

    private static void AddCell(List<GridPoint> cells, GridPoint cell)
    {
        if (!cells.Contains(cell))
        {
            cells.Add(cell);
        }
    }
}
