#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Motherboards;
using Assets.Scripts.Objects.Pipes;
using Assets.Scripts.Util;
using StationGodMCP.Api.Shared.Game.Lint;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Controllers;
using StationGodMCP.Pure.Lint;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// What can write a device (inspect_controllers, driven_by), found on the device's own data networks, the only place
/// the game lets them reach it from:
/// <list type="bullet">
/// <item>program chips in circuit holders on the network (an IC Housing, a Console's Lua board): their pins
/// (CircuitHousing.GetLogicableFromIndex reaches a pin's device only on the housing's network), ids (sd, write_id;
/// GetLogicableFromId likewise) and batches (CircuitHousing.GetBatchOutput is the network's devices), from the
/// program parsed once per source (LintChipPrograms.Programs);</item>
/// <item>logic writers and writer switches (LogicWriterBase.CurrentOutput, written only while it is on the writer's
/// OutputNetwork1), batch writers (LogicBatchWriter.CurrentPrefabHash on OutputNetwork1), and mirrors
/// (LogicMirror.CurrentDevice, which passes on whatever is written to the mirror).</item>
/// </list>
/// Logic Memory holds a value and writes no device; a Logic Transmitter's link is to another transmitter, not a device.
/// Each network's sources are gathered at most once per scan, and indexed once the scan has asked enough of its
/// devices (NetworkControllers); a scan lives for one call (Open), so nothing is kept between calls but the parsed
/// programs, and no world change can leave it stale. Main thread only.
/// </summary>
internal static class Controllers
{
    private static ControllerScan? _open;

    /// <summary>
    /// Shares one scan among every lookup until the scope is disposed, so a batch builds each network's index once.
    /// </summary>
    internal static Scope Open()
    {
        ControllerScan? previous = _open;
        _open ??= new ControllerScan();
        return new Scope(previous);
    }

    /// <summary>The controllers that can write the device, through the open scan or a scan of its own.</summary>
    internal static List<Controller> Of(Device device) => (_open ?? new ControllerScan()).Of(device);

    /// <summary>inspect_controllers: the controllers of the devices the query names.</summary>
    internal static object Inspect(ControllerQuery query)
    {
        ControllerScan scan = new ControllerScan();
        return query.NetworkId is long networkId ? OnNetwork(scan, networkId) : Listed(scan, query);
    }

    private static ControllersView Listed(ControllerScan scan, ControllerQuery query)
    {
        List<ControlledDeviceView> devices = new List<ControlledDeviceView>(query.Ids!.Count);
        foreach (long id in query.Ids)
        {
            try
            {
                Device device = Require(id);
                devices.Add(new ControlledDeviceView(new ThingId(id), device.PrefabName,
                    ControllerView.ListOrNull(scan.Of(device)) ?? new List<ControllerView>()));
            }
            catch (ApiException missing) when (!query.Single)
            {
                devices.Add(new ControlledDeviceView(new ThingId(id), missing));
            }
        }

        return new ControllersView(null, devices, devices.Count);
    }

    private static ControllersView OnNetwork(ControllerScan scan, long networkId)
    {
        CableNetwork network = Referencable.Find<CableNetwork>(networkId) ??
                               throw ApiErrors.Refused("network_not_found", $"No cable network has id {networkId}.");
        List<Device> members = ControllerScan.DataDevices(network);
        List<ControlledDeviceView> devices = new List<ControlledDeviceView>();
        foreach (Device device in members)
        {
            List<ControllerView>? controllers = ControllerView.ListOrNull(scan.Of(device));
            if (controllers != null)
            {
                devices.Add(new ControlledDeviceView(new ThingId(device.ReferenceId), device.PrefabName, controllers));
            }
        }

        return new ControllersView(new ThingId(networkId), devices, members.Count);
    }

    // A Device in the world by id: device_not_found, worded as the device tools word it, for anything else.
    private static Device Require(long id)
    {
        ScopedTarget target = Devices.Require(DeviceScope.World, new ThingId(id));
        return target.Thing is Device device
            ? device
            : throw ApiErrors.Refused("device_not_found",
                $"{Names.Of(target.Thing)} ({id}) is worn or held, not on a data network: nothing on a network writes it.");
    }

    /// <summary>Restores the scan open before Open; a struct, so a using block allocates nothing.</summary>
    internal readonly struct Scope : IDisposable
    {
        private readonly ControllerScan? _previous;

        internal Scope(ControllerScan? previous) => _previous = previous;

        public void Dispose() => _open = _previous;
    }
}

/// <summary>The controllers of the networks one call looks at, each gathered on first use (NetworkControllers).</summary>
internal sealed class ControllerScan
{
    private readonly Dictionary<long, NetworkControllers> _networks = new Dictionary<long, NetworkControllers>();

    internal List<Controller> Of(Device device)
    {
        List<Controller> found = new List<Controller>();
        ControlledDevice asked = new ControlledDevice(device.ReferenceId, device.PrefabHash, NameHashOf(device));
        foreach (CableNetwork network in device.ConnectedCableNetworks)
        {
            if (network != null)
            {
                ControllersOn(network).AddFor(asked, found);
            }
        }

        return found;
    }

    /// <summary>The network's data devices (CableNetwork.DataDeviceList), copied.</summary>
    internal static List<Device> DataDevices(CableNetwork network)
    {
        try
        {
            List<Device> devices = new List<Device>(network.DataDeviceList);
            devices.RemoveAll(static device => device == null || device.IsBeingDestroyed);
            return devices;
        }
        catch (InvalidOperationException)
        {
            // CableNetwork.DataDeviceList rebuilt while copied; the network shows no devices this call.
            return new List<Device>();
        }
    }

    private NetworkControllers ControllersOn(CableNetwork network)
    {
        if (!_networks.TryGetValue(network.ReferenceId, out NetworkControllers controllers))
        {
            _networks[network.ReferenceId] = controllers = Gather(network);
        }

        return controllers;
    }

    // The network's data devices and its sources: what each device's lookup on it matches against.
    private static NetworkControllers Gather(CableNetwork network)
    {
        List<Device> devices = DataDevices(network);
        HashSet<long> members = new HashSet<long>();
        List<IControllerSource> sources = new List<IControllerSource>();
        foreach (Device device in devices)
        {
            members.Add(device.ReferenceId);
            AddWriter(network, device, sources);
            foreach (Thing holder in LintChips.HoldersOn(device))
            {
                ProgramSource? program = ProgramOf(holder, device);
                if (program != null)
                {
                    sources.Add(program);
                }
            }
        }

        return new NetworkControllers(members, sources);
    }

    // The logic unit kinds that write another device; a writer counts on the network its output is on.
    private static void AddWriter(CableNetwork network, Device device, List<IControllerSource> sources)
    {
        switch (device)
        {
            case LogicBatchWriter batch when batch.OutputNetwork1 == network && batch.LogicType != LogicType.None:
                sources.Add(new BatchWriterSource(batch.ReferenceId, batch.PrefabName, batch.CurrentPrefabHash,
                    EnumNames<LogicType>.Of(batch.LogicType)));
                break;
            case LogicWriterBase writer when !(writer is LogicBatchWriter) && writer.OutputNetwork1 == network &&
                                             writer.CurrentOutput != null && writer.LogicType != LogicType.None:
                sources.Add(new WiredSource(ControllerKinds.LogicWriter, writer.ReferenceId, writer.PrefabName,
                    writer.CurrentOutput.ReferenceId, EnumNames<LogicType>.Of(writer.LogicType)));
                break;
            case LogicMirror mirror when mirror.CurrentDevice != null:
                sources.Add(new WiredSource(ControllerKinds.Mirror, mirror.ReferenceId, mirror.PrefabName,
                    mirror.CurrentDevice.ReferenceId, null));
                break;
        }
    }

    // A holder's program as a source: its language (Lua when its chip is a Lua chip, as LintChips reads it), its
    // pins (an IC Housing's Devices) and its operations; null when it holds no program.
    private static ProgramSource? ProgramOf(Thing holderThing, Device owner)
    {
        string? source;
        try
        {
            source = ((ICircuitHolder)holderThing).GetSourceCode();
        }
        catch (Exception error) when (error is InvalidOperationException || error is NullReferenceException)
        {
            // ICircuitHolder.GetSourceCode on a holder whose chip is half removed; it runs nothing.
            return null;
        }

        if (string.IsNullOrEmpty(source))
        {
            return null;
        }

        ProgrammableChip? chip = LintChips.ChipIn(holderThing);
        string language = chip != null && !LuaChips.IsLua(chip) ? "ic10" : "lua";
        return new ProgramSource(owner.ReferenceId, owner.PrefabName, PinsOf(holderThing),
            LintChipPrograms.Programs.Of(language, source!));
    }

    private static long[] PinsOf(Thing holder)
    {
        ILogicable[]? devices = holder is CircuitHousing housing ? housing.Devices : null;
        if (devices == null)
        {
            return Array.Empty<long>();
        }

        long[] pins = new long[devices.Length];
        for (int index = 0; index < devices.Length; index++)
        {
            pins[index] = devices[index] != null ? devices[index].ReferenceId : 0;
        }

        return pins;
    }

    private static int NameHashOf(Device device)
    {
        try
        {
            return ((ILogicable)device).GetNameHash();
        }
        catch (Exception)
        {
            // ILogicable.GetNameHash on a device being torn down; no by-name batch matches it.
            return 0;
        }
    }
}
