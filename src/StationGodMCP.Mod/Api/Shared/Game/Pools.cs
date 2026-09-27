#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Util;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>Copies of the game's object pools, taken under the pool's own lock.</summary>
internal static class Pools
{
    // ConcurrentDensePool locks on itself for every change (Add, Remove, RemoveWhere: the atmospherics thread removes
    // atmospheres); DensePool.ToList does not lock, so it is called under that same lock.
    internal static List<T> Snapshot<T>(ConcurrentDensePool<T> pool) where T : class, IDensePoolable
    {
        lock (pool)
        {
            return pool.ToList();
        }
    }
}
