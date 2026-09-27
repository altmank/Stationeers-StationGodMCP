#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// Every StationGod Gateway on the grid: added when one registers or finishes loading, removed when it deregisters or
/// is destroyed (StationGodGateway). Locked: those callbacks and the API both use it on the main thread, but a
/// destroyed gateway is dropped here too.
/// </summary>
internal static class GatewayRegistry
{
    private static readonly object Sync = new object();
    private static readonly HashSet<StationGodGateway> Gateways = new HashSet<StationGodGateway>();

    internal static void Register(StationGodGateway? gateway)
    {
        if (gateway == null)
        {
            return;
        }

        lock (Sync)
        {
            Gateways.Add(gateway);
        }
    }

    internal static void Unregister(StationGodGateway gateway)
    {
        lock (Sync)
        {
            Gateways.Remove(gateway);
        }
    }

    /// <summary>The live gateways, by reference id.</summary>
    internal static List<StationGodGateway> Snapshot()
    {
        List<StationGodGateway> gateways;
        lock (Sync)
        {
            // Unity's == reports a destroyed gateway as null.
            Gateways.RemoveWhere(static gateway => gateway == null);
            gateways = new List<StationGodGateway>(Gateways);
        }

        gateways.Sort(static (a, b) => a.ReferenceId.CompareTo(b.ReferenceId));
        return gateways;
    }

    internal static bool TryGet(long referenceId, out StationGodGateway? gateway)
    {
        foreach (StationGodGateway candidate in Snapshot())
        {
            if (candidate.ReferenceId == referenceId)
            {
                gateway = candidate;
                return true;
            }
        }

        gateway = null;
        return false;
    }

    internal static void Clear()
    {
        lock (Sync)
        {
            Gateways.Clear();
        }
    }
}
