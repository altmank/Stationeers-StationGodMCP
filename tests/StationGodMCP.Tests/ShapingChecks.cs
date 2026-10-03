#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Shaping;
using StationGodMCP.Server;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// The mod's shaping held to two references: with nothing to leave out it writes exactly what the serialiser writes,
/// and its single-name fields give what the reference FieldSelection gives, compared as parsed JSON with key order.
/// </summary>
internal static class ShapingChecks
{
    internal static readonly ShapeRequest Nothing = new ShapeRequest(null, new Dictionary<string, int>(), null);

    /// <summary>The text the mod writes for value (the reply's result) with this shape.</summary>
    internal static ShapedText Mod(object? value, ShapeRequest shape) =>
        ApiJson.WriteShaped(ApiJson.Fresh(), value, shape, ShapingRoot.Result);

    /// <summary>The mod's text for a JSON reply with fields, read leniently.</summary>
    internal static string ModFields(string reply, IEnumerable<string> fields) =>
        Mod(Parse(reply), Fields(fields)).Json;

    /// <summary>The mod's text for a JSON reply with omit, read leniently.</summary>
    internal static string ModOmit(string reply, IEnumerable<string> omit) =>
        Mod(Parse(reply), ShapeRequest.Lenient(new JObject { ["omit"] = new JArray(new List<string>(omit).ToArray()) })!).Json;

    internal static ShapeRequest Fields(IEnumerable<string> fields) =>
        ShapeRequest.Lenient(new JObject { ["fields"] = new JArray(new List<string>(fields).ToArray()) })!;

    /// <summary>The reference's text for the same reply and fields.</summary>
    internal static string Sidecar(string reply, IEnumerable<string> fields)
    {
        using JsonDocument names = JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(fields));
        using JsonDocument document = JsonDocument.Parse(reply);
        return FieldSelection.Of(names.RootElement).Apply(document.RootElement).GetRawText();
    }

    internal static JToken Parse(string json)
    {
        using JsonTextReader reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
        return JToken.ReadFrom(reader);
    }

    /// <summary>Every view a wire test serialises: unchanged through the shaping writer, and fields as the sidecar's.</summary>
    internal static void HoldFor(object? view, string serialised)
    {
        Assert.Equal(serialised, Mod(view, Nothing).Json);
        if (view != null)
        {
            CallReplyView envelope = CallReplyView.Of("w1", view, false, 1.25, 0.5, 1);
            Assert.Equal(JsonConvert.SerializeObject(envelope, ApiJson.Settings),
                ApiJson.WriteShaped(ApiJson.Fresh(), envelope, Nothing, ShapingRoot.Envelope).Json);
        }

        List<string> keys = EntryKeys(serialised);
        if (keys.Count == 0)
        {
            return;
        }

        List<string> everyOther = new List<string>();
        for (int index = 0; index < keys.Count; index += 2)
        {
            everyOther.Add(keys[index]);
        }

        everyOther.Add("no_such_key");
        SameJson(Sidecar(serialised, everyOther), ModFields(serialised, everyOther));
    }

    /// <summary>The keys of every object entry of every top-level list, first seen first.</summary>
    internal static List<string> EntryKeys(string reply)
    {
        List<string> keys = new List<string>();
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        using JsonDocument document = JsonDocument.Parse(reply);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return keys;
        }

        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement entry in property.Value.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (JsonProperty key in entry.EnumerateObject())
                {
                    if (seen.Add(key.Name))
                    {
                        keys.Add(key.Name);
                    }
                }
            }
        }

        return keys;
    }

    /// <summary>Equal as parsed JSON, key order included; numbers compared as numbers.</summary>
    internal static void SameJson(string expected, string actual)
    {
        using JsonDocument left = JsonDocument.Parse(expected);
        using JsonDocument right = JsonDocument.Parse(actual);
        string? difference = Difference(left.RootElement, right.RootElement, "$");
        Assert.True(difference == null, $"{difference}\nexpected: {expected}\nactual:   {actual}");
    }

    private static string? Difference(JsonElement left, JsonElement right, string path)
    {
        if (left.ValueKind != right.ValueKind)
        {
            return $"{path}: {left.ValueKind} against {right.ValueKind}";
        }

        switch (left.ValueKind)
        {
            case JsonValueKind.Object:
            {
                List<JsonProperty> a = new List<JsonProperty>(left.EnumerateObject());
                List<JsonProperty> b = new List<JsonProperty>(right.EnumerateObject());
                if (a.Count != b.Count)
                {
                    return $"{path}: {a.Count} keys against {b.Count}";
                }

                for (int index = 0; index < a.Count; index++)
                {
                    if (a[index].Name != b[index].Name)
                    {
                        return $"{path}: key {index} is {a[index].Name} against {b[index].Name}";
                    }

                    string? inner = Difference(a[index].Value, b[index].Value, path + "." + a[index].Name);
                    if (inner != null)
                    {
                        return inner;
                    }
                }

                return null;
            }
            case JsonValueKind.Array:
            {
                int count = left.GetArrayLength();
                if (count != right.GetArrayLength())
                {
                    return $"{path}: {count} entries against {right.GetArrayLength()}";
                }

                for (int index = 0; index < count; index++)
                {
                    string? inner = Difference(left[index], right[index], $"{path}[{index}]");
                    if (inner != null)
                    {
                        return inner;
                    }
                }

                return null;
            }
            case JsonValueKind.Number:
                return left.GetDouble().Equals(right.GetDouble()) ? null : $"{path}: {left} against {right}";
            case JsonValueKind.String:
                return left.GetString() == right.GetString() ? null : $"{path}: {left} against {right}";
            default:
                return null;
        }
    }
}
