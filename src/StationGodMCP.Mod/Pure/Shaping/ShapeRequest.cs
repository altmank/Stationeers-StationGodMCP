#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace StationGodMCP.Pure.Shaping;

/// <summary>
/// A request's shape: which keys to keep in the reply's list entries (fields), how many entries to keep of named
/// top-level lists (limit), and the largest reply the caller accepts (max_bytes).
/// </summary>
internal sealed class ShapeRequest
{
    internal const int MaximumLimit = 100000;
    internal const int MinimumMaxBytes = 1024;
    internal const int MaximumMaxBytes = 16777216;

    private static readonly Dictionary<string, int> NoLimits = new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>Nothing to leave out: every key and entry is written.</summary>
    internal static readonly ShapeRequest None = new ShapeRequest(null, NoLimits, null);

    internal ShapeRequest(FieldSelectors? fields, IReadOnlyDictionary<string, int> limits, int? maxBytes)
    {
        Fields = fields;
        Limits = limits;
        MaxBytes = maxBytes;
    }

    /// <summary>Null when no fields were given: every entry is kept whole.</summary>
    internal FieldSelectors? Fields { get; }

    internal IReadOnlyDictionary<string, int> Limits { get; }

    internal int? MaxBytes { get; }

    /// <summary>The largest number of entries to keep of a top-level list; int.MaxValue when not limited.</summary>
    internal int LimitOf(string list) => Limits.TryGetValue(list, out int limit) ? limit : int.MaxValue;

    /// <summary>
    /// The version-1 reading, which never refuses: null when there is no shape object; inside it, a selector that does
    /// not parse is kept only to be reported unmatched, and a limit, a max_bytes or a key it cannot use is ignored.
    /// </summary>
    internal static ShapeRequest? Lenient(JToken? token)
    {
        if (!(token is JObject shape))
        {
            return null;
        }

        return new ShapeRequest(LenientFields(shape["fields"]), LenientLimits(shape["limit"]), LenientMaxBytes(shape["max_bytes"]));
    }

    private static FieldSelectors? LenientFields(JToken? token)
    {
        if (!(token is JArray array) || array.Count == 0)
        {
            return null;
        }

        List<FieldSelector> selectors = new List<FieldSelector>(array.Count);
        foreach (JToken item in array)
        {
            selectors.Add(item.Type == JTokenType.String
                ? FieldSelector.Parse((string)item!)
                : FieldSelector.NotText(item.ToString(Formatting.None)));
        }

        return FieldSelectors.Of(selectors);
    }

    private static IReadOnlyDictionary<string, int> LenientLimits(JToken? token)
    {
        if (!(token is JObject limits))
        {
            return NoLimits;
        }

        Dictionary<string, int> kept = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (JProperty property in limits.Properties())
        {
            if (WholeNumber(property.Value, 0, MaximumLimit) is int limit)
            {
                kept[property.Name] = limit;
            }
        }

        return kept;
    }

    private static int? LenientMaxBytes(JToken? token) => WholeNumber(token, MinimumMaxBytes, MaximumMaxBytes);

    /// <summary>An integer in range, also written as 3.0 or 1e2; null otherwise.</summary>
    internal static int? WholeNumber(JToken? token, int minimum, int maximum)
    {
        double value;
        switch (token?.Type)
        {
            case JTokenType.Integer:
                value = (double)token;
                break;
            case JTokenType.Float:
                value = (double)token;
                if (Math.Floor(value) != value)
                {
                    return null;
                }

                break;
            default:
                return null;
        }

        return value >= minimum && value <= maximum ? (int)value : null;
    }
}

/// <summary>
/// What shaping saw while writing one reply: the length of every top-level list before any cut, which of them limit
/// cut, and which selectors matched a key.
/// </summary>
internal sealed class ShapeOutcome
{
    private readonly bool[] _matched;
    private readonly List<KeyValuePair<string, int>> _lists = new List<KeyValuePair<string, int>>();
    private readonly List<KeyValuePair<string, int>> _cut = new List<KeyValuePair<string, int>>();

    internal ShapeOutcome(int selectors) => _matched = new bool[selectors];

    /// <summary>At least one object entry of a top-level list was written.</summary>
    internal bool AnyEntry { get; private set; }

    /// <summary>Each top-level list's name and length before any cut, in reply order.</summary>
    internal IReadOnlyList<KeyValuePair<string, int>> Lists => _lists;

    /// <summary>Each list limit cut, with its length before the cut.</summary>
    internal IReadOnlyList<KeyValuePair<string, int>> Cut => _cut;

    internal bool Matched(int selector) => _matched[selector];

    internal void Match(IReadOnlyList<int>? selectors)
    {
        if (selectors == null)
        {
            return;
        }

        foreach (int selector in selectors)
        {
            _matched[selector] = true;
        }
    }

    internal void SawEntry() => AnyEntry = true;

    internal void SawList(string name, int length, int limit)
    {
        _lists.Add(new KeyValuePair<string, int>(name, length));
        if (length > limit)
        {
            _cut.Add(new KeyValuePair<string, int>(name, length));
        }
    }
}
