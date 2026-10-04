#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace StationGodMCP.Api.Shared;

/// <summary>
/// from_id on the building tools: one thing, or a list of up to eight tried in order for each material. The first
/// also takes the refund for refund_to source and stands for the source wherever one thing is meant.
/// </summary>
internal static class SourceArgs
{
    internal const string Argument = "from_id";
    internal const int MaximumSources = 8;

    /// <summary>The ids given, in order, each once; empty when from_id is absent.</summary>
    internal static List<ThingId> Of(Args args)
    {
        JToken? token = args.Optional(Argument);
        if (token == null || token.Type == JTokenType.Null)
        {
            return new List<ThingId>();
        }

        if (!(token is JArray))
        {
            return new List<ThingId> { args.ThingId(Argument) };
        }

        List<ThingId> ids = new List<ThingId>();
        foreach (ThingId id in args.ThingIds(Argument, MaximumSources))
        {
            if (!ids.Contains(id))
            {
                ids.Add(id);
            }
        }

        return ids.Count > 0
            ? ids
            : throw ApiErrors.InvalidArgument($"Argument '{Argument}' must name 1 to {MaximumSources} things.");
    }

    /// <summary>The first id: the source of a one-thing role (refunds, undo records); null when absent.</summary>
    internal static ThingId? First(List<ThingId> ids) => ids.Count > 0 ? ids[0] : (ThingId?)null;

    /// <summary>The ids after the first: further sources the materials are taken from.</summary>
    internal static List<ThingId> Rest(List<ThingId> ids) => ids.Count > 1 ? ids.GetRange(1, ids.Count - 1) : new List<ThingId>();
}
