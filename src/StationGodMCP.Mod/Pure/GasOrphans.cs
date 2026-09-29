#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// Pipe networks the game no longer lists (deregistered: gone from PipeNetwork.AllPipeNetworks) that pipes still name
/// as their network. Nothing simulates such a network, yet its atmosphere keeps whatever it held: when the game merged
/// it away, the survivor received a copy of that gas, so the pipes left on it hold a duplicate (live test 2026-09-29,
/// pipes-1). The readings list them as networks with their pipes and contents, so a family they belong to is not
/// conserved; this sorts the orphans the job left from those that were orphans already before it.
/// </summary>
internal sealed class GasOrphans
{
    private GasOrphans(List<NetworkGas> left, List<NetworkGas> old)
    {
        Left = left;
        Old = old;
    }

    internal static GasOrphans None { get; } = new GasOrphans(new List<NetworkGas>(), new List<NetworkGas>());

    /// <summary>Orphans after the job that were not orphans before it: the job's doing, and a failed check.</summary>
    internal List<NetworkGas> Left { get; }

    /// <summary>Orphans that were there before the job already: reported, not the job's.</summary>
    internal List<NetworkGas> Old { get; }

    internal bool Ok => Left.Count == 0;

    internal static GasOrphans Of(IReadOnlyList<NetworkGas> before, IReadOnlyList<NetworkGas> after)
    {
        HashSet<long> earlier = new HashSet<long>();
        foreach (NetworkGas network in before)
        {
            earlier.Add(network.Id);
        }

        List<NetworkGas> left = new List<NetworkGas>();
        List<NetworkGas> old = new List<NetworkGas>();
        foreach (NetworkGas network in after)
        {
            (earlier.Contains(network.Id) ? old : left).Add(network);
        }

        left.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        old.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        return new GasOrphans(left, old);
    }
}
