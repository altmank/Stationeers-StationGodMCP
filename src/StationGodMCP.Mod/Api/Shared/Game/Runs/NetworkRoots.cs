#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Pure;

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
        ForEachFeed(networks, (device, _) => roots.Add(device));
        return roots;
    }

    /// <summary>
    /// The suppliers of every listed cable network, each with the network it feeds: a battery is a root of the network
    /// on its output, not of the one on its input.
    /// </summary>
    internal static NetworkRootSet Feeds(IEnumerable<IReferencable> networks)
    {
        NetworkRootSet roots = NetworkRootSet.None;
        ForEachFeed(networks, roots.AddFeed);
        return roots;
    }

    private static void ForEachFeed(IEnumerable<IReferencable> networks, Action<long, long> feed)
    {
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
                    feed(device.ReferenceId, cables.ReferenceId);
                }
            }
        }
    }

    /// <summary>
    /// The device root names: thing_not_found when there is no such thing, invalid_argument when it is no device,
    /// not_on_network when the edit touches networks and it is on none of them (so it could root none of them).
    /// </summary>
    internal static void RequireOn(ThingId root, UpgradeFamily family, ICollection<long> networks)
    {
        Thing thing = GameLookup.RequireThing(root);
        if (!(thing is Device device))
        {
            throw ApiErrors.InvalidArgument($"root: {thing.DisplayName} ({thing.PrefabName}) is not a device.");
        }

        List<long> on = family.DeviceNetworks(device);
        if (networks.Count > 0 && !on.Exists(networks.Contains))
        {
            throw ApiErrors.Refused("not_on_network",
                $"root: {device.PrefabName} {device.ReferenceId} is on none of the {family.NetworkKind} networks the " +
                $"edit touches [{string.Join(", ", Sorted(networks))}]; its networks: [{string.Join(", ", on)}].");
        }
    }

    private static List<long> Sorted(ICollection<long> ids)
    {
        List<long> sorted = new List<long>(ids);
        sorted.Sort();
        return sorted;
    }

    internal static bool Feeds(Device device, CableNetwork network)
    {
        if (device is ElectricalInputOutput io)
        {
            return io.OutputNetwork == network;
        }

        return OverridesGeneration(device.GetType()) && device.PowerCableNetwork == network;
    }

    /// <summary>A generator by class (not an input/output device): it overrides GetGeneratedPower.</summary>
    internal static bool IsGenerator(Device device) =>
        !(device is ElectricalInputOutput) && OverridesGeneration(device.GetType());

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
