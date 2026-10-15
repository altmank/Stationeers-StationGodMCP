#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Api.Shared;

/// <summary>One end of reroute.between: a device or piece, and for a device optionally the port (OpenEnds index).</summary>
internal sealed class RerouteEndArg
{
    internal RerouteEndArg(ThingId id, int? port)
    {
        Id = id;
        Port = port;
    }

    internal ThingId Id { get; }

    internal int? Port { get; }
}

/// <summary>plan route's reroute.between as given: exactly two ends, each an id or {id, port} for a device port.</summary>
internal static class RerouteArgs
{
    private const string Option = "reroute.between";
    private const int MaximumPort = 64;

    private static readonly string[] EndKeys = { "id", "port" };

    /// <summary>The two ends; null with a problem added when the call does not give exactly two that read.</summary>
    internal static List<RerouteEndArg>? Between(JToken? token, List<OptionProblem> problems)
    {
        if (!(token is JArray array) || array.Count != 2)
        {
            problems.Add(new OptionProblem(Option,
                "names exactly two ends: an id, or {id, port} for a device port"));
            return null;
        }

        List<RerouteEndArg> ends = new List<RerouteEndArg>(2);
        for (int index = 0; index < array.Count; index++)
        {
            if (End(array[index], $"{Option}[{index}]", problems) is { } end)
            {
                ends.Add(end);
            }
        }

        return ends.Count == 2 ? ends : null;
    }

    private static RerouteEndArg? End(JToken token, string path, List<OptionProblem> problems)
    {
        if (ThingId.TryRead(token, out ThingId id))
        {
            return new RerouteEndArg(id, null);
        }

        if (!(token is JObject item) || !ThingId.TryRead(item["id"], out id))
        {
            problems.Add(new OptionProblem(path, "must be an id or {id, port}"));
            return null;
        }

        if (NestedKeys.First(item, path, EndKeys) is { } unknown)
        {
            problems.Add(unknown);
            return null;
        }

        JToken? port = item["port"];
        if (port == null || port.Type == JTokenType.Null)
        {
            return new RerouteEndArg(id, null);
        }

        if (port.Type == JTokenType.Integer && port.Value<long>() is >= 0 and <= MaximumPort)
        {
            return new RerouteEndArg(id, port.Value<int>());
        }

        problems.Add(new OptionProblem(path + ".port", $"must be a port index, 0 to {MaximumPort}"));
        return null;
    }
}
