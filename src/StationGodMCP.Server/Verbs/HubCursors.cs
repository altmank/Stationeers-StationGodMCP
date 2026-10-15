using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace StationGodMCP.Server;

/// <summary>
/// A hub cursor over a script or program reply's main list: the verb call to page with (its what and options), its
/// stream, the main list's key, and the mod's walk cursor its next page goes on with.
/// </summary>
internal sealed class HubCursor(string id, long stream, WhatEntry what, JsonObject options, string main, string modCursor)
{
    internal string Id { get; } = id;

    internal long Stream { get; } = stream;

    internal WhatEntry What { get; } = what;

    internal JsonObject Options { get; } = options;

    internal string Main { get; } = main;

    /// <summary>The mod's walk cursor (the last page's cursor_id), sent as cursor on the next page.</summary>
    internal string ModCursor { get; set; } = modCursor;

    /// <summary>The first call's shape (fields, omit, list_limits): every page keeps it.</summary>
    internal System.Text.Json.JsonElement? Shape { get; init; }

    /// <summary>When it was opened or last paged (HubCursors' clock, ms).</summary>
    internal long LastUsedMs { get; set; }

    /// <summary>The store's use count when it was opened or last paged: its recency.</summary>
    internal long LastUse { get; set; }
}

/// <summary>
/// The hub cursors of one hub, freed at the walk's end, when their stream ends, on a world change, and by two bounds
/// (cost design A4): at most MaximumCursors held, the least recently used evicted first when another opens, and none
/// idle longer than MaximumIdleMs (freed when the store is next used). A freed cursor answers cursor_expired, as the
/// mod's own walk does when its bounds evict it.
/// </summary>
internal sealed class HubCursors
{
    /// <summary>Cursors held by one hub: a script's loops of finds each hold one until their walk ends.</summary>
    internal const int DefaultMaximumCursors = 256;

    /// <summary>A walk not paged for ten minutes is dropped: its caller has moved on.</summary>
    internal const long DefaultMaximumIdleMs = 600_000;

    private readonly object _gate = new();
    private readonly Dictionary<string, HubCursor> _cursors = new(StringComparer.Ordinal);
    private readonly Func<long> _clockMs;
    private int _next;
    private long _uses;

    internal HubCursors(int maximumCursors = DefaultMaximumCursors, long maximumIdleMs = DefaultMaximumIdleMs,
        Func<long>? clockMs = null)
    {
        MaximumCursors = maximumCursors > 0 ? maximumCursors : throw new ArgumentOutOfRangeException(nameof(maximumCursors));
        MaximumIdleMs = maximumIdleMs > 0 ? maximumIdleMs : throw new ArgumentOutOfRangeException(nameof(maximumIdleMs));
        _clockMs = clockMs ?? (static () => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency);
    }

    internal int MaximumCursors { get; }

    internal long MaximumIdleMs { get; }

    internal HubCursor Open(long stream, WhatEntry what, JsonObject options, string main, string modCursor,
        System.Text.Json.JsonElement? shape = null)
    {
        lock (_gate)
        {
            long now = _clockMs();
            DropIdle(now);
            while (_cursors.Count >= MaximumCursors)
            {
                _cursors.Remove(LeastRecent().Id);
            }

            _next++;
            HubCursor cursor = new("c" + _next.ToString(CultureInfo.InvariantCulture), stream, what, options, main,
                modCursor)
            {
                Shape = shape,
                LastUsedMs = now,
                LastUse = ++_uses
            };
            _cursors.Add(cursor.Id, cursor);
            return cursor;
        }
    }

    /// <summary>
    /// The stream's cursor, marked used; null when it is unknown, freed, evicted, idle too long or another stream's.
    /// </summary>
    internal HubCursor? Get(long stream, string id)
    {
        lock (_gate)
        {
            long now = _clockMs();
            DropIdle(now);
            if (!_cursors.TryGetValue(id, out HubCursor? cursor) || cursor.Stream != stream)
            {
                return null;
            }

            cursor.LastUsedMs = now;
            cursor.LastUse = ++_uses;
            return cursor;
        }
    }

    internal void Free(string id)
    {
        lock (_gate)
        {
            _cursors.Remove(id);
        }
    }

    internal void FreeStream(long stream)
    {
        lock (_gate)
        {
            List<string> ended = [];
            foreach ((string id, HubCursor cursor) in _cursors)
            {
                if (cursor.Stream == stream)
                {
                    ended.Add(id);
                }
            }

            foreach (string id in ended)
            {
                _cursors.Remove(id);
            }
        }
    }

    internal void Clear()
    {
        lock (_gate)
        {
            _cursors.Clear();
        }
    }

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _cursors.Count;
            }
        }
    }

    /// <summary>The entries of a page: the main list of the reply.</summary>
    internal static JsonArray Entries(JsonNode? reply, string main) =>
        reply?[main] is JsonArray entries ? (JsonArray)entries.DeepClone() : [];

    private void DropIdle(long now)
    {
        List<string>? idle = null;
        foreach ((string id, HubCursor cursor) in _cursors)
        {
            if (now - cursor.LastUsedMs > MaximumIdleMs)
            {
                (idle ??= []).Add(id);
            }
        }

        if (idle != null)
        {
            foreach (string id in idle)
            {
                _cursors.Remove(id);
            }
        }
    }

    private HubCursor LeastRecent()
    {
        HubCursor? oldest = null;
        foreach (HubCursor cursor in _cursors.Values)
        {
            if (oldest == null || cursor.LastUse < oldest.LastUse)
            {
                oldest = cursor;
            }
        }

        return oldest!;
    }
}
