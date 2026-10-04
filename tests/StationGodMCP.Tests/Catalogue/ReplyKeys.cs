#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Newtonsoft.Json.Serialization;

namespace StationGodMCP.Tests.CatalogueChecks;

/// <summary>
/// The keys a method's reply can carry, read from its views (x-views, x-entry-views) at any depth, plus the shared
/// reply keys it gets and the keys shaping adds: what a help.keys line may name.
/// </summary>
internal static class ReplyKeys
{
    private const int MaximumDepth = 6;

    /// <summary>Shaping's own keys, added to any reply that takes fields.</summary>
    internal static readonly string[] ShapingKeys = ["fields_unmatched", "fields_valid", "omit_unmatched", "truncated"];

    /// <summary>Every key at any depth: the reply schema's top-level keys, its views' keys and their nested views' keys.</summary>
    internal static HashSet<string> All(JsonObject method, JsonArray sharedReplyKeys)
    {
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach ((string key, JsonNode? _) in method["reply"]!["properties"]!.AsObject())
        {
            keys.Add(key);
        }

        foreach (JsonObject shared in CatalogueFiles.SharedKeysFor(method, sharedReplyKeys))
        {
            keys.Add((string)shared["key"]!);
        }

        keys.UnionWith(ShapingKeys);
        List<Type> named = EntryViews(method);
        HashSet<Type> seen = [];
        foreach (Type view in Views(method))
        {
            Walk(view, named, keys, seen, 0);
        }

        return keys;
    }

    private static void Walk(Type type, List<Type> named, HashSet<string> keys, HashSet<Type> seen, int depth)
    {
        if (depth > MaximumDepth || !seen.Add(type))
        {
            return;
        }

        foreach (JsonProperty property in ViewShapes.Properties(type))
        {
            keys.Add(property.PropertyName!);
            foreach (Type inner in Inner(property.PropertyType!, named))
            {
                Walk(inner, named, keys, seen, depth + 1);
            }
        }
    }

    // The view types a property's value holds: its own type, or a list's element type (an abstract one through
    // x-entry-views); none for primitives, maps and untyped values.
    private static IEnumerable<Type> Inner(Type type, List<Type> named)
    {
        Type bare = Nullable.GetUnderlyingType(type) ?? type;
        Type? element = ViewShapes.IsList(bare) ? ElementOf(bare) : bare;
        if (element == null || !IsView(element) && !element.IsAbstract)
        {
            return [];
        }

        return element.IsAbstract || element.IsInterface
            ? named.Where(candidate => element.IsAssignableFrom(candidate))
            : [element];
    }

    private static bool IsView(Type type) =>
        type.IsClass && type.Namespace != null && type.Namespace.StartsWith("StationGodMCP", StringComparison.Ordinal) &&
        type != typeof(string);

    private static Type? ElementOf(Type type)
    {
        if (type.IsArray)
        {
            return type.GetElementType();
        }

        Type? enumerable = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            ? type
            : type.GetInterfaces().FirstOrDefault(face => face.IsGenericType &&
                                                          face.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return enumerable?.GetGenericArguments()[0];
    }

    private static IEnumerable<Type> Views(JsonObject method) =>
        (method["x-views"] as JsonArray ?? []).Select(name => ViewShapes.Find((string)name!)).OfType<Type>();

    private static List<Type> EntryViews(JsonObject method) =>
        (method["x-entry-views"] as JsonObject ?? [])
        .SelectMany(pair => pair.Value!.AsArray().Select(name => ViewShapes.Find((string)name!)).OfType<Type>())
        .ToList();
}
