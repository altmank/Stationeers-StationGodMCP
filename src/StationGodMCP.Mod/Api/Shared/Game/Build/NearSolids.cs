#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using Objects.Structures;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game.Build;

/// <summary>
/// The solids near a box, from what the grid holds: on each face of every 2 m cell the box reaches into, the plates
/// registered there (GridController.GetFaceStructures; doors left out, a doorway is no surface) with their mesh boxes;
/// each cell's frame (its Center slot) as the 2 m cell it fills; and, for Around, the other things there: the 2 m
/// structures of those cells and the small-grid devices, mounted things and chutes near it (NearBodies; cables and
/// pipes are no obstacle).
/// </summary>
internal static class NearSolids
{
    private const double Reach = 0.01;

    /// <summary>The plates and frames of the 2 m cells the box reaches into. skip: things a plan removes.</summary>
    internal static List<Solid> Surfaces(Box3 box, GridFacts facts, Func<Structure, bool> skip)
    {
        List<Solid> solids = new List<Solid>();
        HashSet<long> seen = new HashSet<long>();
        foreach (GridCell large in LargeCellsOf(box))
        {
            Frame? frame = facts.FrameAt(large);
            if (frame != null && !skip(frame) && seen.Add(frame.ReferenceId))
            {
                Vec3 centre = new Vec3(large.X / 10.0, large.Y / 10.0, large.Z / 10.0);
                solids.Add(new Solid(SolidKind.Frame, NameOf(frame), frame.ReferenceId,
                    new Box3(centre - new Vec3(1, 1, 1), centre + new Vec3(1, 1, 1))));
            }

            foreach (GridStep face in GridStep.All)
            {
                GridCell point = new GridCell(large.X + face.Dx * SmallCellCode.Large / 2,
                    large.Y + face.Dy * SmallCellCode.Large / 2, large.Z + face.Dz * SmallCellCode.Large / 2);
                foreach (Structure plate in facts.FaceStructuresAt(point))
                {
                    if (!Openings.IsDoor(plate) && !skip(plate) && seen.Add(plate.ReferenceId))
                    {
                        solids.Add(new Solid(SolidKind.Plate, NameOf(plate), plate.ReferenceId, Bodies.RenderBox(plate),
                            FacePlane.Of(point), Openings.KindOf(plate) == OpeningKind.Window));
                    }
                }
            }
        }

        return solids;
    }

    /// <summary>
    /// The surfaces and every other solid near the box; things taking one of own's small cells (the body itself, a
    /// device on its pipe) are left out, and so are the ignored ids.
    /// </summary>
    internal static List<Solid> Around(Box3 box, GridFacts facts, HashSet<GridCell> own, HashSet<long> ignore,
        Func<Structure, bool> skip)
    {
        Func<Structure, bool> leave = structure => ignore.Contains(structure.ReferenceId) || skip(structure);
        List<Solid> solids = Surfaces(box, facts, leave);
        HashSet<long> seen = new HashSet<long>();
        foreach (Solid solid in solids)
        {
            if (solid.Id is long id)
            {
                seen.Add(id);
            }
        }

        foreach (GridCell large in LargeCellsOf(box))
        {
            Cell? cell = GridController.World.GetCell(new Vector3(large.X / 10f, large.Y / 10f, large.Z / 10f));
            if (cell?.AllStructures == null)
            {
                continue;
            }

            foreach (Structure structure in new List<Structure>(cell.AllStructures))
            {
                if (structure != null && !(structure is SmallGrid) && !structure.IsBeingDestroyed &&
                    !Openings.IsDoor(structure) && !leave(structure) && seen.Add(structure.ReferenceId))
                {
                    solids.Add(new Solid(SolidKind.Body, NameOf(structure), structure.ReferenceId,
                        Bodies.RenderBox(structure)));
                }
            }
        }

        foreach (NearBody body in NearBodies.Around(box, facts, ignore, NearKinds.AnyPiece))
        {
            SmallGrid thing = body.Thing;
            if (thing is Cable || thing is Piping || body.Shares(own) || skip(thing) || !seen.Add(thing.ReferenceId))
            {
                continue;
            }

            solids.Add(new Solid(SolidKind.Body, NameOf(thing), thing.ReferenceId, body.Render));
        }

        return solids;
    }

    private static string NameOf(Structure structure) =>
        $"{Names.Of(structure)} ({structure.PrefabName} {structure.ReferenceId})";

    // The 2 m cells (centres on odd metres, decimetres) whose box the region reaches into by more than Reach.
    private static List<GridCell> LargeCellsOf(Box3 box)
    {
        List<GridCell> cells = new List<GridCell>();
        int[] min = new int[3];
        int[] max = new int[3];
        for (int axis = 0; axis < 3; axis++)
        {
            min[axis] = (int)Math.Ceiling((box.Min[axis] + Reach - 1.0 - 1.0) / 2.0) * 2 + 1;
            max[axis] = (int)Math.Floor((box.Max[axis] - Reach + 1.0 - 1.0) / 2.0) * 2 + 1;
        }

        for (int x = min[0]; x <= max[0]; x += 2)
        {
            for (int y = min[1]; y <= max[1]; y += 2)
            {
                for (int z = min[2]; z <= max[2]; z += 2)
                {
                    cells.Add(new GridCell(x * 10, y * 10, z * 10));
                }
            }
        }

        return cells;
    }
}
