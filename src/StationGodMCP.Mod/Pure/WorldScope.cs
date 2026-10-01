#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// Every store that holds state about one world (keyed by reference ids, which repeat across loads of a save) and must
/// forget it when the world is left: each registers its Clear here once. Observe is called every frame, before any
/// request runs, with whether a world is running; the first frame after a running world is left (a save loads, a new
/// game starts, the menu opens) starts a new epoch and clears every store, so no request of the next world sees a
/// paused chip, a print record, a job or a flight log of the last one. A store whose Clear throws is reported and the
/// others are still cleared.
/// </summary>
internal sealed class WorldScope
{
    private readonly List<(string Name, Action Clear)> _stores = new List<(string, Action)>();
    private bool _inWorld;

    /// <summary>Counts the worlds left since the mod loaded: 0 until the first one is left.</summary>
    internal long Epoch { get; private set; }

    internal IReadOnlyList<(string Name, Action Clear)> Stores => _stores;

    internal void Register(string name, Action clear) => _stores.Add((name, clear));

    /// <summary>
    /// One frame's state. Returns the stores whose Clear failed when this frame started a new epoch (empty
    /// otherwise), with the failure, for the caller to log.
    /// </summary>
    internal List<(string Name, Exception Failure)> Observe(bool worldRunning)
    {
        bool left = _inWorld && !worldRunning;
        _inWorld = worldRunning;
        return left ? ClearAll() : NoFailures;
    }

    private static readonly List<(string Name, Exception Failure)> NoFailures = new List<(string, Exception)>(0);

    private List<(string Name, Exception Failure)> ClearAll()
    {
        Epoch++;
        List<(string Name, Exception Failure)> failures = new List<(string, Exception)>();
        foreach ((string name, Action clear) in _stores)
        {
            try
            {
                clear();
            }
            catch (Exception failure)
            {
                // A store's own Clear: reported by name, and the next store is still cleared.
                failures.Add((name, failure));
            }
        }

        return failures;
    }
}
