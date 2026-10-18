#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// A reply object made of plain values (cost design A5): its keys in order, each value a view, a string, a number, a
/// list of views or another reply object, and spreads, a view whose keys are written in its place. Nothing is turned
/// into text or a token tree until the reply is written, once, by the writer that sends it. A key added twice keeps its
/// first place and its last value; a spread leaves out the keys its object adds itself (they are written where added).
/// </summary>
[JsonConverter(typeof(ReplyObjectConverter))]
internal sealed class ReplyObject
{
    private readonly List<Part> _parts;

    internal ReplyObject(int capacity = 8) => _parts = new List<Part>(capacity);

    internal int Count => _parts.Count;

    internal ReplyObject Add(string key, object? value)
    {
        for (int index = 0; index < _parts.Count; index++)
        {
            if (_parts[index].Key == key)
            {
                _parts[index] = new Part(key, value, null);
                return this;
            }
        }

        _parts.Add(new Part(key, value, null));
        return this;
    }

    /// <summary>The view's keys (a view, a reply object or a JObject) written here, but those named in except.</summary>
    internal ReplyObject Spread(object? view, params string[] except)
    {
        if (view != null)
        {
            _parts.Add(new Part(null, view, except.Length == 0 ? null : except));
        }

        return this;
    }

    /// <summary>
    /// As Spread, with key's value (when the view has key) written in the view's own place as value instead.
    /// </summary>
    internal ReplyObject SpreadReplacing(object view, string key, object? value, params string[] except) =>
        Spread(new ReplacedKey(view, key, value), except);

    /// <summary>The value added under key; null when there is none (spreads are not looked into).</summary>
    internal object? this[string key]
    {
        get
        {
            foreach (Part part in _parts)
            {
                if (part.Key == key)
                {
                    return part.Value;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// The value added under key here or in a reply object spread into this one (a list reply's own keys, ListReply),
    /// the first found in written order; null when there is none. Views spread in are not looked into.
    /// </summary>
    internal object? Find(string key)
    {
        foreach (Part part in _parts)
        {
            if (part.Key == key)
            {
                return part.Value;
            }

            if (part.Key == null && part.Value is ReplyObject spread && spread.Find(key) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>Whether key was added (spreads are not looked into).</summary>
    internal bool Has(string key)
    {
        foreach (Part part in _parts)
        {
            if (part.Key == key)
            {
                return true;
            }
        }

        return false;
    }

    internal void WriteTo(JsonWriter writer, JsonSerializer serializer)
    {
        writer.WriteStartObject();
        WriteKeys(writer, serializer, null);
        writer.WriteEndObject();
    }

    /// <summary>The keys of this object, without the braces, but those named in except.</summary>
    internal void WriteKeys(JsonWriter writer, JsonSerializer serializer, string[]? except)
    {
        string[]? own = null;
        foreach (Part part in _parts)
        {
            if (part.Key == null)
            {
                own ??= OwnKeys();
                ViewKeys.Write(writer, serializer, part.Value, Merge(Merge(except, part.Except), own));
                continue;
            }

            if (except != null && Array.IndexOf(except, part.Key) >= 0)
            {
                continue;
            }

            writer.WritePropertyName(part.Key);
            serializer.Serialize(writer, part.Value);
        }
    }

    /// <summary>The keys a write of this object holds, spreads' keys read from their contracts.</summary>
    internal void CollectKeys(IContractResolver resolver, ICollection<string> keys)
    {
        foreach (Part part in _parts)
        {
            if (part.Key != null)
            {
                keys.Add(part.Key);
            }
            else
            {
                ViewKeys.Collect(resolver, part.Value, Merge(part.Except, OwnKeys()), keys);
            }
        }
    }

    /// <summary>Its keys and values in written order, spreads' keys included, but those named in except.</summary>
    internal IEnumerable<KeyValuePair<string, object?>> Pairs(JsonSerializer serializer, string[]? except)
    {
        string[]? own = null;
        foreach (Part part in _parts)
        {
            if (part.Key == null)
            {
                own ??= OwnKeys();
                foreach (KeyValuePair<string, object?> key in ViewKeys.Pairs(serializer, part.Value,
                             Merge(Merge(except, part.Except), own)))
                {
                    yield return key;
                }
            }
            else if (except == null || Array.IndexOf(except, part.Key) < 0)
            {
                yield return new KeyValuePair<string, object?>(part.Key, part.Value);
            }
        }
    }

    // The keys added by name; null when there are none.
    private string[]? OwnKeys()
    {
        int count = 0;
        foreach (Part part in _parts)
        {
            if (part.Key != null)
            {
                count++;
            }
        }

        if (count == 0)
        {
            return null;
        }

        string[] keys = new string[count];
        int at = 0;
        foreach (Part part in _parts)
        {
            if (part.Key != null)
            {
                keys[at++] = part.Key;
            }
        }

        return keys;
    }

    private static string[]? Merge(string[]? outer, string[]? inner)
    {
        if (outer == null)
        {
            return inner;
        }

        if (inner == null)
        {
            return outer;
        }

        string[] both = new string[outer.Length + inner.Length];
        outer.CopyTo(both, 0);
        inner.CopyTo(both, outer.Length);
        return both;
    }

    private readonly struct Part
    {
        internal Part(string? key, object? value, string[]? except)
        {
            Key = key;
            Value = value;
            Except = except;
        }

        /// <summary>The key; null for a spread.</summary>
        internal string? Key { get; }

        internal object? Value { get; }

        internal string[]? Except { get; }
    }
}

/// <summary>Writes a reply object through the serializer that writes the reply.</summary>
internal sealed class ReplyObjectConverter : JsonConverter
{
    public override bool CanRead => false;

    public override bool CanConvert(Type objectType) => objectType == typeof(ReplyObject);

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer) =>
        ((ReplyObject)value!).WriteTo(writer, serializer);

    public override object ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer) =>
        throw new NotSupportedException("A reply object is only written.");
}

/// <summary>
/// A view's keys written in place (a spread): as the serializer would write the view's own object, through its
/// contract (names, null and default handling, converters), without the braces.
/// </summary>
internal static class ViewKeys
{
    internal static void Write(JsonWriter writer, JsonSerializer serializer, object? view, string[]? except)
    {
        switch (view)
        {
            case null:
                return;
            case IReplyForm form:
                Write(writer, serializer, form.Form(serializer, null), except);
                return;
            case ReplacedKey replaced:
                foreach (KeyValuePair<string, object?> key in Pairs(serializer, replaced, except))
                {
                    writer.WritePropertyName(key.Key);
                    serializer.Serialize(writer, key.Value);
                }

                return;
            case ReplyObject reply:
                reply.WriteKeys(writer, serializer, except);
                return;
            case JObject tree:
                foreach (JProperty key in tree.Properties())
                {
                    if (except == null || Array.IndexOf(except, key.Name) < 0)
                    {
                        writer.WritePropertyName(key.Name);
                        key.Value.WriteTo(writer);
                    }
                }

                return;
        }

        if (!(serializer.ContractResolver.ResolveContract(view.GetType()) is JsonObjectContract contract))
        {
            throw new InvalidOperationException($"A {view.GetType().Name} has no keys to spread.");
        }

        foreach (JsonProperty property in contract.Properties)
        {
            if (!Shown(property, view, serializer, out object? value) ||
                (except != null && Array.IndexOf(except, property.PropertyName) >= 0))
            {
                continue;
            }

            writer.WritePropertyName(property.PropertyName!);
            if (property.Converter != null && value != null)
            {
                property.Converter.WriteJson(writer, value, serializer);
            }
            else
            {
                serializer.Serialize(writer, value);
            }
        }
    }

    /// <summary>The keys and values a write of view holds, in order, as Write would write them (nothing written).</summary>
    internal static IEnumerable<KeyValuePair<string, object?>> Pairs(JsonSerializer serializer, object? view,
        string[]? except)
    {
        switch (view)
        {
            case null:
                yield break;
            case IReplyForm form:
                foreach (KeyValuePair<string, object?> key in Pairs(serializer, form.Form(serializer, null), except))
                {
                    yield return key;
                }

                yield break;
            case ReplacedKey replaced:
                foreach (KeyValuePair<string, object?> key in Pairs(serializer, replaced.View, except))
                {
                    yield return key.Key == replaced.Key
                        ? new KeyValuePair<string, object?>(key.Key, replaced.Value)
                        : key;
                }

                yield break;
            case ReplyObject reply:
                foreach (KeyValuePair<string, object?> key in reply.Pairs(serializer, except))
                {
                    yield return key;
                }

                yield break;
            case JObject tree:
                foreach (JProperty key in tree.Properties())
                {
                    if (except == null || Array.IndexOf(except, key.Name) < 0)
                    {
                        yield return new KeyValuePair<string, object?>(key.Name, key.Value);
                    }
                }

                yield break;
        }

        if (!(serializer.ContractResolver.ResolveContract(view.GetType()) is JsonObjectContract contract))
        {
            throw new InvalidOperationException($"A {view.GetType().Name} has no keys to spread.");
        }

        foreach (JsonProperty property in contract.Properties)
        {
            if (Shown(property, view, serializer, out object? value) &&
                (except == null || Array.IndexOf(except, property.PropertyName) < 0))
            {
                yield return new KeyValuePair<string, object?>(property.PropertyName!, value);
            }
        }
    }

    /// <summary>
    /// The value a write of view holds under key (a view's property through its contract, a reply object's key, a
    /// token's), for a reader looking into a reply before it is written; null when it has none.
    /// </summary>
    internal static object? Get(JsonSerializer serializer, object? view, string key)
    {
        switch (view)
        {
            case null:
                return null;
            case JObject tree:
                return tree[key];
            case JToken:
                return null;
            case IDictionary map:
                return map.Contains(key) ? map[key] : null;
        }

        foreach (KeyValuePair<string, object?> pair in Pairs(serializer, view, null))
        {
            if (pair.Key == key)
            {
                return pair.Value;
            }
        }

        return null;
    }

    /// <summary>Whether a value is written as a JSON list (a token list, or a collection that is no dictionary).</summary>
    internal static bool IsList(object? value) => value switch
    {
        null => false,
        string => false,
        JArray => true,
        JToken => false,
        IDictionary => false,
        IEnumerable => true,
        _ => false
    };

    /// <summary>The keys a spread of view writes (a property that may be left out when null counts as written).</summary>
    internal static void Collect(IContractResolver resolver, object? view, string[]? except, ICollection<string> keys)
    {
        switch (view)
        {
            case null:
            case IReplyForm:
                return;
            case ReplacedKey replaced:
                Collect(resolver, replaced.View, except, keys);
                return;
            case ReplyObject reply:
                reply.CollectKeys(resolver, keys);
                return;
            case JObject tree:
                foreach (JProperty key in tree.Properties())
                {
                    if (except == null || Array.IndexOf(except, key.Name) < 0)
                    {
                        keys.Add(key.Name);
                    }
                }

                return;
        }

        if (resolver.ResolveContract(view.GetType()) is JsonObjectContract contract)
        {
            foreach (JsonProperty property in contract.Properties)
            {
                if (!property.Ignored && property.Readable &&
                    (except == null || Array.IndexOf(except, property.PropertyName) < 0))
                {
                    keys.Add(property.PropertyName!);
                }
            }
        }
    }

    private static bool Shown(JsonProperty property, object view, JsonSerializer serializer, out object? value)
    {
        value = null;
        if (property.Ignored || !property.Readable || (property.ShouldSerialize != null && !property.ShouldSerialize(view)))
        {
            return false;
        }

        value = property.ValueProvider!.GetValue(view);
        if (value == null)
        {
            return (property.NullValueHandling ?? serializer.NullValueHandling) != NullValueHandling.Ignore;
        }

        DefaultValueHandling defaults = property.DefaultValueHandling ?? serializer.DefaultValueHandling;
        if ((defaults & DefaultValueHandling.Ignore) != 0)
        {
            object? fallback = property.DefaultValue ?? (property.PropertyType is { IsValueType: true } type
                ? Activator.CreateInstance(type)
                : null);
            return !Equals(value, fallback);
        }

        return true;
    }
}

/// <summary>A view spread with one of its keys written with another value, in the view's own place.</summary>
internal sealed class ReplacedKey
{
    internal ReplacedKey(object view, string key, object? value)
    {
        View = view;
        Key = key;
        Value = value;
    }

    internal object View { get; }

    internal string Key { get; }

    internal object? Value { get; }
}
