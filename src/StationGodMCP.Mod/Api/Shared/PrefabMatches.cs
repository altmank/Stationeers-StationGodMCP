#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared;

/// <summary>
/// A list's prefab, prefabs and prefab_contains arguments (Pure/PrefabMatch). prefabs is a list of whole names, each
/// trimmed as prefab is; it does not go with prefab.
/// </summary>
internal static class PrefabMatches
{
    /// <summary>The most names one prefabs list takes.</summary>
    internal const int MaximumPrefabs = 200;

    internal static PrefabMatch Parse(Args args)
    {
        string? contains = args.OptionalString("prefab_contains");
        if (!args.Has("prefabs"))
        {
            return new PrefabMatch(args.OptionalString("prefab")?.Trim(), contains);
        }

        if (args.Has("prefab"))
        {
            throw ApiErrors.InvalidArgument("Give prefab or prefabs, not both.");
        }

        JArray array = args.Array("prefabs", MaximumPrefabs);
        List<string> names = new List<string>(array.Count);
        for (int index = 0; index < array.Count; index++)
        {
            string? name = array[index].Type == JTokenType.String ? array[index].Value<string>()?.Trim() : null;
            if (string.IsNullOrEmpty(name))
            {
                throw ApiErrors.InvalidArgument($"prefabs[{index}] must be a prefab name: a string that is not empty.");
            }

            names.Add(name!);
        }

        return PrefabMatch.OfNames(names, contains);
    }
}
