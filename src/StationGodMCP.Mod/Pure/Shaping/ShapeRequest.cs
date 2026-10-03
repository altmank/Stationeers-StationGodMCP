#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace StationGodMCP.Pure.Shaping;

/// <summary>
/// A request's shape: which keys to keep in the reply's list entries (fields), which keys to leave out anywhere in the
/// reply (omit), how many entries to keep of named top-level lists (limit), and the largest reply the caller accepts
/// (max_bytes).
/// </summary>
internal sealed class ShapeRequest
{
    internal const int MaximumLimit = 100000;
    internal const int MinimumMaxBytes = 1024;
    internal const int MaximumMaxBytes = 16777216;

    private static readonly Dictionary<string, int> NoLimits = new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>Nothing to leave out: every key and entry is written.</summary>
    internal static readonly ShapeRequest None = new ShapeRequest(null, NoLimits, null);

    internal ShapeRequest(FieldSelectors? fields, IReadOnlyDictionary<string, int> limits, int? maxBytes,
        OmitSelectors? omit = null)
    {
        Fields = fields;
        Limits = limits;
        MaxBytes = maxBytes;
        Omit = omit;
    }

    /// <summary>Null when no fields were given: every entry is kept whole.</summary>
    internal FieldSelectors? Fields { get; }

    /// <summary>Null when no omit was given: no key is left out by path.</summary>
    internal OmitSelectors? Omit { get; }

    internal IReadOnlyDictionary<string, int> Limits { get; }

    internal int? MaxBytes { get; }

    /// <summary>The largest number of entries to keep of a top-level list; int.MaxValue when not limited.</summary>
    internal int LimitOf(string list) => Limits.TryGetValue(list, out int limit) ? limit : int.MaxValue;

    /// <summary>
    /// Whether the written reply keeps key in the object entries of the top-level list of that name: always without
    /// fields; with fields, when a single name or a path through that list reaches the key. A handler may skip
    /// building a costly part (the catalogue's x-costly) this answers false for, because the writer would leave it
    /// out anyway.
    /// </summary>
    internal bool Wants(string list, string key) => Fields == null || Fields.EntryNodeFor(list).Child(key) != null;

    /// <summary>
    /// Whether omit leaves out the top-level key: a handler may skip building a costly part the written reply would
    /// drop anyway.
    /// </summary>
    internal bool Omits(string key) => Omit?.Root.Child(key)?.Whole == true;

    /// <summary>
    /// The lenient reading ([Server] StrictArguments false), which never refuses: null when there is no shape object; inside it, a selector that does
    /// not parse is kept only to be reported unmatched, and a limit, a max_bytes or a key it cannot use is ignored.
    /// </summary>
    internal static ShapeRequest? Lenient(JToken? token)
    {
        if (!(token is JObject shape))
        {
            return null;
        }

        List<FieldSelector>? fields = LenientSelectors(shape["fields"]);
        List<FieldSelector>? omit = LenientSelectors(shape["omit"]);
        return new ShapeRequest(fields != null ? FieldSelectors.Of(fields) : null, LenientLimits(shape["limit"]),
            LenientMaxBytes(shape["max_bytes"]), omit != null ? OmitSelectors.Of(omit) : null);
    }

    private static List<FieldSelector>? LenientSelectors(JToken? token)
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

        return selectors;
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

    /// <summary>The most selectors a shape may list.</summary>
    internal const int MaximumSelectors = 256;

    /// <summary>
    /// The strict reading, which refuses what it cannot use: a shape that is not an object, a key other than fields,
    /// omit, limit and max_bytes, fields or omit that is not 1 to 256 strings each following the selector grammar, a
    /// limit naming a
    /// key that is not one of the reply's lists (replyLists, from the catalogue) or outside 0 to 100,000, a max_bytes
    /// outside 1,024 to 16,777,216. Null with the problems added when it refuses; null without problems when there is
    /// no shape.
    /// </summary>
    internal static ShapeRequest? Strict(JToken? token, ICollection<string>? replyLists, List<string> problems)
    {
        if (token == null || token.Type == JTokenType.Null)
        {
            return null;
        }

        if (!(token is JObject shape))
        {
            problems.Add("shape must be an object.");
            return null;
        }

        foreach (JProperty property in shape.Properties())
        {
            if (property.Name != "fields" && property.Name != "omit" && property.Name != "limit" &&
                property.Name != "max_bytes")
            {
                problems.Add($"shape has no key '{property.Name}'; it takes fields, omit, limit and max_bytes.");
            }
        }

        List<FieldSelector>? fieldSelectors = StrictSelectors(shape["fields"], "fields", problems);
        List<FieldSelector>? omitSelectors = StrictSelectors(shape["omit"], "omit", problems);
        FieldSelectors? fields = fieldSelectors != null ? FieldSelectors.Of(fieldSelectors) : null;
        OmitSelectors? omit = omitSelectors != null ? OmitSelectors.Of(omitSelectors) : null;
        IReadOnlyDictionary<string, int> limits = StrictLimits(shape["limit"], replyLists, problems);
        int? maxBytes = null;
        JToken? given = shape["max_bytes"];
        if (given != null && given.Type != JTokenType.Null)
        {
            maxBytes = WholeNumber(given, MinimumMaxBytes, MaximumMaxBytes);
            if (maxBytes == null)
            {
                problems.Add($"shape.max_bytes must be an integer from {MinimumMaxBytes} to {MaximumMaxBytes}.");
            }
        }

        return problems.Count == 0 ? new ShapeRequest(fields, limits, maxBytes, omit) : null;
    }

    private static List<FieldSelector>? StrictSelectors(JToken? token, string key, List<string> problems)
    {
        if (token == null || token.Type == JTokenType.Null)
        {
            return null;
        }

        if (!(token is JArray array) || array.Count == 0 || array.Count > MaximumSelectors)
        {
            problems.Add($"shape.{key} must be an array of 1 to {MaximumSelectors} selectors.");
            return null;
        }

        List<FieldSelector> selectors = new List<FieldSelector>(array.Count);
        foreach (JToken item in array)
        {
            FieldSelector? selector = item.Type == JTokenType.String ? FieldSelector.Parse((string)item!) : null;
            if (selector == null || selector is FieldSelector.Unparsed)
            {
                problems.Add($"shape.{key}: {item.ToString(Formatting.None)} is not a selector (names of letters, " +
                             "digits and '_', joined by dots).");
                continue;
            }

            selectors.Add(selector);
        }

        return selectors;
    }

    private static IReadOnlyDictionary<string, int> StrictLimits(JToken? token, ICollection<string>? replyLists,
        List<string> problems)
    {
        if (token == null || token.Type == JTokenType.Null)
        {
            return NoLimits;
        }

        if (!(token is JObject limits))
        {
            problems.Add("shape.limit must be an object of list names and counts.");
            return NoLimits;
        }

        Dictionary<string, int> kept = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (JProperty property in limits.Properties())
        {
            if (replyLists != null && !replyLists.Contains(property.Name))
            {
                problems.Add($"shape.limit: '{property.Name}' is not a list of this method's reply.");
                continue;
            }

            if (WholeNumber(property.Value, 0, MaximumLimit) is int limit)
            {
                kept[property.Name] = limit;
            }
            else
            {
                problems.Add($"shape.limit.{property.Name} must be an integer from 0 to {MaximumLimit}.");
            }
        }

        return kept;
    }

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
/// cut, which fields selectors matched a key, and which omit selectors left one out.
/// </summary>
internal sealed class ShapeOutcome
{
    private readonly bool[] _matched;
    private readonly bool[] _omitted;
    private readonly List<KeyValuePair<string, int>> _lists = new List<KeyValuePair<string, int>>();
    private readonly List<KeyValuePair<string, int>> _cut = new List<KeyValuePair<string, int>>();

    internal ShapeOutcome(int selectors, int omitSelectors = 0)
    {
        _matched = new bool[selectors];
        _omitted = new bool[omitSelectors];
    }

    /// <summary>At least one object entry of a top-level list was written.</summary>
    internal bool AnyEntry { get; private set; }

    /// <summary>Each top-level list's name and length before any cut, in reply order.</summary>
    internal IReadOnlyList<KeyValuePair<string, int>> Lists => _lists;

    /// <summary>Each list limit cut, with its length before the cut.</summary>
    internal IReadOnlyList<KeyValuePair<string, int>> Cut => _cut;

    internal bool Matched(int selector) => _matched[selector];

    internal bool Omitted(int selector) => _omitted[selector];

    internal void Omit(IReadOnlyList<int>? selectors)
    {
        if (selectors == null)
        {
            return;
        }

        foreach (int selector in selectors)
        {
            _omitted[selector] = true;
        }
    }

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

    /// <summary>Every omit selector ending at or below node: the key it names is left out with node's.</summary>
    internal void OmitBelow(SelectorNode node)
    {
        Omit(node.Ends);
        foreach (SelectorNode child in node.Children)
        {
            OmitBelow(child);
        }
    }

    internal void SawList(string name, int length, int limit)
    {
        _lists.Add(new KeyValuePair<string, int>(name, length));
        if (length > limit)
        {
            _cut.Add(new KeyValuePair<string, int>(name, length));
        }
    }
}
