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

    /// <summary>
    /// Every target in the scope by reference id, read now (a write's: a gateway's data networks walked, never its kept
    /// map). The world scope's devices are the world index's Device class, checked against the game's pool.
    /// </summary>
    internal Dictionary<long, ScopedTarget> DeviceMap()
    {
        Dictionary<long, ScopedTarget> devices = new Dictionary<long, ScopedTarget>();
        if (_isWorld)
        {
            foreach (object? _ in Scan(devices))
            {
            }

            return devices;
        }

        foreach (Device device in GatewayMaps.Build(Gateway!).Values)
        {
            Add(devices, ScopedTarget.From(device));
        }

        AddWorn(devices);
        return devices;
    }

    /// <summary>Whether a gateway scope's data networks hold the device now (live; the world scope holds every one).</summary>
    internal bool Reaches(Device device)
    {
        if (_isWorld)
        {
            return true;
        }

        foreach (CableNetwork network in Gateway!.GetDataNetworks())
        {
            List<Device>? visible = network?.DataDeviceList;
            if (visible != null && visible.Contains(device))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the scope holds the device, for a read: a gateway scope's followed map (GatewayMaps, rebuilt only when a
    /// data network changed), so a sample of many devices costs a lookup each; the world scope holds every one.
    /// </summary>
    internal bool Holds(Device device) =>
        _isWorld || (((ILogicable)device).GetAsThing is Thing thing && thing != null &&
                     GatewayMaps.Of(Gateway!).TryGetValue(thing.ReferenceId, out Device kept) &&
                     ReferenceEquals(kept, device));

    /// <summary>
    /// DeviceMap spread over frames (a Work or ScanBuild body): the world scope's Device class candidates checked one per
    /// step, or the gateway's devices (its followed map, GatewayMaps) one per step; then the worn and held items, each
    /// joining where no device holds its id. Reads only: a write takes DeviceMap.
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
            foreach (Device device in GatewayMaps.Of(Gateway!).Values)
            {
                Add(devices, ScopedTarget.From(device));
                yield return null;
            }
        }

        AddWorn(devices);
    }

    // The worn and held items, each joining where no device holds its id.
    private static void AddWorn(Dictionary<long, ScopedTarget> devices)
    {
        foreach (ScopedTarget worn in ScopedTarget.Worn())
        {
            if (!devices.ContainsKey(worn.ReferenceId))
            {
                devices[worn.ReferenceId] = worn;
            }
        }
    }

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
            string? unavailable = gateway.GetUnavailableReason();
            gateways.Add(new Views.GatewayView(new ThingId(gateway.ReferenceId).ToString(), gateway.DisplayName,
                gateway.PrefabName, new Views.GatewayState(unavailable == null, unavailable ?? "ready"),
                new Views.GatewayCounts(gateway.GetDataNetworks().Count, GatewayMaps.Of(gateway).Count, worn)));
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

    private static void Add(Dictionary<long, ScopedTarget> devices, ScopedTarget? target)
    {
        if (target != null)
        {
            devices[target.ReferenceId] = target;
        }
    }
}
