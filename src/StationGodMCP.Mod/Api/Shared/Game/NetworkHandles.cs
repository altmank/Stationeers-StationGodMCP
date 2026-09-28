#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Pipes;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared.Game.Upgrades;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// Resolves a NetworkHandle to the current id of one family's network. A reference id that names no thing is taken as
/// a network id, as before; one that names a piece of the family stands for the piece's network, one that names a
/// device for its only network of the family (several: ambiguous_port, listing them). {reference_id, port} names the
/// network of the piece joined to that port of a device (Device.OpenEnds[port], the piece in the end's cell that the
/// game's IsConnected accepts), or a piece's own network. Every handle that was not a plain network id is recorded
/// for the reply's resolved_networks.
/// </summary>
internal static class NetworkHandles
{
    internal static ThingId Resolve(Args args, string name, UpgradeFamily family) =>
        Resolve(args.Optional(name) ?? throw ApiErrors.InvalidArgument($"Argument '{name}' is required."), name,
            family);

    internal static ThingId Resolve(JToken token, string name, UpgradeFamily family)
    {
        NetworkHandle handle = NetworkHandle.Read(token, name);
        if (!GameLookup.TryFindThing(handle.Id, out Thing thing))
        {
            if (handle is NetworkHandle.ByPort)
            {
                throw ApiErrors.ThingNotFound(handle.Id);
            }

            return handle.Id;
        }

        ThingId network = handle is NetworkHandle.ByPort byPort && byPort.Port.HasValue
            ? OfPort(thing, byPort.Port.Value, name, family)
            : OfThing(thing, name, family);
        ResolvedNetworks.Record(name, handle, network);
        return network;
    }

    /// <summary>
    /// Several handles (allow_bridge): an object form is resolved to its network; a plain id is kept as given (it may
    /// name a device whose ports are meant to share a network).
    /// </summary>
    internal static HashSet<long> ResolveAllowances(Args args, string name, int maximum, UpgradeFamily family)
    {
        HashSet<long> ids = new HashSet<long>();
        if (!args.Has(name))
        {
            return ids;
        }

        JArray array = args.Array(name, maximum);
        for (int index = 0; index < array.Count; index++)
        {
            string entry = $"{name}[{index}]";
            NetworkHandle handle = NetworkHandle.Read(array[index], entry);
            // A bare id may name a device on purpose (allow_bridge names devices whose ports may share a network).
            ids.Add(handle is NetworkHandle.ById
                ? handle.Id.Value
                : Resolve(array[index], entry, family).Value);
        }

        return ids;
    }

    private static ThingId OfThing(Thing thing, string name, UpgradeFamily family)
    {
        if (thing is SmallGrid piece && family.IsMember(piece))
        {
            IReferencable network = family.NetworkOf(piece) ?? throw ApiErrors.Refused("no_network",
                $"{name}: {piece.PrefabName} {piece.ReferenceId} is on no {family.NetworkKind} network.");
            return new ThingId(network.ReferenceId);
        }

        if (thing is Device device)
        {
            List<long> networks = family.DeviceNetworks(device);
            if (networks.Count == 1)
            {
                return new ThingId(networks[0]);
            }

            throw ApiErrors.Refused(networks.Count == 0 ? "no_network" : "ambiguous_port",
                $"{name}: {device.PrefabName} {device.ReferenceId} is on {networks.Count} {family.NetworkKind} " +
                $"networks [{string.Join(", ", networks)}]; name one with {{reference_id, port}} " +
                $"(ports: {Ports(device, family)}).");
        }

        throw ApiErrors.InvalidArgument(
            $"{name}: {thing.DisplayName} ({thing.PrefabName}) is neither a {family.NetworkKind} piece nor a device.");
    }

    private static ThingId OfPort(Thing thing, int port, string name, UpgradeFamily family)
    {
        if (!(thing is Device device))
        {
            return OfThing(thing, name, family);
        }

        if (device.OpenEnds == null || port >= device.OpenEnds.Count || device.OpenEnds[port]?.Transform == null)
        {
            throw ApiErrors.InvalidArgument(
                $"{name}: port {port} is not a port of {device.PrefabName} {device.ReferenceId} (ports: " +
                $"{Ports(device, family)}).");
        }

        Connection end = device.OpenEnds[port];
        SmallCell? cell = GridController.World.GetSmallCell(end.GetLocalGrid());
        if (cell != null)
        {
            foreach (SmallGrid? candidate in new SmallGrid?[] { cell.Cable, cell.Pipe, cell.Chute })
            {
                if (candidate != null && !candidate.IsBeingDestroyed && family.IsMember(candidate) &&
                    candidate.IsConnected(end))
                {
                    IReferencable? network = family.NetworkOf(candidate);
                    if (network != null)
                    {
                        return new ThingId(network.ReferenceId);
                    }
                }
            }
        }

        throw ApiErrors.Refused("port_not_joined",
            $"{name}: port {port} of {device.PrefabName} {device.ReferenceId} joins no {family.NetworkKind} network " +
            $"(ports: {Ports(device, family)}).");
    }

    // Each port with the family's network on it, as "index: network" (or "index: -" when nothing is joined).
    private static string Ports(Device device, UpgradeFamily family)
    {
        if (device.OpenEnds == null)
        {
            return "none";
        }

        List<string> ports = new List<string>();
        for (int index = 0; index < device.OpenEnds.Count; index++)
        {
            Connection end = device.OpenEnds[index];
            if (end?.Transform == null)
            {
                continue;
            }

            SmallCell? cell = GridController.World.GetSmallCell(end.GetLocalGrid());
            string network = "-";
            if (cell != null)
            {
                foreach (SmallGrid? candidate in new SmallGrid?[] { cell.Cable, cell.Pipe, cell.Chute })
                {
                    if (candidate != null && family.IsMember(candidate) && candidate.IsConnected(end))
                    {
                        network = family.NetworkOf(candidate)?.ReferenceId.ToString(
                            System.Globalization.CultureInfo.InvariantCulture) ?? "-";
                    }
                }
            }

            ports.Add($"{index} {end.ConnectionType}: {network}");
        }

        return ports.Count > 0 ? string.Join(", ", ports) : "none";
    }
}
