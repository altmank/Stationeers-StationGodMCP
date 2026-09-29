#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared;

/// <summary>
/// refund_to as a request gives it: one word (source, ground, none as before; inventory, storage or a container id as
/// a chain of one) or a list of targets (inventory, source, storage, ground, container ids as strings or integers).
/// The run and swap tools also keep their refund flag: false is none, and cannot go with a refund_to that gives back.
/// </summary>
internal static class RefundArgs
{
    internal const string Argument = "refund_to";
    internal const string Flag = "refund";

    /// <summary>refund_to alone (remove_structure, undo_job): left out, the default chain.</summary>
    internal static RefundRoute Route(Args args) => RouteOf(args.Optional(Argument)) ?? RefundRoute.Default;

    /// <summary>refund_to with the refund flag (the run, upgrade, clean and replace tools).</summary>
    internal static RefundRoute RouteWithFlag(Args args)
    {
        bool? refund = args.OptionalBool(Flag);
        RefundRoute? route = RouteOf(args.Optional(Argument));
        if (refund == false)
        {
            return route == null || !route.GivesBack
                ? RefundRoute.Nothing
                : throw ApiErrors.InvalidArgument(
                    "refund false gives nothing back; leave out refund_to, or leave out refund to choose where it goes.");
        }

        if (refund == true && route != null && !route.GivesBack)
        {
            throw ApiErrors.InvalidArgument("refund true and refund_to none disagree; pass one of them.");
        }

        return route ?? RefundRoute.Default;
    }

    /// <summary>The route a refund_to value names; null when it is absent.</summary>
    internal static RefundRoute? RouteOf(JToken? token)
    {
        if (token == null || token.Type == JTokenType.Null)
        {
            return null;
        }

        if (token.Type == JTokenType.String)
        {
            string text = token.Value<string>() ?? string.Empty;
            return RefundRoute.OfWord(text) ?? throw ApiErrors.InvalidArgument(
                "refund_to must be source, ground, none, inventory, storage, a container's reference id, or a list " +
                "of inventory, source, storage, ground and container ids.");
        }

        if (token.Type == JTokenType.Integer)
        {
            return RefundRoute.OfWord(token.ToString(Formatting.None)) ??
                   throw ApiErrors.InvalidArgument("refund_to as a number must be a container's reference id.");
        }

        if (token is not JArray array)
        {
            throw ApiErrors.InvalidArgument("refund_to must be a string or a list of targets.");
        }

        List<string> words = new List<string>(array.Count);
        for (int index = 0; index < array.Count; index++)
        {
            JToken item = array[index];
            if (item.Type == JTokenType.String)
            {
                words.Add(item.Value<string>() ?? string.Empty);
            }
            else if (item.Type == JTokenType.Integer)
            {
                words.Add(item.ToString(Formatting.None));
            }
            else
            {
                throw ApiErrors.InvalidArgument($"refund_to[{index}] must be a target word or a container's reference id.");
            }
        }

        return RefundRoute.OfWords(words, out string? error) ?? throw ApiErrors.InvalidArgument(error!);
    }

    /// <summary>The route as a request writes it again: a single word as a string, a chain as a list.</summary>
    internal static JToken Wire(RefundRoute route)
    {
        if (route.IsSingleWord)
        {
            return new JValue(route.Words[0]);
        }

        JArray words = new JArray();
        foreach (string word in route.Words)
        {
            words.Add(word);
        }

        return words;
    }

    /// <summary>The route as a reply shows it: a single word as a string, a chain as its list of words.</summary>
    internal static object View(RefundRoute route) =>
        route.IsSingleWord ? route.Words[0] : new List<string>(route.Words);
}
