#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Pipes;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// Resolves a NetworkHandle to the current id of one family's network. A reference id that names no thing is taken as
/// a network id, as before; one that names a piece of the family stands for the piece's network, one that names a
/// device for its only network of the family (several: ambiguous_port, listing them). {reference_id, port} names the
/// network of the piece joined to that port of a device (Device.OpenEnds[port], the piece in the end's cell that the
/// game's IsConnected accepts), or a piece's own network. A per-tool call's handle that was not a plain network id is
/// recorded for that reply's resolved_networks; a verb's network option (NetworkTarget) is resolved the same way.
/// </summary>
internal static class NetworkHandles
{
    internal static ThingId Resolve(Args args, string name, UpgradeFamily family) =>
        Resolve(args.Optional(name) ?? throw ApiErrors.InvalidArgument($"Argument '{Vocabulary.Path(name)}' is " +
            "required."), name,
            family);

    internal static ThingId Resolve(JToken token, string name, UpgradeFamily family)
    {
        NetworkHandle handle = NetworkHandle.Read(token, name);
        ThingId network = Resolve(handle, name, family);
        if (GameLookup.TryFindThing(handle.Id, out _))
        {
            ResolvedNetworks.Record(name, handle, network);
        }

        return network;
    }

    /// <summary>
    /// The family's network a verb's network option names (option names it in a refusal): a network's id, a piece of
    /// the family (its network), a device (its only network of the family), or {id, port} (the network joined at that
    /// port of a device).
    /// </summary>
    internal static ThingId Resolve(NetworkTarget target, string option, UpgradeFamily family) =>
        Resolve(HandleOf(target, option), option, family);

    /// <summary>
    /// The family of the network a verb's network option names, when the call names no kind: a piece's own, the kind
    /// of a device's port when one is named, a device's only kind of network, or a network id's kind; refused when the
    /// network option names none of those, or a device on several kinds.
    /// </summary>
    internal static UpgradeFamily FamilyOf(NetworkTarget target, string option)
    {
        UpgradeFamily[] families = { new CableFamily(), new PipeFamily(), new ChuteFamily() };
        NetworkHandle handle = HandleOf(target, option);
        if (!GameLookup.TryFindThing(handle.Id, out Thing thing))
        {
            foreach (UpgradeFamily family in families)
            {
                if (HasNetwork(family, handle.Id))
                {
                    return family;
                }
            }

            throw ApiErrors.Refused("network_not_found",
                $"{option}: {handle.Id} names no thing and no cable, pipe or chute network. A network's id changes " +
                "after almost every edit; name a piece on it instead.");
        }

        List<UpgradeFamily> found = new List<UpgradeFamily>(1);
        foreach (UpgradeFamily family in families)
        {
            bool fits = thing is SmallGrid piece && family.IsMember(piece)
                ? true
                : thing is Device device &&
                  (handle is NetworkHandle.ByPort { Port: { } port }
                      ? PortIs(device, port, family)
                      : family.DeviceNetworks(device).Count > 0);
            if (fits)
            {
                found.Add(family);
            }
        }

        return found.Count == 1
            ? found[0]
            : throw ApiErrors.InvalidArgument(found.Count == 0
                ? $"{option}: {Names.Of(thing)} ({thing.PrefabName}) is on no cable, pipe or chute network."
                : $"{option}: {Names.Of(thing)} ({thing.PrefabName}) is on {found.Count} kinds of network; give kind.");
    }

    // A device port's kind: its end's connection type.
    private static bool PortIs(Device device, int port, UpgradeFamily family) =>
        device.OpenEnds != null && port < device.OpenEnds.Count && device.OpenEnds[port] is { } end &&
        (end.ConnectionType & NetworkReader.EndTypesOf(family.NetworkKind)) != NetworkType.None;

    private static bool HasNetwork(UpgradeFamily family, ThingId id)
    {
        try
        {
            family.NetworkMembers(id);
            return true;
        }
        catch (ApiException missing) when (missing.Code == "network_not_found")
        {
            return false;
        }
    }

    // A verb's network option as a handle: an id, or {id, port} with a port index.
    private static NetworkHandle HandleOf(NetworkTarget target, string option)
    {
        if (!ThingId.TryRead(new JValue(target.Id), out ThingId id))
        {
            throw ApiErrors.InvalidArgument($"{option}: '{target.Id}' is no id of a network, a piece or a device.");
        }

        if (target.Port == null)
        {
            return new NetworkHandle.ById(id);
        }

        return int.TryParse(target.Port, System.Globalization.NumberStyles.Integer,
                   System.Globalization.CultureInfo.InvariantCulture, out int port) && port >= 0 &&
               port <= NetworkHandle.MaximumPort
            ? new NetworkHandle.ByPort(id, port)
            : throw ApiErrors.InvalidArgument(
                $"{option}.port: '{target.Port}' is no port index (0 to {NetworkHandle.MaximumPort}).");
    }

    private static ThingId Resolve(NetworkHandle handle, string name, UpgradeFamily family)
    {
        if (!GameLookup.TryFindThing(handle.Id, out Thing thing))
        {
            if (handle is NetworkHandle.ByPort)
            {
                throw ApiErrors.ThingNotFound(handle.Id);
            }

            return handle.Id;
        }

        return handle is NetworkHandle.ByPort byPort && byPort.Port.HasValue
            ? OfPort(thing, byPort.Port.Value, name, family)
            : OfThing(thing, name, family);
    }

    /// <summary>
    /// Several handles (allow_bridge): an object form is resolved to its network; a plain id is kept as given (it may
    /// name a network, or a device whose ports are meant to share a network), and one naming a piece of the family
    /// (or a device that is one of its members, a vent on a pipe) adds that piece's network too. A plain id naming
    /// no thing and no network of the family is network_not_found; one naming another thing, or a device with fewer
    /// than two ports that bridges accepts (it has nothing to bridge: a locker), is invalid_argument.
    /// </summary>
    internal static HashSet<long> ResolveAllowances(Args args, string name, int maximum, UpgradeFamily family,
        System.Func<Connection, bool> bridges)
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
            if (!(handle is NetworkHandle.ById))
            {
                ids.Add(Resolve(array[index], entry, family).Value);
                continue;
            }

            ids.Add(handle.Id.Value);
            if (!GameLookup.TryFindThing(handle.Id, out Thing thing))
            {
                // Not a thing: it must be a network of the family (network_not_found otherwise).
                try
                {
                    family.NetworkMembers(handle.Id);
                }
                catch (ApiException missing) when (missing.Code == "network_not_found")
                {
                    throw ApiErrors.Refused(missing.Code,
                        $"{Vocabulary.Path(entry)}: {handle.Id} names no thing and no {family.NetworkKind} network. " +
                        "A piece's id changes when an edit replaces it (a split, a junction); read it again " +
                        $"({Vocabulary.Tool("connections")}).");
                }
            }
            else if (thing is SmallGrid piece && family.IsMember(piece))
            {
                ids.Add(Resolve(array[index], entry, family).Value);
            }
            else if (!(thing is Device device))
            {
                throw ApiErrors.InvalidArgument(
                    $"{Vocabulary.Path(entry)}: {Names.Of(thing)} ({thing.PrefabName}) is neither a " +
                    $"{family.NetworkKind} piece, a device nor a network.");
            }
            else if (BridgingPorts(device, bridges) < 2)
            {
                throw ApiErrors.InvalidArgument(
                    $"{Vocabulary.Path(entry)}: {Names.Of(device)} ({device.PrefabName}) has no two " +
                    $"{family.NetworkKind} ports that could share a network, so it bridges nothing; name a network, " +
                    $"a piece of one or {{{Vocabulary.Arg("reference_id")}, {Vocabulary.Arg("port")}}}.");
            }
        }

        return ids;
    }

    private static int BridgingPorts(Device device, System.Func<Connection, bool> bridges)
    {
        int count = 0;
        foreach (Connection? end in device.OpenEnds ?? new List<Connection>())
        {
            if (end?.Transform != null && bridges(end))
            {
                count++;
            }
        }

        return count;
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
                $"{name}: {device.PrefabName} {device.ReferenceId} is on {networks.Count} " +
                $"{family.NetworkKind} networks [{string.Join(", ", networks)}]; name one with {{id, port}} " +
                $"(ports: {Ports(device, family)}).");
        }

        throw ApiErrors.InvalidArgument(
            $"{name}: {Names.Of(thing)} ({thing.PrefabName}) is neither a {family.NetworkKind} piece nor a device.");
    }

    /// <summary>
    /// The family's network joined at a device's port (Device.OpenEnds[port]), or a piece's own network; not recorded
    /// in resolved_networks.
    /// </summary>
    internal static ThingId OfPort(Thing thing, int port, string name, UpgradeFamily family)
    {
        if (!(thing is Device device))
        {
            return OfThing(thing, name, family);
        }

        if (device.OpenEnds == null || port >= device.OpenEnds.Count || device.OpenEnds[port]?.Transform == null)
        {
            throw ApiErrors.InvalidArgument(
                $"{name}: port {port} is not a port of {device.PrefabName} {device.ReferenceId} " +
                $"(ports: {Ports(device, family)}).");
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
            $"{name}: port {port} of {device.PrefabName} {device.ReferenceId} joins no " +
            $"{family.NetworkKind} network (ports: {Ports(device, family)}).");
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
