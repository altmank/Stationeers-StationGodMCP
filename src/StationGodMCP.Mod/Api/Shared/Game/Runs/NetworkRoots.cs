#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;

namespace StationGodMCP.Api.Shared.Game.Runs;

/// <summary>
/// The devices that feed a cable network, found by what they are rather than by what they give this tick (a solar
/// panel at night still feeds its network). PowerTick counts a device as a provider when GetGeneratedPower is above 0
/// (CODE): an ElectricalInputOutput (APC, transformer, battery, wireless power, pylon terminus, rocket umbilical)
/// gives power only to its OutputNetwork; any other device whose class overrides Device.GetGeneratedPower (a
/// generator, a solar panel, a turbine) gives it to its PowerCableNetwork. Nothing is called on the device, so a
/// supplier's side effects (SolarPanel.OnPowerGenerateRate) never run.
/// </summary>
internal static class NetworkRoots
{
    private static readonly Dictionary<Type, bool> Generates = new Dictionary<Type, bool>();

    /// <summary>The suppliers of every listed cable network, by device id.</summary>
    internal static HashSet<long> Suppliers(IEnumerable<IReferencable> networks)
    {
        HashSet<long> roots = new HashSet<long>();
        foreach (IReferencable network in networks)
        {
            if (!(network is CableNetwork cables))
            {
                continue;
            }

            foreach (Device device in RunNetworks.Copy(cables.DeviceList))
            {
                if (Feeds(device, cables))
                {
                    roots.Add(device.ReferenceId);
                }
            }
        }

        return roots;
    }

    internal static bool Feeds(Device device, CableNetwork network)
    {
        if (device is ElectricalInputOutput io)
        {
            return io.OutputNetwork == network;
        }

        return OverridesGeneration(device.GetType()) && device.PowerCableNetwork == network;
    }

    private static bool OverridesGeneration(Type type)
    {
        if (!Generates.TryGetValue(type, out bool generates))
        {
            MethodInfo? method = type.GetMethod(nameof(Device.GetGeneratedPower),
                BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(CableNetwork) }, null);
            generates = method != null && method.DeclaringType != typeof(Device);
            Generates[type] = generates;
        }

        return generates;
    }
}
