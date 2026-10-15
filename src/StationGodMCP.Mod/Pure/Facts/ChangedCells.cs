#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure.Facts;

/// <summary>
/// The large (2 m) cells the game changed since the last Take: told from any thread by the change points (a
/// structure's air or gravity, a room's cells, an edited voxel), taken by the main thread. Cells are the game's Grid3 in
/// decimetres; a large cell is named by its centre, at odd metres. Past MaximumWaiting cells (a frozen main thread, a
/// huge edit) the collector stops listing and says everything changed.
/// </summary>
internal sealed class ChangedCells
{
    /// <summary>Cells listed before the collector gives up and says everything changed.</summary>
    internal const int MaximumWaiting = 100_000;

    /// <summary>A large cell's edge, decimetres.</summary>
    internal const int LargeCellDm = 20;

    // How far from a voxel a cell's air rules read terrain, metres per axis (AtmosphereHelper.CanContainAtmos reads the
    // voxels 1 m around a cell's centre, GetWorldVolume the size-2 voxel at it, a face test the voxels at the face).
    private const float VoxelReachM = 2f;

    private static readonly GridCell[] Steps =
    {
        new GridCell(LargeCellDm, 0, 0), new GridCell(-LargeCellDm, 0, 0), new GridCell(0, LargeCellDm, 0),
        new GridCell(0, -LargeCellDm, 0), new GridCell(0, 0, LargeCellDm), new GridCell(0, 0, -LargeCellDm)
    };

    private readonly object _gate = new object();
    private List<GridCell> _waiting = new List<GridCell>();
    private List<GridCell> _spare = new List<GridCell>();
    private bool _everything;
    private long _told;

    /// <summary>Cells told since the mod loaded (counted, listed or not).</summary>
    internal long Told
    {
        get
        {
            lock (_gate)
            {
                return _told;
            }
        }
    }

    /// <summary>The large cell holding a point given in decimetres (any grid's position). Any thread.</summary>
    internal void Add(int xDm, int yDm, int zDm) => Add(LargeCellOf(xDm, yDm, zDm));

    /// <summary>A large cell, by its centre. Any thread.</summary>
    internal void Add(GridCell cell)
    {
        lock (_gate)
        {
            _told++;
            if (_everything)
            {
                return;
            }

            if (_waiting.Count >= MaximumWaiting)
            {
                _everything = true;
                _waiting.Clear();
                return;
            }

            _waiting.Add(cell);
        }
    }

    /// <summary>An edited voxel at a point in metres: every large cell whose air rules may read it. Any thread.</summary>
    internal void AddVoxel(float xM, float yM, float zM)
    {
        int x0 = FirstCentreFrom(xM - VoxelReachM);
        int y0 = FirstCentreFrom(yM - VoxelReachM);
        int z0 = FirstCentreFrom(zM - VoxelReachM);
        for (int x = x0; x <= xM + VoxelReachM; x += 2)
        {
            for (int y = y0; y <= yM + VoxelReachM; y += 2)
            {
                for (int z = z0; z <= zM + VoxelReachM; z += 2)
                {
                    Add(new GridCell(x * 10, y * 10, z * 10));
                }
            }
        }
    }

    /// <summary>
    /// The cells told since the last Take, into cells (cleared first); false when everything must count as changed
    /// (cells then empty). Main thread.
    /// </summary>
    internal bool Take(List<GridCell> cells)
    {
        cells.Clear();
        lock (_gate)
        {
            if (_everything)
            {
                _everything = false;
                return false;
            }

            (_waiting, _spare) = (_spare, _waiting);
        }

        cells.AddRange(_spare);
        _spare.Clear();
        return true;
    }

    /// <summary>A world left: nothing waits.</summary>
    internal void Clear()
    {
        lock (_gate)
        {
            _waiting.Clear();
            _everything = false;
        }
    }

    /// <summary>The large cell (its centre, decimetres) holding a point given in decimetres.</summary>
    internal static GridCell LargeCellOf(int xDm, int yDm, int zDm) =>
        new GridCell(CentreDm(xDm), CentreDm(yDm), CentreDm(zDm));

    /// <summary>A large cell and its six neighbours, the cells whose frames' faces read it.</summary>
    internal static void Around(GridCell cell, ICollection<GridCell> into)
    {
        into.Add(cell);
        foreach (GridCell step in Steps)
        {
            into.Add(new GridCell(cell.X + step.X, cell.Y + step.Y, cell.Z + step.Z));
        }
    }

    // Centres are at odd metres: a point in [2k, 2k + 2) m is in the cell centred at 2k + 1 m.
    private static int CentreDm(int dm) => FloorDiv(dm, LargeCellDm) * LargeCellDm + LargeCellDm / 2;

    private static int FloorDiv(int value, int by) => value >= 0 ? value / by : -((-value + by - 1) / by);

    // The first odd metre at or above a point, metres.
    private static int FirstCentreFrom(float metres)
    {
        int at = (int)Math.Ceiling(metres);
        return (at & 1) != 0 ? at : at + 1;
    }
}
