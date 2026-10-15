#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Motherboards;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Facts;
using StationGodMCP.WorldModel;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// Each StationGod Gateway's device map, followed (cost design A2): the devices on its data networks
/// (CableNetwork.DataDeviceList, which also holds devices bridged by data transmitters) by reference id, kept until a
/// data network changes. Every change of a cable network's device lists goes through
/// CableNetwork.DirtyPowerAndDataDeviceLists or DirtyDataDeviceList (WorldEvents counts them); a gateway joined to
/// another network changes its InputNetwork1/OutputNetwork1. So a map is rebuilt only when the count moved since it was
/// built or the gateway's networks are others; its cost is the gateway's devices, never the world's. Main thread.
/// <para>
/// Reads only: a write checks its device on the gateway's networks now (DeviceScope.Reaches).
/// Verify mode ([Performance] VerifyFacts): a kept map is also built again and compared.
/// </para>
/// </summary>
[ReplyFact(FactRule.Followed, "the devices on each gateway's data networks")]
internal static class GatewayMaps
{
    private static readonly Dictionary<long, Map> Maps = new Dictionary<long, Map>();
    private static long _built;
    private static long _reused;
    private static long _verified;
    private static long _differences;

    /// <summary>[Performance] VerifyFacts: a kept map is also built again and compared.</summary>
    internal static bool Verify { get; set; }

    internal static bool Ready => WorldEvents.DataNetworksMissing.Length == 0;

    /// <summary>The gateway's devices by reference id; refuses game_changed when a change point is missing.</summary>
    internal static IReadOnlyDictionary<long, Device> Of(StationGodGateway gateway)
    {
        if (!Ready)
        {
            throw new GameChangedException(WorldEvents.DataNetworksMissing);
        }

        // Read before the build: a change while it runs moves the count past it, so the next read builds again.
        long changes = WorldEvents.DataNetworkChanges;
        CableNetwork? input = gateway.InputNetwork1;
        CableNetwork? output = gateway.OutputNetwork1;
        if (Maps.TryGetValue(gateway.ReferenceId, out Map? kept) && kept.Changes == changes &&
            ReferenceEquals(kept.Input, input) && ReferenceEquals(kept.Output, output))
        {
            _reused++;
            if (Verify)
            {
                Compare(gateway, kept.Devices);
            }

            return kept.Devices;
        }

        _built++;
        Dictionary<long, Device> devices = Build(gateway);
        Maps[gateway.ReferenceId] = new Map(changes, input, output, devices);
        return devices;
    }

    /// <summary>server info runtime.facts.gateway_maps.</summary>
    internal static GatewayMapsFactView View() =>
        new GatewayMapsFactView(Ready, Verify, Maps.Count, _built, _reused, WorldEvents.DataNetworkChanges, _verified,
            _differences);

    /// <summary>A world left.</summary>
    internal static void Clear() => Maps.Clear();

    /// <summary>The devices on the gateway's data networks now, by reference id.</summary>
    internal static Dictionary<long, Device> Build(StationGodGateway gateway)
    {
        Dictionary<long, Device> devices = new Dictionary<long, Device>();
        foreach (CableNetwork network in gateway.GetDataNetworks())
        {
            List<Device>? visible = network?.DataDeviceList;
            for (int index = 0; visible != null && index < visible.Count; index++)
            {
                Device device = visible[index];
                Thing? thing = device != null ? ((ILogicable)device).GetAsThing : null;
                if (thing != null)
                {
                    devices[thing.ReferenceId] = device!;
                }
            }
        }

        return devices;
    }

    private static void Compare(StationGodGateway gateway, Dictionary<long, Device> kept)
    {
        Dictionary<long, Device> now = Build(gateway);
        _verified++;
        int same = 0;
        foreach (KeyValuePair<long, Device> entry in now)
        {
            if (kept.TryGetValue(entry.Key, out Device device) && ReferenceEquals(device, entry.Value))
            {
                same++;
            }
        }

        if (same == now.Count && same == kept.Count)
        {
            return;
        }

        _differences++;
        StationGodMod.LogWarning($"Gateway {gateway.ReferenceId}'s device map differs: {kept.Count} kept, " +
                                 $"{now.Count} on its networks now, {same} the same.");
    }

    private sealed class Map
    {
        internal Map(long changes, CableNetwork? input, CableNetwork? output, Dictionary<long, Device> devices)
        {
            Changes = changes;
            Input = input;
            Output = output;
            Devices = devices;
        }

        internal long Changes { get; }

        internal CableNetwork? Input { get; }

        internal CableNetwork? Output { get; }

        internal Dictionary<long, Device> Devices { get; }
    }
}
