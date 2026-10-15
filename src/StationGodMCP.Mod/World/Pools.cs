#nullable enable

using System.Collections.Generic;
using System.Diagnostics;
using Assets.Scripts.Util;
using StationGodMCP.Pure.Profiling;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>Copies of the game's object pools, taken under the pool's own lock.</summary>
internal static class Pools
{
    // ConcurrentDensePool locks on itself for every change (Add, Remove, RemoveWhere: the atmospherics thread removes
    // atmospheres) and for the whole of a ForEach (the game tick's passes over every atmosphere); DensePool.ToList does
    // not lock, so it is called under that same lock, after any pass the tick has under way.
    internal static List<T> Snapshot<T>(ConcurrentDensePool<T> pool) where T : class, IDensePoolable =>
        Snapshot(pool, out _);

    /// <summary>As Snapshot, with the milliseconds spent waiting for the pool's lock.</summary>
    internal static List<T> Snapshot<T>(ConcurrentDensePool<T> pool, out double lockWaitMs)
        where T : class, IDensePoolable
    {
        using ProfScope copying = Prof.Scope(ProfId.PoolSnapshot);
        long asked = Stopwatch.GetTimestamp();
        lock (pool)
        {
            lockWaitMs = (Stopwatch.GetTimestamp() - asked) * 1000.0 / Stopwatch.Frequency;
            return pool.ToList();
        }
    }
}
