#nullable enable

using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// Proves a migrated tool's wire: the old anonymous shape, serialised as the mod serialised it before the views, must
/// give the same JSON text as the new view serialised with ApiJson.Settings, apart from the renames listed in
/// by each test (and fields the new view adds, which are named).
/// </summary>
internal static class WireCheck
{
    private static readonly JsonSerializerSettings OldSettings = new JsonSerializerSettings
    {
        FloatFormatHandling = FloatFormatHandling.String,
        Culture = CultureInfo.InvariantCulture
    };

    internal static string Old(object? shape) => JsonConvert.SerializeObject(shape, OldSettings);

    internal static string New(object? view) => JsonConvert.SerializeObject(view, ApiJson.Settings);

    internal static void Same(object? oldShape, object? newView) => Assert.Equal(Old(oldShape), New(newView));

    /// <summary>
    /// The old shape with each renamed key replaced where it stood must serialise exactly as the view does, once the
    /// view's added keys are taken out. Paths are dotted keys from the root, "[]" for every element of a list, e.g.
    /// "gases[].moles". A rename keeps the key's place.
    /// </summary>
    internal static void SameAfterRenames(object? oldShape, object? newView,
        IReadOnlyDictionary<string, string> renames, params string[] added)
    {
        JToken old = JToken.Parse(Old(oldShape));
        Rename(old, string.Empty, renames);
        JToken now = JToken.Parse(New(newView));
        foreach (string path in added)
        {
            Remove(now, path.Split('.'), 0);
        }

        Assert.Equal(old.ToString(Formatting.None), now.ToString(Formatting.None));
    }

    /// <summary>
    /// As SameAfterRenames, and the old shape's dropped keys (paths before any rename) are taken out of it first.
    /// </summary>
    internal static void SameAfterDrops(object? oldShape, object? newView,
        IReadOnlyDictionary<string, string> renames, string[] dropped, params string[] added)
    {
        JToken old = JToken.Parse(Old(oldShape));
        foreach (string path in dropped)
        {
            Remove(old, path.Split('.'), 0);
        }

        Rename(old, string.Empty, renames);
        JToken now = JToken.Parse(New(newView));
        foreach (string path in added)
        {
            Remove(now, path.Split('.'), 0);
        }

        Assert.Equal(old.ToString(Formatting.None), now.ToString(Formatting.None));
    }

    private static void Rename(JToken token, string path, IReadOnlyDictionary<string, string> renames)
    {
        if (token is JArray array)
        {
            foreach (JToken item in array)
            {
                Rename(item, path + "[]", renames);
            }

            return;
        }

        if (!(token is JObject obj))
        {
            return;
        }

        List<JProperty> properties = new List<JProperty>(obj.Properties());
        foreach (JProperty property in properties)
        {
            string here = path.Length == 0 ? property.Name : path + "." + property.Name;
            Rename(property.Value, here, renames);
            if (renames.TryGetValue(here, out string? renamed))
            {
                property.Replace(new JProperty(renamed, property.Value));
            }
        }
    }

    private static void Remove(JToken token, string[] parts, int at)
    {
        string part = parts[at];
        bool each = part.EndsWith("[]");
        string key = each ? part.Substring(0, part.Length - 2) : part;
        if (!(token is JObject obj) || !(obj[key] is JToken child))
        {
            return;
        }

        if (at == parts.Length - 1)
        {
            obj.Remove(key);
            return;
        }

        if (each && child is JArray list)
        {
            foreach (JToken item in list)
            {
                Remove(item, parts, at + 1);
            }

            return;
        }

        Remove(child, parts, at + 1);
    }
}
