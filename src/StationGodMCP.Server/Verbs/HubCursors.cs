using System.Globalization;
using System.Text.Json.Nodes;

namespace StationGodMCP.Server;

/// <summary>
/// A hub cursor over a script or program reply's main list: the handler call to page with, its stream, the main list's
/// key, and either the mod's walk cursor (cursor_id) or the offset of the next page.
/// </summary>
internal sealed class HubCursor(string id, long stream, WhatEntry what, HandlerCall call, string main)
{
    internal string Id { get; } = id;

    internal long Stream { get; } = stream;

    internal WhatEntry What { get; } = what;

    internal HandlerCall Call { get; } = call;

    internal string Main { get; } = main;

    /// <summary>The mod's walk cursor ("k12") when the handler walks; null for offset pages.</summary>
    internal string? WalkCursor { get; set; }

    internal int NextOffset { get; set; }

    /// <summary>The first call's shape (fields, omit, list_limits): every page keeps it.</summary>
    internal System.Text.Json.JsonElement? Shape { get; init; }
}

/// <summary>The hub cursors of one hub, freed at the walk's end, when their stream ends and on a world change.</summary>
internal sealed class HubCursors
{
    private readonly object _gate = new();
    private readonly Dictionary<string, HubCursor> _cursors = new(StringComparer.Ordinal);
    private int _next;

    internal HubCursor Open(long stream, WhatEntry what, HandlerCall call, string main,
        System.Text.Json.JsonElement? shape = null)
    {
        lock (_gate)
        {
            _next++;
            HubCursor cursor = new("c" + _next.ToString(CultureInfo.InvariantCulture), stream, what, call, main)
            {
                Shape = shape
            };
            _cursors.Add(cursor.Id, cursor);
            return cursor;
        }
    }

    /// <summary>The stream's cursor; null when it is unknown, freed, or another stream's.</summary>
    internal HubCursor? Get(long stream, string id)
    {
        lock (_gate)
        {
            return _cursors.TryGetValue(id, out HubCursor? cursor) && cursor.Stream == stream ? cursor : null;
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
}
