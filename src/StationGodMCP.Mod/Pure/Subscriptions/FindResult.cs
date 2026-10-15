#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace StationGodMCP.Pure.Subscriptions;

/// <summary>
/// One evaluation of a find subscription: the find's reply pages as its handler answered them (plain views, the first
/// page then each later page of its walk), when it began (game time, as_of_s), the frame it ended in, what it cost the
/// main thread, and the world news it began at. Made on the main thread and never changed after; its text form
/// (Rendered) is made once, by whichever writer thread needs it first, so subscribers sharing it write it once.
/// </summary>
internal sealed class FindResult
{
    private readonly IReadOnlyList<object> _pages;
    private readonly Func<object, JObject> _render;
    private readonly object _gate = new object();
    private RenderedFind? _rendered;

    /// <summary>render writes one page as JSON (the reply views' serializer); called off the main thread.</summary>
    internal FindResult(IReadOnlyList<object> pages, Func<object, JObject> render, double asOfS, long frame,
        double costMs, long news)
    {
        _pages = pages;
        _render = render;
        AsOfS = asOfS;
        Frame = frame;
        CostMs = costMs;
        News = news;
    }

    /// <summary>The game time the evaluation began: its live facts are as of then.</summary>
    internal double AsOfS { get; }

    internal long Frame { get; }

    /// <summary>The main-thread milliseconds of every step of the evaluation.</summary>
    internal double CostMs { get; }

    /// <summary>The query's channels' counter when the evaluation began.</summary>
    internal long News { get; }

    /// <summary>The text form, made on first use (any thread) and kept.</summary>
    internal RenderedFind Rendered
    {
        get
        {
            lock (_gate)
            {
                if (_rendered == null)
                {
                    List<JObject> pages = new List<JObject>(_pages.Count);
                    foreach (object page in _pages)
                    {
                        pages.Add(_render(page));
                    }

                    _rendered = RenderedFind.Of(pages);
                }

                return _rendered;
            }
        }
    }
}

/// <summary>
/// A find's reply as a subscription holds it: every list whole (a walk's pages joined), without the paging keys
/// (count, total, offset, limit, has_more, cursor_id). A list whose entries all carry one of the key fields
/// (reference_id, gateway_id, room_id, atmosphere_id), each a different value, is keyed: its entries are compared one by
/// one. Everything else (the reply's other keys, lists without such keys) is the rest, compared whole.
/// </summary>
internal sealed class RenderedFind
{
    internal static readonly string[] KeyFields = { "reference_id", "gateway_id", "room_id", "atmosphere_id" };

    private static readonly HashSet<string> PagingKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "count", "total", "offset", "limit", "has_more", "cursor_id",
    };

    private RenderedFind(JObject whole, Dictionary<string, KeyedList> lists, JObject rest)
    {
        Whole = whole;
        Lists = lists;
        Rest = rest;
        RestText = rest.ToString(Formatting.None);
    }

    /// <summary>The reply as a subscription's first reply (and a whole update) carries it.</summary>
    internal JObject Whole { get; }

    internal IReadOnlyDictionary<string, KeyedList> Lists { get; }

    internal JObject Rest { get; }

    internal string RestText { get; }

    internal static RenderedFind Of(IReadOnlyList<JObject> pages)
    {
        JObject whole = new JObject();
        Dictionary<string, JArray> joined = new Dictionary<string, JArray>(StringComparer.Ordinal);
        for (int index = 0; index < pages.Count; index++)
        {
            foreach (JProperty property in pages[index].Properties())
            {
                if (PagingKeys.Contains(property.Name))
                {
                    continue;
                }

                if (property.Value is JArray entries)
                {
                    if (!joined.TryGetValue(property.Name, out JArray? list))
                    {
                        list = new JArray();
                        joined.Add(property.Name, list);
                        whole[property.Name] = list;
                    }

                    foreach (JToken entry in entries)
                    {
                        list.Add(entry.DeepClone());
                    }
                }
                else if (index == 0)
                {
                    // A later page answers the first page's counts again: the first one's are kept.
                    whole[property.Name] = property.Value.DeepClone();
                }
            }
        }

        Dictionary<string, KeyedList> lists = new Dictionary<string, KeyedList>(StringComparer.Ordinal);
        JObject rest = new JObject();
        foreach (JProperty property in whole.Properties())
        {
            if (property.Value is JArray list && KeyedList.TryOf(list) is KeyedList keyed)
            {
                lists.Add(property.Name, keyed);
            }
            else
            {
                rest[property.Name] = property.Value;
            }
        }

        return new RenderedFind(whole, lists, rest);
    }
}

/// <summary>A keyed list: its entries in order, each by its key with its text.</summary>
internal sealed class KeyedList
{
    private KeyedList(string field, List<string> order, Dictionary<string, KeyedEntry> byKey)
    {
        Field = field;
        Order = order;
        ByKey = byKey;
    }

    /// <summary>The key field its entries carry.</summary>
    internal string Field { get; }

    internal IReadOnlyList<string> Order { get; }

    internal IReadOnlyDictionary<string, KeyedEntry> ByKey { get; }

    /// <summary>
    /// The list keyed by the first key field every entry carries with a value of its own; an empty list is keyed (by no
    /// field yet); null when no field keys it.
    /// </summary>
    internal static KeyedList? TryOf(JArray list)
    {
        if (list.Count == 0)
        {
            return new KeyedList(string.Empty, new List<string>(), new Dictionary<string, KeyedEntry>());
        }

        foreach (string field in RenderedFind.KeyFields)
        {
            List<string> order = new List<string>(list.Count);
            Dictionary<string, KeyedEntry> byKey = new Dictionary<string, KeyedEntry>(list.Count, StringComparer.Ordinal);
            bool keyed = true;
            foreach (JToken entry in list)
            {
                string? key = entry is JObject row && row[field] is JValue { Value: not null } value
                    ? value.ToString(Formatting.None).Trim('"')
                    : null;
                if (key == null || byKey.ContainsKey(key))
                {
                    keyed = false;
                    break;
                }

                order.Add(key);
                byKey.Add(key, new KeyedEntry(entry, entry.ToString(Formatting.None)));
            }

            if (keyed)
            {
                return new KeyedList(field, order, byKey);
            }
        }

        return null;
    }
}

/// <summary>One entry of a keyed list and its text.</summary>
internal sealed class KeyedEntry
{
    internal KeyedEntry(JToken value, string text)
    {
        Value = value;
        Text = text;
    }

    internal JToken Value { get; }

    internal string Text { get; }
}

/// <summary>One keyed list's change: entries new to it, entries whose text changed, and the keys gone from it.</summary>
internal sealed class FindListChange
{
    internal List<JToken> Added { get; } = new List<JToken>();

    internal List<JToken> Changed { get; } = new List<JToken>();

    internal List<string> Removed { get; } = new List<string>();

    internal bool IsEmpty => Added.Count == 0 && Changed.Count == 0 && Removed.Count == 0;
}

/// <summary>What changed between two evaluations a subscriber was sent: per keyed list, and the rest when it changed.</summary>
internal sealed class FindChange
{
    private FindChange(Dictionary<string, FindListChange> lists, JObject? rest)
    {
        Lists = lists;
        Rest = rest;
    }

    internal IReadOnlyDictionary<string, FindListChange> Lists { get; }

    /// <summary>The rest whole, when it differs; null when it is the same.</summary>
    internal JObject? Rest { get; }

    /// <summary>
    /// The change from before to after; null when nothing differs. A list keyed in one and not the other is compared
    /// as the rest is: whole (its entries all added, or its keys all removed).
    /// </summary>
    internal static FindChange? Between(RenderedFind before, RenderedFind after)
    {
        Dictionary<string, FindListChange> lists = new Dictionary<string, FindListChange>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, KeyedList> list in after.Lists)
        {
            before.Lists.TryGetValue(list.Key, out KeyedList? was);
            FindListChange change = new FindListChange();
            foreach (string key in list.Value.Order)
            {
                KeyedEntry entry = list.Value.ByKey[key];
                if (was == null || !was.ByKey.TryGetValue(key, out KeyedEntry? old))
                {
                    change.Added.Add(entry.Value);
                }
                else if (!string.Equals(old.Text, entry.Text, StringComparison.Ordinal))
                {
                    change.Changed.Add(entry.Value);
                }
            }

            if (was != null)
            {
                foreach (string key in was.Order)
                {
                    if (!list.Value.ByKey.ContainsKey(key))
                    {
                        change.Removed.Add(key);
                    }
                }
            }

            if (!change.IsEmpty)
            {
                lists.Add(list.Key, change);
            }
        }

        foreach (KeyValuePair<string, KeyedList> list in before.Lists)
        {
            if (!after.Lists.ContainsKey(list.Key))
            {
                FindListChange gone = new FindListChange();
                gone.Removed.AddRange(list.Value.Order);
                lists.Add(list.Key, gone);
            }
        }

        JObject? rest = string.Equals(before.RestText, after.RestText, StringComparison.Ordinal) ? null : after.Rest;
        return lists.Count == 0 && rest == null ? null : new FindChange(lists, rest);
    }
}
