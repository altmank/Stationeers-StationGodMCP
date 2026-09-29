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
/// has side effects: SolarPanel.GetGeneratedPower fires OnPowerGenerateRate):
/// a battery's output gives PowerStored and its input takes PowerMaximum - PowerStored; an APC's output gives
/// AvailablePower (its input's potential plus its cell) and its input takes its output's demand plus the cell's charge
/// rate; a transformer's output gives min(Setting, its input's potential) and its input takes min(Setting, its
/// output's demand) plus UsedPower; any other input/output device gives AvailablePower and takes its output's demand;
/// a solar panel gives GenerationRate, another generator the PowerGeneration it reads, and any other device takes
/// UsedPower while on and built. Off or errored devices give and take nothing, as in the game.
/// </summary>
internal static class PortLoads
{
    internal static PortPower? Of(Device? device, int index)
    {
        if (device == null || device.OpenEnds == null || index < 0 || index >= device.OpenEnds.Count)
        {
            return null;
        }

        Connection end = device.OpenEnds[index];
        if (!device.OnOff || device.Error == 1)
        {
            return new PortPower(0.0, 0.0);
        }

        return device is ElectricalInputOutput io ? InputOutput(io, end) : Single(device);
    }

    private static PortPower InputOutput(ElectricalInputOutput io, Connection end)
    {
        bool output = IsOutput(io, end);
        double demand = io.OutputNetwork != null ? io.OutputNetwork.RequiredLoad : 0.0;
        switch (io)
        {
            case Battery battery:
                return output
                    ? new PortPower(Math.Max(battery.PowerStored, 0f), 0.0)
                    : new PortPower(0.0, Math.Max(0.0, battery.PowerMaximum - battery.PowerStored));
            case AreaPowerControl apc:
                if (output)
                {
                    return new PortPower(apc.AvailablePower, 0.0);
                }

                double charge = apc.Battery != null && !apc.Battery.IsCharged
                    ? Math.Min(apc.BatteryChargeRate, apc.Battery.PowerDelta)
                    : 0.0;
                return new PortPower(0.0, (io.OutputNetwork != null ? Math.Max(demand, apc.UsedPower) : 0.0) + charge);
            case Transformer transformer:
                return output
                    ? new PortPower(Math.Min(transformer.Setting, io.InputNetwork?.PotentialLoad ?? 0f), 0.0)
                    : new PortPower(0.0,
                        io.OutputNetwork != null ? Math.Min(transformer.Setting, demand) + transformer.UsedPower : 0.0);
            default:
                return output ? new PortPower(io.AvailablePower, 0.0) : new PortPower(0.0, demand);
        }
    }

    // The OpenEnds entry and the OutputConnection field are separate copies of one port (PortSides): compare the Unity
    // components they share, never the Connection objects.
    private static bool IsOutput(ElectricalInputOutput io, Connection end)
    {
        Connection? output = io.OutputConnection;
        return PortSides.IsOutput(
            output != null && end.Transform != null && output.Transform != null
                ? end.Transform == output.Transform
                : null,
            output != null && end.Collider != null && output.Collider != null
                ? end.Collider == output.Collider
                : null,
            (int)end.ConnectionRole);
    }

    private static PortPower Single(Device device)
    {
        if (device is SolarPanel solar)
        {
            return new PortPower(solar.GenerationRate, 0.0);
        }

        if (NetworkRoots.IsGenerator(device))
        {
            return new PortPower(
                device.CanLogicRead(LogicType.PowerGeneration)
                    ? Math.Max(0.0, device.GetLogicValue(LogicType.PowerGeneration))
                    : 0.0, 0.0);
        }

        return new PortPower(0.0, device.IsStructureCompleted ? Math.Max(0f, device.UsedPower) : 0.0);
    }
}
