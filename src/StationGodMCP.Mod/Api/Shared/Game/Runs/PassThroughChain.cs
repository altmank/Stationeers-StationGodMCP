#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game.Runs;

/// <summary>
/// The switched-on demand behind the pass-through devices of one network after an edit (Pure/PassThroughLoads), read
/// from the live networks: an APC, a transformer (capped by its Setting) or a power transmitter (capped by
/// MaxPowerTransmission) whose input port is on the network pulls, once switched on, what the devices off on its
/// output network require (PortLoads.Dormant, the same ceilings), and on through the pass-through devices there. The
/// networks of the edit are never walked into (their ports are counted directly), and each network behind is counted
/// once for all ports: one chain per forecast network.
/// </summary>
internal sealed class PassThroughChain
{
    private readonly HashSet<long> _seen;
    private readonly Dictionary<long, CableNetwork> _networks = new Dictionary<long, CableNetwork>();
    private readonly Dictionary<long, LoadBehind> _byDevice = new Dictionary<long, LoadBehind>();

    internal PassThroughChain(IEnumerable<long> editNetworks)
    {
        _seen = new HashSet<long>(editNetworks);
    }

    /// <summary>The load behind a device port that is a pass-through device's input; LoadBehind.None otherwise.</summary>
    internal LoadBehind Of(Device? device, int index)
    {
        if (device is not ElectricalInputOutput io || PortLoads.Of(device, index)?.Side != PowerSide.Input)
        {
            return LoadBehind.None;
        }

        if (!_byDevice.TryGetValue(io.ReferenceId, out LoadBehind behind))
        {
            PassThrough? start = PassThroughOf(io);
            behind = start != null ? PassThroughLoads.Of(start, NetworkOf, _seen) : LoadBehind.None;
            _byDevice[io.ReferenceId] = behind;
        }

        return behind;
    }

    // How a device passes power on, with the checks PortLoads.Demand makes: an APC passes its output's demand in
    // error or not, a transformer or another input/output device only out of error; a battery never (its own charge).
    private PassThrough? PassThroughOf(ElectricalInputOutput io)
    {
        CableNetwork? output = io.OutputNetwork;
        if (io is Battery || output == null || (io.Error == 1 && io is not AreaPowerControl))
        {
            return null;
        }

        _networks[output.ReferenceId] = output;
        double passed = output.RequiredLoad;
        return io switch
        {
            Transformer transformer => new PassThrough(io.ReferenceId, output.ReferenceId, passed, transformer.Setting),
            PowerTransmitter => new PassThrough(io.ReferenceId, output.ReferenceId,
                io.OnOff ? passed : PowerTransmitter.MaxPowerTransmission, PowerTransmitter.MaxPowerTransmission),
            _ => new PassThrough(io.ReferenceId, output.ReferenceId, passed, null)
        };
    }

    private NetworkBehind? NetworkOf(long id)
    {
        if (!_networks.TryGetValue(id, out CableNetwork network))
        {
            return null;
        }

        List<SwitchedOnDemand> off = new List<SwitchedOnDemand>();
        List<PassThrough> feeds = new List<PassThrough>();
        foreach (Device device in RunNetworks.Copy(network.DeviceList))
        {
            double required = RequiredOn(device, network);
            if (required > 0.0)
            {
                off.Add(new SwitchedOnDemand(device.ReferenceId, required));
            }

            if (device is ElectricalInputOutput io && io.InputNetwork == network && PassThroughOf(io) is PassThrough feed)
            {
                feeds.Add(feed);
            }
        }

        return new NetworkBehind(off, feeds);
    }

    // What a device off now requires on the network once switched on: each power side on it counted once.
    private static double RequiredOn(Device device, CableNetwork network)
    {
        if (device.OnOff || device.OpenEnds == null)
        {
            return 0.0;
        }

        double required = 0.0;
        HashSet<PowerSide> sides = new HashSet<PowerSide>();
        ElectricalInputOutput? io = device as ElectricalInputOutput;
        for (int index = 0; index < device.OpenEnds.Count; index++)
        {
            PortPower? dormant = PortLoads.Dormant(device, index);
            if (dormant == null || !IsOn(device.OpenEnds[index], dormant.Side, io, network) || !sides.Add(dormant.Side))
            {
                continue;
            }

            required += dormant.RequiredW;
        }

        return required;
    }

    private static bool IsOn(Connection end, PowerSide side, ElectricalInputOutput? io, CableNetwork network) =>
        side switch
        {
            PowerSide.Input => io?.InputNetwork == network,
            PowerSide.Output => io?.OutputNetwork == network,
            _ => CableOf(end)?.CableNetwork == network
        };

    private static Cable? CableOf(Connection end)
    {
        if (end?.Transform == null)
        {
            return null;
        }

        SmallCell? cell = GridController.World.GetSmallCell(end.GetLocalGrid());
        Cable? cable = cell?.Cable;
        return cable != null && !cable.IsBeingDestroyed && cable.IsConnected(end) ? cable : null;
    }
}
