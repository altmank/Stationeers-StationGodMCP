#nullable enable

using System.Collections.Generic;
using System.Globalization;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Motherboards;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.WorldModel;
using StationGodMCP.Pure.Scheduling;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// The devices one API call may touch: every device in the world (gateway_id "world" or none), or, as a filter,
/// those on one StationGod Gateway's data networks. Worn and held logic items are in every scope; a device with the
/// same reference id wins over a worn item.
/// </summary>
internal sealed class DeviceScope
{
    // Device class candidates CountingWorld checks per step.
    private const int CandidatesPerStep = 256;

    internal const string WorldId = "world";

    private readonly bool _isWorld;

    private Dictionary<long, Device>? _gatewayDevices;

    private DeviceScope(StationGodGateway? gateway, bool isWorld)
    {
        Gateway = gateway;
        _isWorld = isWorld;
    }

    internal static DeviceScope World { get; } = new DeviceScope(null, true);

    internal static DeviceScope ForGateway(StationGodGateway gateway) => new DeviceScope(gateway, false);

    /// <summary>The real gateway, or null for the world scope.</summary>
    internal StationGodGateway? Gateway { get; }

    internal bool IsWorld => _isWorld;

    /// <summary>The gateway_id callers pass and replies echo: the gateway's reference id, or "world".</summary>
    internal string Id => _isWorld ? WorldId : Gateway!.ReferenceId.ToString(CultureInfo.InvariantCulture);

    /// <summary>Where a device must be to be in this scope, for error messages.</summary>
    internal string Where => _isWorld ? "in the world" : $"on a data network connected to gateway {Id}";

    internal bool IsGatewayItself(ScopedTarget device) =>
        _isWorld ? device.Thing is StationGodGateway : ReferenceEquals(device.Thing, Gateway);

    /// <summary>Every target in the scope by reference id (Scan taken to its end).</summary>
    internal Dictionary<long, ScopedTarget> DeviceMap()
    {
        Dictionary<long, ScopedTarget> devices = new Dictionary<long, ScopedTarget>();
        foreach (object? _ in Scan(devices))
        {
        }

        return devices;
    }

    /// <summary>
    /// DeviceMap spread over frames (a Work or ScanBuild body): the world scope's Device class candidates checked one per
    /// step, or the gateway's data networks' devices one per step; then the worn and held items, each joining where
    /// no device holds its id. devices ends as DeviceMap answers.
    /// </summary>
    internal IEnumerable<object?> Scan(Dictionary<long, ScopedTarget> devices)
    {
        if (_isWorld)
        {
            List<Thing> candidates = StationGodMCP.WorldModel.World.Candidates(WorldClass.Device);
            yield return null;
            for (int index = 0; index < candidates.Count; index++)
            {
                if (StationGodMCP.WorldModel.World.Holds(WorldClass.Device, candidates[index]) &&
                    candidates[index] is Device device)
                {
                    Add(devices, ScopedTarget.From(device));
                }

                yield return null;
            }
        }
        else
        {
            Dictionary<long, Device> gateway = new Dictionary<long, Device>();
            foreach (CableNetwork network in Gateway!.GetDataNetworks())
            {
                List<Device>? visible = network?.DataDeviceList;
                for (int index = 0; visible != null && index < visible.Count; index++)
                {
                    Device device = visible[index];
                    Thing? thing = device != null ? ((ILogicable)device).GetAsThing : null;
                    if (thing != null)
                    {
                        gateway[thing.ReferenceId] = device!;
                    }

                    yield return null;
                }
            }

            _gatewayDevices = gateway;
            foreach (Device device in gateway.Values)
            {
                Add(devices, ScopedTarget.From(device));
                yield return null;
            }
        }

        foreach (ScopedTarget worn in ScopedTarget.Worn())
        {
            if (!devices.ContainsKey(worn.ReferenceId))
            {
                devices[worn.ReferenceId] = worn;
            }
        }
    }

    /// <summary>
    /// The devices on the gateway's data networks (CableNetwork.DataDeviceList) by reference id, built once per scope
    /// instance (a gateway scope is made per request). Null for the world scope.
    /// </summary>
    internal Dictionary<long, Device>? GatewayDevices()
    {
        if (_isWorld)
        {
            return null;
        }

        if (_gatewayDevices == null)
        {
            Dictionary<long, Device> devices = new Dictionary<long, Device>();
            foreach (CableNetwork network in Gateway!.GetDataNetworks())
            {
                AddNetwork(devices, network);
            }

            _gatewayDevices = devices;
        }

        return _gatewayDevices;
    }

    private static void AddNetwork(Dictionary<long, Device> devices, CableNetwork? network)
    {
        List<Device>? visible = network?.DataDeviceList;
        if (visible == null)
        {
            return;
        }

        foreach (Device device in visible)
        {
            Thing? thing = device != null ? ((ILogicable)device).GetAsThing : null;
            if (thing != null)
            {
                devices[thing.ReferenceId] = device!;
            }
        }
    }

    /// <summary>How many targets DeviceMap would hold for the world scope, without building it.</summary>
    /// <summary>
    /// The scopes a gateway option takes: "world" first, every device in the world (its status "bypass"), then each
    /// StationGod Gateway (GatewayRegistry), whose id narrows a call to its data networks, with its status and counts.
    /// A Work body: the world's devices counted a batch per step, a gateway per step.
    /// </summary>
    internal static IEnumerable<object?> Listing(Answer<List<Views.GatewayView>> answer)
    {
        int worn = ScopedTarget.Worn().Count;
        Answer<int> world = new Answer<int>();
        foreach (object? step in CountingWorld(world))
        {
            yield return step;
        }

        List<Views.GatewayView> gateways = new List<Views.GatewayView>
        {
            new Views.GatewayView(WorldId, "Whole world", null, new Views.GatewayState(true, "bypass"),
                new Views.GatewayCounts(0, world.Value, worn))
        };

        foreach (StationGodGateway gateway in GatewayRegistry.Snapshot())
        {
            List<CableNetwork> networks = gateway.GetDataNetworks();
            string? unavailable = gateway.GetUnavailableReason();
            gateways.Add(new Views.GatewayView(new ThingId(gateway.ReferenceId).ToString(), gateway.DisplayName,
                gateway.PrefabName, new Views.GatewayState(unavailable == null, unavailable ?? "ready"),
                new Views.GatewayCounts(networks.Count, CountDevices(networks), worn)));
            yield return null;
        }

        answer.Value = gateways;
    }

    // Every device in the world and every worn or held logic item, each once: the Device class candidates checked
    // CandidatesPerStep per step.
    private static IEnumerable<object?> CountingWorld(Answer<int> answer)
    {
        HashSet<long> ids = new HashSet<long>();
        List<Thing> candidates = StationGodMCP.WorldModel.World.Candidates(WorldClass.Device);
        for (int index = 0; index < candidates.Count; index++)
        {
            if (candidates[index] is Device device &&
                StationGodMCP.WorldModel.World.Holds(WorldClass.Device, device) &&
                ((ILogicable)device).GetAsThing is Thing thing && thing != null)
            {
                ids.Add(thing.ReferenceId);
            }

            if (index % CandidatesPerStep == CandidatesPerStep - 1)
            {
                yield return null;
            }
        }

        foreach (ScopedTarget worn in ScopedTarget.Worn())
        {
            ids.Add(worn.ReferenceId);
        }

        answer.Value = ids.Count;
    }

    /// <summary>The devices on these data networks, each once.</summary>
    internal static int CountDevices(List<CableNetwork> networks)
    {
        Dictionary<long, Device> devices = new Dictionary<long, Device>();
        foreach (CableNetwork network in networks)
        {
            AddNetwork(devices, network);
        }

        return devices.Count;
    }

    private static void Add(Dictionary<long, ScopedTarget> devices, ScopedTarget? target)
    {
        if (target != null)
        {
            devices[target.ReferenceId] = target;
        }
    }
}
