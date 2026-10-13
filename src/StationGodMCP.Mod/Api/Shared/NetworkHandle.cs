#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Protocol;

namespace StationGodMCP.Api.Shared;

/// <summary>
/// A way to name a network that survives edits. The game gives a network a new id after almost every merge or split,
/// so an id noted before an edit goes stale; the pieces and devices on it keep theirs. A handle is a reference id
/// (a network's own id, or any piece or device on it) or {reference_id, port} (a device's port, or a piece). The game
/// side resolves it to the network's current id each time it is read.
/// </summary>
internal abstract class NetworkHandle
{
    private NetworkHandle(ThingId id)
    {
        Id = id;
    }

    /// <summary>The id given: a network, a piece or a device.</summary>
    internal ThingId Id { get; }

    /// <summary>The handle as given, for the resolved_networks entry: the id, or {reference_id, port}.</summary>
    internal abstract object Given { get; }

    /// <summary>A reference id: a network's own id, or a piece or device on it.</summary>
    internal sealed class ById : NetworkHandle
    {
        internal ById(ThingId id)
            : base(id)
        {
        }

        internal override object Given => Id;
    }

    /// <summary>{reference_id, port}: the network at a device port (port may be left out for a piece).</summary>
    internal sealed class ByPort : NetworkHandle
    {
        internal ByPort(ThingId id, int? port)
            : base(id)
        {
            Port = port;
        }

        internal int? Port { get; }

        internal override object Given => new HandleGivenView(Id, Port);
    }

    internal const int MaximumPort = 64;
}

/// <summary>A handle given as {reference_id, port}, echoed in resolved_networks.</summary>
internal sealed class HandleGivenView
{
    internal HandleGivenView(ThingId referenceId, int? port)
    {
        ReferenceId = referenceId;
        Port = port;
    }

    public ThingId ReferenceId { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public int? Port { get; }
}

/// <summary>One handle a request named and the network id it stood for when the request ran.</summary>
internal sealed class ResolvedNetworkView
{
    internal ResolvedNetworkView(string argument, object given, ThingId networkId)
    {
        Argument = argument;
        Given = given;
        NetworkId = networkId;
    }

    /// <summary>The argument that named it (network_id, to.network_id, join_to, allow_bridge[1] ...).</summary>
    public string Argument { get; }

    public object Given { get; }

    public ThingId NetworkId { get; }
}

/// <summary>
/// The handles one request resolved, collected while it runs and added to its reply as resolved_networks. A plain
/// network id that names the network itself is not listed. Thread-static, as Truncations is: the request is collected
/// on the thread that runs it (the main thread, one request at a time), and no other thread's work reaches its list.
/// </summary>
internal static class ResolvedNetworks
{
    [ThreadStatic]
    private static List<ResolvedNetworkView>? _current;

    internal static void Begin() => _current = null;

    internal static void Record(string argument, NetworkHandle handle, ThingId network)
    {
        _current ??= new List<ResolvedNetworkView>();
        object given = handle.Given;
        foreach (ResolvedNetworkView known in _current)
        {
            // One entry per argument, and one per handle: join_to defaulted from to's handle is not listed twice.
            if (known.Argument == argument || (known.NetworkId.Equals(network) && SameGiven(known.Given, given)))
            {
                return;
            }
        }

        _current.Add(new ResolvedNetworkView(argument, given, network));
    }

    private static bool SameGiven(object known, object given) =>
        known.Equals(given) || (known is HandleGivenView a && given is HandleGivenView b &&
                                a.ReferenceId.Equals(b.ReferenceId) && a.Port == b.Port);

    /// <summary>What was resolved since Begin, or null for nothing; the list is handed over and forgotten.</summary>
    internal static List<ResolvedNetworkView>? Take()
    {
        List<ResolvedNetworkView>? taken = _current;
        _current = null;
        return taken;
    }

    /// <summary>The result with resolved_networks added when anything was resolved (object results only).</summary>
    internal static object Attach(object result, List<ResolvedNetworkView>? resolved)
    {
        if (resolved == null || resolved.Count == 0)
        {
            return result;
        }

        JToken token = JToken.FromObject(result, JsonSerializer.Create(ApiJson.Settings));
        if (!(token is JObject item))
        {
            return result;
        }

        item["resolved_networks"] = JToken.FromObject(resolved, JsonSerializer.Create(ApiJson.Settings));
        return item;
    }
}
