#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Tests.CatalogueChecks;

/// <summary>
/// The wire shape of the mod's view classes (compiled into the tests from Api/Views and Api/Shared), as the mod's own
/// serialiser settings name them (ApiJson.Settings: snake case, [JsonProperty] names kept): each serialised property's
/// key, and the JSON type its CLR type gives.
/// </summary>
internal static class ViewShapes
{
    private static readonly Assembly Mod = typeof(ApiJson).Assembly;

    private static readonly Lazy<Dictionary<string, Type>> ViewTypes = new Lazy<Dictionary<string, Type>>(() =>
        Mod.GetTypes()
            .Where(type => type.Namespace != null && type.Namespace.StartsWith("StationGodMCP.Api", StringComparison.Ordinal) &&
                           type.IsClass && !type.IsAbstract && !type.IsGenericTypeDefinition &&
                           !typeof(Delegate).IsAssignableFrom(type) && !type.Name.Contains('<'))
            .GroupBy(type => type.Name)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal));

    private static readonly NullabilityInfoContext Nullability = new NullabilityInfoContext();

    /// <summary>A view class by its simple name, or null when the tests do not compile one of that name.</summary>
    internal static Type? Find(string name) => ViewTypes.Value.TryGetValue(name, out Type? type) ? type : null;

    /// <summary>The serialised properties of a view, in the serialiser's order.</summary>
    internal static List<JsonProperty> Properties(Type view)
    {
        IContractResolver resolver = ApiJson.Settings.ContractResolver!;
        if (!(resolver.ResolveContract(view) is JsonObjectContract contract))
        {
            throw new InvalidOperationException($"{view.Name} does not serialise as an object.");
        }

        return contract.Properties.Where(property => !property.Ignored && property.Readable).ToList();
    }

    /// <summary>The keys a view writes.</summary>
    internal static List<string> Keys(Type view) => Properties(view).Select(property => property.PropertyName!).ToList();

    /// <summary>A reply schema property for one view property: its JSON type, "null" added when it can be null.</summary>
    internal static JsonObject SchemaOf(JsonProperty property)
    {
        Type type = property.PropertyType!;
        List<string> types = JsonTypes(type);
        if (types.Count > 0 && CanBeNull(property))
        {
            types.Add("null");
        }

        JsonObject schema = new JsonObject();
        if (types.Count == 1)
        {
            schema["type"] = types[0];
        }
        else if (types.Count > 1)
        {
            schema["type"] = new JsonArray(types.Select(name => (JsonNode)name).ToArray());
        }

        return schema;
    }

    /// <summary>Whether the CLR type serialises as a JSON array (a list, an array, any non-string enumerable but a map).</summary>
    internal static bool IsList(Type type) =>
        type != typeof(string) && !typeof(JToken).IsAssignableFrom(type) && !IsMap(type) &&
        typeof(IEnumerable).IsAssignableFrom(type);

    private static bool IsMap(Type type) =>
        typeof(IDictionary).IsAssignableFrom(type) ||
        type.GetInterfaces().Any(face => face.IsGenericType &&
                                         (face.GetGenericTypeDefinition() == typeof(IDictionary<,>) ||
                                          face.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));

    // The JSON types a CLR type serialises to; empty for "any" (object, JToken).
    private static List<string> JsonTypes(Type type)
    {
        Type bare = Nullable.GetUnderlyingType(type) ?? type;
        if (bare == typeof(string) || bare == typeof(char) || bare == typeof(ThingId) || bare == typeof(Guid))
        {
            return new List<string> { "string" };
        }

        if (bare == typeof(bool))
        {
            return new List<string> { "boolean" };
        }

        if (bare.IsEnum)
        {
            return new List<string> { "integer" };
        }

        if (bare == typeof(int) || bare == typeof(long) || bare == typeof(short) || bare == typeof(byte) ||
            bare == typeof(uint) || bare == typeof(ulong) || bare == typeof(ushort) || bare == typeof(sbyte))
        {
            return new List<string> { "integer" };
        }

        if (bare == typeof(float) || bare == typeof(double) || bare == typeof(decimal))
        {
            return new List<string> { "number" };
        }

        if (bare == typeof(object) || typeof(JToken).IsAssignableFrom(bare) && bare != typeof(JObject) && bare != typeof(JArray))
        {
            return new List<string>();
        }

        if (bare == typeof(JArray) || IsList(bare))
        {
            return new List<string> { "array" };
        }

        return new List<string> { "object" };
    }

    private static bool CanBeNull(JsonProperty property)
    {
        Type type = property.PropertyType!;
        if (Nullable.GetUnderlyingType(type) != null)
        {
            return true;
        }

        if (type.IsValueType)
        {
            return false;
        }

        PropertyInfo? info = property.DeclaringType?.GetProperty(property.UnderlyingName!,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (info == null)
        {
            return true;
        }

        return Nullability.Create(info).ReadState != NullabilityState.NotNull;
    }
}
