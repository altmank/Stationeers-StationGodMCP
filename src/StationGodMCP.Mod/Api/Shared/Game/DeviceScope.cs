#nullable enable

using System.Collections.Generic;
using System.Globalization;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Motherboards;
using Assets.Scripts.Objects.Pipes;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// The devices one API call may touch: every device in the world (gateway_id "world" or none), or, as a filter,
/// those on one StationGod Gateway's data networks. Worn and held logic items are in every scope; a device with the
/// same reference id wins over a worn item.
/// </summary>
internal sealed class DeviceScope
{
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

    /// <summary>Every target in the scope by reference id.</summary>
    internal Dictionary<long, ScopedTarget> DeviceMap()
    {
        Dictionary<long, ScopedTarget> devices = new Dictionary<long, ScopedTarget>();
        if (_isWorld)
        {
            // Device.AllDevices holds every Device structure registered on the grid (Device.OnRegistered,
            // OnDeregistered) whether or not it is cabled or powered. Walking CableNetwork.AllCableNetworks'
            // DataDeviceList would miss a device with no cable at all, such as an airlock door whose cable was
            // destroyed.
            foreach (Device device in Device.AllDevices.ToList())
            {
                Add(devices, ScopedTarget.From(device));
            }
        }
        else
        {
            foreach (Device device in GatewayDevices()!.Values)
            {
                Add(devices, ScopedTarget.From(device));
            }
        }

        // Worn and held items never sit on a cable network. They are reachable through any scope; a cabled device
        // with the same reference id wins.
        foreach (ScopedTarget worn in ScopedTarget.Worn())
        {
            if (!devices.ContainsKey(worn.ReferenceId))
            {
                devices[worn.ReferenceId] = worn;
            }
        }

        return devices;
    }

    /// <summary>Every target in the scope, by reference id.</summary>
    internal List<ScopedTarget> SortedDevices()
    {
        List<ScopedTarget> devices = new List<ScopedTarget>(DeviceMap().Values);
        devices.Sort(static (a, b) => a.ReferenceId.CompareTo(b.ReferenceId));
        return devices;
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
    internal static int CountWorld()
    {
        HashSet<long> ids = new HashSet<long>();
        foreach (Device device in Device.AllDevices.ToList())
        {
            Thing? thing = device != null ? ((ILogicable)device).GetAsThing : null;
            if (thing != null)
            {
                ids.Add(thing.ReferenceId);
            }
        }

        foreach (ScopedTarget worn in ScopedTarget.Worn())
        {
            ids.Add(worn.ReferenceId);
        }

        return ids.Count;
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
