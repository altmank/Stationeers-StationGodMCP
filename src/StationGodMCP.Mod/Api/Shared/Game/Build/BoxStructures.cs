#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game.Build;

/// <summary>
/// The structures remove structure's box takes: every structure the grid files in a 2 m cell near the box
/// (Cell.AllStructures: frames, walls, large devices) and every small-grid thing in a half-metre cell near it (its
/// device, mounted thing, pipe, cable and chute), each once, whose position lies in the box and whose prefab the
/// filter keeps; in id order.
/// </summary>
internal static class BoxStructures
{
    private const double LargeStep = 2.0;
    private const double SmallStep = 0.5;

    internal static List<ThingId> In(RemovalBox box, PrefabFilter? prefab)
    {
        GridController world = GridController.World;
        Dictionary<long, Structure> found = new Dictionary<long, Structure>();
        foreach (double x in RemovalBox.Steps(box.Min.X, box.Max.X, LargeStep, LargeStep))
        {
            foreach (double y in RemovalBox.Steps(box.Min.Y, box.Max.Y, LargeStep, LargeStep))
            {
                foreach (double z in RemovalBox.Steps(box.Min.Z, box.Max.Z, LargeStep, LargeStep))
                {
                    Cell? cell = world.GetCell(new Vector3((float)x, (float)y, (float)z));
                    if (cell?.AllStructures == null)
                    {
                        continue;
                    }

                    foreach (Structure structure in new List<Structure>(cell.AllStructures))
                    {
                        Consider(found, structure, box, prefab);
                    }
                }
            }
        }

        foreach (double x in RemovalBox.Steps(box.Min.X, box.Max.X, SmallStep, SmallStep))
        {
            foreach (double y in RemovalBox.Steps(box.Min.Y, box.Max.Y, SmallStep, SmallStep))
            {
                foreach (double z in RemovalBox.Steps(box.Min.Z, box.Max.Z, SmallStep, SmallStep))
                {
                    GridCell at = RunArgs.CellOf(new Vector3((float)x, (float)y, (float)z));
                    SmallCell? cell = world.GetSmallCell(new Grid3(at.X, at.Y, at.Z));
                    if (cell == null)
                    {
                        continue;
                    }

                    Consider(found, cell.Device, box, prefab);
                    Consider(found, cell.Other, box, prefab);
                    Consider(found, cell.Pipe, box, prefab);
                    Consider(found, cell.Cable, box, prefab);
                    Consider(found, cell.Chute, box, prefab);
                }
            }
        }

        List<long> ids = new List<long>(found.Keys);
        ids.Sort();
        return ids.ConvertAll(id => new ThingId(id));
    }

    private static void Consider(Dictionary<long, Structure> found, Structure? structure, RemovalBox box,
        PrefabFilter? prefab)
    {
        if (structure == null || structure.IsBeingDestroyed || found.ContainsKey(structure.ReferenceId))
        {
            return;
        }

        Vector3 position = structure.ThingTransformPosition;
        if (box.Holds(position.x, position.y, position.z) &&
            (prefab == null || prefab.Keeps(structure.PrefabName, structure.PrefabHash)))
        {
            found[structure.ReferenceId] = structure;
        }
    }
}
