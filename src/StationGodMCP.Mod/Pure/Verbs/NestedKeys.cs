#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// The one check of a nested option object's keys (review D, R-D-22): a key that is not one of the object's own is a
/// problem at its path with the nearest names, never dropped silently, so a call answers every problem at once. Check
/// adds the problems; Refuse throws the first, for a reader that refuses by throwing (a placement's at and orient,
/// read through PlacementReader, whose refusal becomes the placement's problem).
/// </summary>
internal static class NestedKeys
{
    internal static void Check(JObject item, string path, IReadOnlyList<string> keys, List<OptionProblem> problems)
    {
        foreach (JProperty key in item.Properties())
        {
            if (!Has(keys, key.Name))
            {
                problems.Add(new OptionProblem(path + "." + key.Name, Problem(keys), NearestNames.Of(key.Name, keys)));
            }
        }
    }

    /// <summary>The first key that is not one of the object's own, as a problem; null when every key is.</summary>
    internal static OptionProblem? First(JObject item, string path, IReadOnlyList<string> keys)
    {
        foreach (JProperty key in item.Properties())
        {
            if (!Has(keys, key.Name))
            {
                return new OptionProblem(path + "." + key.Name, Problem(keys), NearestNames.Of(key.Name, keys));
            }
        }

        return null;
    }

    internal static void Refuse(JObject item, string path, IReadOnlyList<string> keys)
    {
        foreach (JProperty key in item.Properties())
        {
            if (!Has(keys, key.Name))
            {
                IReadOnlyList<string> nearest = NearestNames.Of(key.Name, keys);
                throw ApiErrors.InvalidArgument($"{path}.{key.Name} {Problem(keys)}" +
                    (nearest.Count > 0 ? $"; did you mean {string.Join(" or ", nearest)}?" : "."));
            }
        }
    }

    private static string Problem(IReadOnlyList<string> keys) => $"is not a key here: {string.Join(", ", keys)}";

    private static bool Has(IReadOnlyList<string> keys, string name)
    {
        for (int index = 0; index < keys.Count; index++)
        {
            if (string.Equals(keys[index], name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
