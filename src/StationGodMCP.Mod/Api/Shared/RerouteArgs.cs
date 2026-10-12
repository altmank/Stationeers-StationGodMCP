#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Protocol;

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

/// <summary>reroute.between as given: exactly two ends, each a reference id or {reference_id, port}.</summary>
internal static class RerouteArgs
{
    private const int MaximumPort = 64;

    internal static List<RerouteEndArg> Between(JToken? token)
    {
        if (!(token is JArray array) || array.Count != 2)
        {
            throw ApiErrors.InvalidArgument(
                Vocabulary.Text("{arg:reroute}.{arg:between} names exactly two ends: a reference id, or " +
                    "{{arg:reference_id}, {arg:port}} for a device port."));
        }

        List<RerouteEndArg> ends = new List<RerouteEndArg>(2);
        for (int index = 0; index < array.Count; index++)
        {
            ends.Add(End(array[index], $"reroute.between[{index}]"));
        }

        return ends;
    }

    private static RerouteEndArg End(JToken token, string name)
    {
        if (ThingId.TryRead(token, out ThingId id))
        {
            return new RerouteEndArg(id, null);
        }

        if (!(token is JObject item))
        {
            throw ApiErrors.InvalidArgument($"{Vocabulary.Path(name)} must be a reference id or " +
                $"{{{Vocabulary.Arg("reference_id")}, {Vocabulary.Arg("port")}}}.");
        }

        Args fields = new Args(item);
        try
        {
            return new RerouteEndArg(fields.ThingId("reference_id"), fields.OptionalInt("port", 0, MaximumPort));
        }
        catch (ApiException problem) when (problem.Code == ApiErrors.InvalidArgumentCode)
        {
            throw ApiErrors.InvalidArgument($"{Vocabulary.Path(name)}: {problem.Message}");
        }
    }
}
