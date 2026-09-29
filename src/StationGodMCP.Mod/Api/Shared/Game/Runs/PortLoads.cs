#nullable enable

using System;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Motherboards;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game.Runs;

/// <summary>
/// What a device port would supply to and draw from a network it is not on now, for the overload guard. PowerTick
/// (CODE) asks each device GetGeneratedPower / GetUsedPower of the network, and every override answers 0 for a network
/// the device is not on, so the guard reads the same numbers from the device's state instead (nothing is called that
/// has side effects: SolarPanel.GetGeneratedPower fires OnPowerGenerateRate), with the on/off and error checks each
/// override makes (decompile Device, Battery, AreaPowerControl, Transformer, SolarPanel):
/// a battery's output gives PowerStored and its input takes PowerMaximum - PowerStored, while on and not in error; an
/// APC's output gives AvailablePower (its input's potential plus its cell) while on and not in error, and its input
/// takes its output's demand while on plus the cell's charge rate whether on or not; a transformer's output gives
/// min(Setting, its input's potential) while on and not in error, and its input takes, while on, min(Setting, its
/// output's demand) plus UsedPower, or only UsedPower in error; any other input/output device gives AvailablePower
/// while on and not in error and takes its output's demand while on (only UsedPower in error); a solar panel gives
/// GenerationRate on or off, another generator the PowerGeneration it reads while on, and any other device takes
/// UsedPower while on and built, in error or not (Device.GetUsedPower asks only OnOff and IsStructureCompleted).
/// <para>
/// Only a port that moves power in the device's power role answers (Pure/PortSides.PowerOf): a Data-only port (a
/// station battery's port 0, a landing pad piece's data port) answers null, as does a port of an input/output device
/// that is neither its InputConnection nor its OutputConnection. Each answer names its side, so the forecast counts a
/// device side once per network however many of its ports are on it.
/// </para>
/// <para>
/// switchedOn: the same numbers for the device as if it were on (Error is kept as it is): what a device that is off
/// now brings once it is switched on (the would_overload_when_on warning). A number that follows a charge is taken at
/// the game's ceiling there instead (Pure/SwitchedOnLoads): a battery's output and input PowerMaximum each, an APC's
/// output its MaximumPower, a power transmitter's input MaxPowerTransmission and its output at most that, so the
/// warning is the same in the dry run and the real run however the charge drifts between them.
/// </para>
/// </summary>
internal static class PortLoads
{
    internal static PortPower? Of(Device? device, int index, bool switchedOn = false)
    {
        if (device == null || device.OpenEnds == null || index < 0 || index >= device.OpenEnds.Count)
        {
            return null;
        }

        Connection end = device.OpenEnds[index];
        if (end == null)
        {
            return null;
        }

        ElectricalInputOutput? io = device as ElectricalInputOutput;
        PowerSide? side = PortSides.PowerOf((int)end.ConnectionType, io != null,
            io != null && IsSide(end, io.OutputConnection, PortSides.IsOutput),
            io != null && IsSide(end, io.InputConnection, PortSides.IsInput));
        if (side == null)
        {
            return null;
        }

        bool on = switchedOn || device.OnOff;
        bool error = device.Error == 1;
        return io != null
            ? InputOutput(io, side.Value == PowerSide.Output, on, error, switchedOn)
            : Single(device, on);
    }

    /// <summary>
    /// What a port of a device that is off now adds once it is switched on: its switched-on numbers less what it
    /// brings now; null for a device that is on (it brings nothing more), a port that is not there, or one with no
    /// power role (a data port).
    /// </summary>
    internal static PortPower? Dormant(Device? device, int index)
    {
        if (device == null || device.OnOff)
        {
            return null;
        }

        PortPower? now = Of(device, index);
        PortPower? on = Of(device, index, true);
        return now == null || on == null
            ? null
            : new PortPower(Math.Max(0.0, on.PotentialW - now.PotentialW), Math.Max(0.0, on.RequiredW - now.RequiredW),
                on.Side);
    }

    // An input/output device supplies only on its output and draws only on its input.
    private static PortPower InputOutput(ElectricalInputOutput io, bool output, bool on, bool error, bool ceiling) =>
        output
            ? new PortPower(on && !error ? Supply(io, ceiling) : 0.0, 0.0, PowerSide.Output)
            : new PortPower(0.0, Demand(io, on, error, ceiling), PowerSide.Input);

    // What the output gives while on and not in error.
    private static double Supply(ElectricalInputOutput io, bool ceiling) =>
        io switch
        {
            Battery battery => ceiling
                ? SwitchedOnLoads.BatteryOutput(battery.PowerMaximum)
                : Math.Max(battery.PowerStored, 0f),
            AreaPowerControl apc => ceiling
                ? SwitchedOnLoads.ApcOutput(io.InputNetwork?.PotentialLoad ?? 0f,
                    apc.Battery != null ? apc.Battery.PowerMaximum : (double?)null)
                : apc.AvailablePower,
            Transformer transformer => Math.Min(transformer.Setting, io.InputNetwork?.PotentialLoad ?? 0f),
            PowerTransmitter when ceiling => SwitchedOnLoads.TransmitterOutput(io.InputNetwork?.PotentialLoad ?? 0f,
                PowerTransmitter.MaxPowerTransmission),
            _ => io.AvailablePower
        };

    // What the input takes, with each class's own on/off and error checks.
    private static double Demand(ElectricalInputOutput io, bool on, bool error, bool ceiling)
    {
        double demand = io.OutputNetwork != null ? io.OutputNetwork.RequiredLoad : 0.0;
        switch (io)
        {
            case Battery battery:
                return !on || error ? 0.0 :
                    ceiling ? SwitchedOnLoads.BatteryInput(battery.PowerMaximum) :
                    Math.Max(0.0, battery.PowerMaximum - battery.PowerStored);
            case AreaPowerControl apc:
                double charge = apc.Battery != null && !apc.Battery.IsCharged
                    ? Math.Min(apc.BatteryChargeRate, apc.Battery.PowerDelta)
                    : 0.0;
                return (on && io.OutputNetwork != null ? Math.Max(demand, apc.UsedPower) : 0.0) + charge;
            case Transformer transformer:
                return !on || io.OutputNetwork == null ? 0.0 :
                    error ? Math.Max(0f, transformer.UsedPower) :
                    Math.Min(transformer.Setting, demand) + transformer.UsedPower;
            case PowerTransmitter when ceiling:
                return error ? Math.Max(0f, io.UsedPower) :
                    SwitchedOnLoads.TransmitterInput(PowerTransmitter.MaxPowerTransmission);
            default:
                return !on ? 0.0 : error ? Math.Max(0f, io.UsedPower) : demand;
        }
    }

    // The OpenEnds entry and the InputConnection / OutputConnection fields are separate copies of one port
    // (PortSides): compare the Unity components they share, never the Connection objects.
    private static bool IsSide(Connection end, Connection? side, Func<bool?, bool?, int, bool> test) =>
        side != null && test(
            end.Transform != null && side.Transform != null ? end.Transform == side.Transform : null,
            end.Collider != null && side.Collider != null ? end.Collider == side.Collider : null,
            (int)end.ConnectionRole);

    private static PortPower Single(Device device, bool on)
    {
        if (device is SolarPanel solar)
        {
            return new PortPower(solar.GenerationRate, 0.0, PowerSide.Device);
        }

        if (NetworkRoots.IsGenerator(device))
        {
            return new PortPower(
                on && device.CanLogicRead(LogicType.PowerGeneration)
                    ? Math.Max(0.0, device.GetLogicValue(LogicType.PowerGeneration))
                    : 0.0, 0.0, PowerSide.Device);
        }

        return new PortPower(0.0, on && device.IsStructureCompleted ? Math.Max(0f, device.UsedPower) : 0.0,
            PowerSide.Device);
    }
}
