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
/// What can write a device (inspect controllers, driven_by), found on the device's own data networks, the only place
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
/// devices (NetworkControllers); a scan lives for one call (Open) or, outside one, for the frame (every lookup of a
/// frame shares it: the game does not tick between them, so it reads the world as it is in that frame; review D,
/// R-D-9), so nothing is kept between frames but the parsed programs, and no world change can leave it stale. A
/// gather fetches a program only from the circuit holders among the network's devices and their slots, and finds a
/// holder's program by its source string before hashing it (ChipProgramCache.OfHolder). Main thread only.
/// </summary>
internal static class Controllers
{
    private static ControllerScan? _open;
    private static ControllerScan? _frameScan;
    private static int _frame = -1;

    /// <summary>
    /// Shares one scan among every lookup until the scope is disposed, so a batch builds each network's index once.
    /// </summary>
    internal static Scope Open()
    {
        ControllerScan? previous = _open;
        _open ??= new ControllerScan();
        return new Scope(previous);
    }

    /// <summary>The controllers that can write the device, through the open scan or the frame's.</summary>
    internal static List<Controller> Of(Device device) => (_open ?? FrameScan()).Of(device);

    /// <summary>A write that changed what can write a device (a chip's source): the frame's scan is read again.</summary>
    internal static void Forget() => _frameScan = null;

    // The scan every lookup of this frame shares outside an open scope.
    private static ControllerScan FrameScan()
    {
        int frame = UnityEngine.Time.frameCount;
        if (_frameScan == null || frame != _frame)
        {
            _frameScan = new ControllerScan();
            _frame = frame;
        }

        return _frameScan;
    }

    /// <summary>
    /// inspect controllers: what can write the device with this id, one entry per controller (ControllerGroups), each
    /// with its shown name and whether it is on.
    /// </summary>
    internal static DeviceControllersView OfDevice(ThingId id)
    {
        Device device = Require(id.Value);
        List<ControllerEntryView> entries = new List<ControllerEntryView>();
        foreach (ControllerGroup group in ControllerGroups.Of((_open ?? FrameScan()).Of(device)))
        {
            Thing? thing = Thing.Find(group.ReferenceId);
            entries.Add(new ControllerEntryView(group, thing != null ? Names.Of(thing) : group.PrefabName,
                thing is Device controller && controller != null ? controller.OnOff : null));
        }

        return new DeviceControllersView(id, Names.Of(device), entries);
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
            AddPrograms(device, sources);
        }

        return new NetworkControllers(members, sources);
    }

    // The device's programs: its own when it is a circuit holder, else those of the holders in its slots (as
    // LintChips.HoldersOn finds them, without a list per device).
    private static void AddPrograms(Device device, List<IControllerSource> sources)
    {
        if (device is ICircuitHolder)
        {
            AddProgram(device, device, sources);
            return;
        }

        if (device.Slots == null)
        {
            return;
        }

        for (int index = 0; index < device.Slots.Count; index++)
        {
            if (device.Slots[index]?.Get() is Thing occupant && occupant != null && occupant is ICircuitHolder)
            {
                AddProgram(occupant, device, sources);
            }
        }
    }

    private static void AddProgram(Thing holder, Device owner, List<IControllerSource> sources)
    {
        ProgramSource? program = ProgramOf(holder, owner);
        if (program != null)
        {
            sources.Add(program);
        }
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
            LintChipPrograms.Programs.OfHolder(holderThing.ReferenceId, language, source!));
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
