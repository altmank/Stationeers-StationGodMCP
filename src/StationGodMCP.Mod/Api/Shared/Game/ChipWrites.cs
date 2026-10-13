#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.Networking;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// The writes on a circuit holder's chip, as the game's own tools make them: the source as the IC editor's export
/// writes it (Source), the IC Housing's pins as its screws set them (Pins), and the chip's execution paused, resumed,
/// stepped or restarted (Run).
/// </summary>
internal static class ChipWrites
{
    // The NetworkUpdateFlags bits the screwdriver raises for screws 0..5 (CircuitHousing.BuildUpdate).
    private static readonly ushort[] PinUpdateFlags = { 1024, 2048, 4096, 8192, 16384, 32768 };

    /// <summary>
    /// Replaces the chip's source (ICircuitHolder.SetSourceCode). An IC10 chip compiles at once and restarts at line
    /// 0; a Lua chip compiles on a worker thread (ChipProgram), so the reply may still say compiling. An IC10 source is
    /// stored with LF line ends and as ASCII, and the reply's warnings say so, and when it passes the in-game editor's
    /// limits (Ic10Source). The reply echoes the stored source only when echo asks: the caller sent it. Sends the chip
    /// to clients.
    /// </summary>
    internal static IcSourceSetView Source(DeviceScope scope, IcTarget ic, string code, bool echo)
    {
        ProgrammableChip chip = ic.RequireChip();
        ChipProgram program = ChipProgram.Of(chip);
        List<SourceNote> notes = program.Write(ic, chip, code);
        IcChip written = program.Describe(ic, chip, 0);
        return new IcSourceSetView(IcRuntime.PlaceOf(scope, ic), echo ? written : written.WithoutSource(),
            IcRuntime.State(chip), notes);
    }

    /// <summary>
    /// An IC Housing's device pins d0..d5, set the way the housing's screws set them with a screwdriver
    /// (Logicable._TryGetNextLogicDevice). The pins are CircuitHousing.Devices. The chip resolves a pin afresh on every
    /// instruction through CircuitHousing.GetLogicableFromIndex, which gives nothing for a device that is not on the
    /// housing's data network (InputNetwork1.DataDeviceList) and, when the housing has no data network at all, gives
    /// the pinned device whatever it is. There is no pin cache to clear. On a host, the screwdriver raises one
    /// NetworkUpdateFlags bit per screw so that CircuitHousing.BuildUpdate sends that pin's reference id to clients.
    /// Every pin is checked before any is written, so a refused write changes nothing. allowOffNetwork stores a device
    /// that is not on the housing's data network.
    /// </summary>
    internal static SetIcPinsView Pins(DeviceScope scope, IcTarget ic, JObject pins, bool allowOffNetwork)
    {
        CircuitHousing? housing = ic.Housing;
        if (housing == null)
        {
            throw ApiErrors.Refused("not_ic_housing",
                $"pins: circuit holder {ic.HolderId} ({ic.HolderThing.PrefabName}) is a {ic.Kind.Name}; only an IC " +
                "Housing's pins can be set.");
        }

        PinCheck check = new PinCheck(scope, housing, allowOffNetwork);
        SortedDictionary<int, Device?> planned =
            check.PlanAll(pins, Math.Min(housing.Devices.Length, PinUpdateFlags.Length));
        List<PinChangeView> changes = new List<PinChangeView>(planned.Count);
        foreach (KeyValuePair<int, Device?> entry in planned)
        {
            ILogicable? previous = housing.Devices[entry.Key];
            housing.Devices[entry.Key] = entry.Value;
            if (NetworkManager.IsServer)
            {
                housing.NetworkUpdateFlags |= PinUpdateFlags[entry.Key];
            }

            ThingId? before = previous == null ? null : new ThingId(previous.ReferenceId);
            ThingId? after = entry.Value == null ? null : new ThingId(entry.Value.ReferenceId);
            changes.Add(new PinChangeView(entry.Key, before, after, !ReferenceEquals(previous, entry.Value)));
        }

        return new SetIcPinsView(scope.Id, new ThingId(housing.ReferenceId), housing.InputNetwork1 != null, changes,
            IcPins.Describe(ic));
    }

    /// <summary>
    /// Pauses a chip, resumes it, steps it one instruction, or restarts a Lua chip. Pausing skips the holder's Execute
    /// (IcExecutionController's prefixes on CircuitHousing.Execute and SuitBase.Execute); a step pauses, then runs
    /// ProgrammableChip.Execute(1) once and sends the chip to clients. Pause and step are IC10 only; restart is Lua
    /// only (ChipProgram).
    /// </summary>
    internal static IcControlView Run(DeviceScope scope, IcTarget ic, string action)
    {
        ProgrammableChip chip = ic.RequireChip();
        ChipProgram program = ChipProgram.Of(chip);
        double previousLine = chip.LineNumber;
        switch (action)
        {
            case "pause":
                program.RequireSteppable("pause");
                IcExecutionController.Pause(ic.HolderId);
                break;
            case "resume":
                IcExecutionController.Resume(ic.HolderId);
                break;
            case "step":
                program.RequireSteppable("step");
                Step(ic, chip);
                break;
            case "restart":
                program.Restart(ic, chip);
                break;
            default:
                throw ApiErrors.InvalidArgument("run: must be pause, step, resume or restart.");
        }

        IcControlOutcome outcome = new IcControlOutcome(action, IcExecutionController.IsPaused(ic.HolderId),
            previousLine);
        return new IcControlView(IcRuntime.PlaceOf(scope, ic), outcome, IcRuntime.State(chip),
            program.Describe(ic, chip, 0));
    }

    // Refusals come before the pause, so a refused step leaves a running chip running.
    private static void Step(IcTarget ic, ProgrammableChip chip)
    {
        if (chip.CompilationError)
        {
            throw ApiErrors.Refused("ic_compile_error", "run: the chip cannot be stepped while it has a compilation error.");
        }

        if (!ic.IsOperable)
        {
            throw ApiErrors.Refused("ic_not_operable", ic.Housing != null
                ? "run: the IC Housing must be on, powered, and operable before stepping."
                : "run: the worn item needs a charged battery before its chip can be stepped.");
        }

        IcExecutionController.Pause(ic.HolderId);
        chip.Execute(1);
        chip.SendUpdate();
    }

    /// <summary>The checks a pin's new device must pass, all made before any pin is written.</summary>
    private sealed class PinCheck
    {
        private readonly DeviceScope _scope;
        private readonly CircuitHousing _housing;
        private readonly bool _allowOffNetwork;
        private readonly Dictionary<long, ScopedTarget>? _visible;

        internal PinCheck(DeviceScope scope, CircuitHousing housing, bool allowOffNetwork)
        {
            _scope = scope;
            _housing = housing;
            _allowOffNetwork = allowOffNetwork;
            _visible = scope.IsWorld ? null : scope.DeviceMap();
        }

        internal SortedDictionary<int, Device?> PlanAll(JObject requested, int pinCount)
        {
            SortedDictionary<int, Device?> planned = new SortedDictionary<int, Device?>();
            foreach (JProperty pin in requested.Properties())
            {
                int index = ParsePinName(pin.Name, pinCount);
                if (planned.ContainsKey(index))
                {
                    throw ApiErrors.InvalidArgument($"pins: d{index} is named more than once.");
                }

                planned[index] = Resolve(index, pin.Value);
            }

            return planned;
        }

        private static int ParsePinName(string name, int pinCount)
        {
            string text = name.Trim().ToLowerInvariant();
            if (text.Length == 2 && text[0] == 'd' && text[1] >= '0' && text[1] < '0' + pinCount)
            {
                return text[1] - '0';
            }

            throw ApiErrors.InvalidArgument($"pins: '{name}' is not a pin; the pins are d0 to d{pinCount - 1}.");
        }

        private Device? Resolve(int index, JToken token)
        {
            if (token.Type == JTokenType.Null)
            {
                return null;
            }

            if (!ThingId.TryRead(token, out ThingId id) || id.Value <= 0)
            {
                throw ApiErrors.InvalidArgument($"pins.d{index}: must be a device's id, or null to clear the pin.");
            }

            Device device = RequireDevice(index, id);
            if (_visible != null && !_visible.ContainsKey(id.Value))
            {
                throw ApiErrors.Refused("device_not_found", $"pins.d{index}: device {id} is not visible {_scope.Where}.");
            }

            if (!IsReadable(device))
            {
                throw ApiErrors.Refused("logic_not_readable",
                    $"pins.d{index}: device {id} ({Names.Of(device)}) has no readable logic, so a screwdriver would " +
                    "skip it.");
            }

            CableNetwork network = _housing.InputNetwork1;
            if (!_allowOffNetwork && network != null && !network.DataDeviceList.Contains(device))
            {
                throw ApiErrors.Refused("not_on_data_network",
                    $"pins.d{index}: device {id} ({Names.Of(device)}) is not on IC Housing {_housing.ReferenceId}'s " +
                    "data network, so the chip would not reach it through the pin. Connect it to the housing's data " +
                    "network first.");
            }

            return device;
        }

        // Referencable.Find is the lookup the housing itself uses to resolve a pin from a network update
        // (CircuitHousing.ProcessUpdate). The chip's network check and the save load (OnFinishedLoad: Thing.Find<Device>)
        // both only keep Devices.
        private Device RequireDevice(int index, ThingId id)
        {
            IReferencable found = Referencable.Find<IReferencable>(id.Value);
            if (found == null)
            {
                throw ApiErrors.Refused(ApiErrors.ThingNotFoundCode, $"pins.d{index}: nothing has reference id {id}.");
            }

            if (!(found is Device device))
            {
                throw ApiErrors.Refused("not_logic_device",
                    $"pins.d{index}: {id} is a {found.GetType().Name}, not a logic device a pin can hold.");
            }

            if (ReferenceEquals(device, _housing))
            {
                throw ApiErrors.InvalidArgument(
                    $"pins.d{index}: {id} is the IC Housing itself, which the chip already reaches as db.");
            }

            return device;
        }

        private static bool IsReadable(Device device)
        {
            try
            {
                return device.IsLogicReadable();
            }
            catch (Exception)
            {
                // Device.IsLogicReadable on a device mid-deconstruction; a screwdriver would skip it too.
                return false;
            }
        }
    }
}
