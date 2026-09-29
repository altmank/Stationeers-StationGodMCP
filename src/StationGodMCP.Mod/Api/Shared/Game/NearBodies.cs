#nullable enable

using System.Collections.Generic;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>A small-grid thing standing near a place: its mesh box (Thing.Bounds) and the small cells it takes.</summary>
internal sealed class NearBody
{
    internal NearBody(SmallGrid thing, Box3 render, HashSet<GridCell> cells)
    {
        Thing = thing;
        Render = render;
        Cells = cells;
    }

    internal SmallGrid Thing { get; }

    internal Box3 Render { get; }

    internal HashSet<GridCell> Cells { get; }

    /// <summary>It takes one of these cells: it stands there by design (a device on a pipe), not in the way.</summary>
    internal bool Shares(IEnumerable<GridCell> cells)
    {
        foreach (GridCell cell in cells)
        {
            if (Cells.Contains(cell))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Which slots of a small cell NearBodies reads.</summary>
internal enum NearKinds
{
    /// <summary>Devices and mounted things (a cell's Device and Other).</summary>
    Mounted,

    /// <summary>Those, and cables, pipes and chutes.</summary>
    AnyPiece
}

/// <summary>
/// The small-grid things whose bodies may reach into a box: every thing registered in a small cell within Margin of
/// it (a mesh overhangs its cells by up to about 0.5 m), at most maxCells cells scanned, sorted by id.
/// </summary>
internal static class NearBodies
{
    private const double Margin = 1.0;

    private const int DefaultMaximumCells = 4096;

    internal static List<NearBody> Around(Box3 region, GridFacts facts, HashSet<long> ignore, NearKinds kinds,
        int maxCells = DefaultMaximumCells)
    {
        Dictionary<long, SmallGrid> found = new Dictionary<long, SmallGrid>();
        int scanned = 0;
        for (int x = Floor(region.Min.X - Margin); x <= Ceil(region.Max.X + Margin); x += GridStep.CellSize)
        {
            for (int y = Floor(region.Min.Y - Margin); y <= Ceil(region.Max.Y + Margin); y += GridStep.CellSize)
            {
                for (int z = Floor(region.Min.Z - Margin); z <= Ceil(region.Max.Z + Margin); z += GridStep.CellSize)
                {
                    if (++scanned > maxCells)
                    {
                        return Bodies(found);
                    }

                    SmallCell? cell = facts.SmallAt(new GridCell(x, y, z));
                    if (cell == null)
                    {
                        continue;
                    }

                    Add(found, cell.Device, ignore);
                    Add(found, cell.Other, ignore);
                    if (kinds == NearKinds.AnyPiece)
                    {
                        Add(found, cell.Pipe, ignore);
                        Add(found, cell.Cable, ignore);
                        Add(found, cell.Chute, ignore);
                    }
                }
            }
        }

        return Bodies(found);
    }

    private static List<NearBody> Bodies(Dictionary<long, SmallGrid> found)
    {
        List<long> ids = new List<long>(found.Keys);
        ids.Sort();
        List<NearBody> bodies = new List<NearBody>(ids.Count);
        foreach (long id in ids)
        {
            SmallGrid thing = found[id];
            bodies.Add(new NearBody(thing, Game.Bodies.RenderBox(thing),
                new HashSet<GridCell>(Game.Bodies.SmallCells(thing))));
        }

        return bodies;
    }

    private static void Add(Dictionary<long, SmallGrid> things, SmallGrid? thing, HashSet<long> ignore)
    {
        if (thing != null && !thing.IsBeingDestroyed && !ignore.Contains(thing.ReferenceId))
        {
            things[thing.ReferenceId] = thing;
        }
    }

    private static int Floor(double metres) => (int)System.Math.Floor(metres * 2.0) * GridStep.CellSize;

    private static int Ceil(double metres) => (int)System.Math.Ceiling(metres * 2.0) * GridStep.CellSize;
}
