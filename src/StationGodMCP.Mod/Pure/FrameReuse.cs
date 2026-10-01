#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>Something shown until a time (Time.realtimeSinceStartup seconds).</summary>
internal interface IExpiring
{
    float Until { get; }
}

/// <summary>Per-frame helpers that allocate nothing (the highlight drawing runs every frame while marks show).</summary>
internal static class Expiry
{
    /// <summary>
    /// Removes, in place and keeping the order of the rest, every item whose time is up (Until at or before now).
    /// Returns how many were removed. No closure, no new list.
    /// </summary>
    internal static int RemoveExpired<T>(List<T> items, float now) where T : IExpiring
    {
        int kept = 0;
        for (int index = 0; index < items.Count; index++)
        {
            if (items[index].Until > now)
            {
                items[kept++] = items[index];
            }
        }

        int removed = items.Count - kept;
        if (removed > 0)
        {
            items.RemoveRange(kept, removed);
        }

        return removed;
    }

    /// <summary>The size of the batch that starts at start when total items go in batches of at most size.</summary>
    internal static int BatchCount(int total, int start, int size) => total - start < size ? total - start : size;
}

/// <summary>
/// Pairs of values grouped by key, filled anew every frame without new lists: BeginFrame empties each group's lists but
/// keeps them (and their capacity) for the next frame; a group left empty for a frame is skipped by the reader. Reset
/// drops every group (when nothing is shown any more, so meshes no longer drawn are let go).
/// </summary>
internal sealed class FrameGroups<TKey, TFirst, TSecond> where TKey : notnull
{
    private readonly Dictionary<TKey, Group> _groups = new Dictionary<TKey, Group>();

    internal int GroupCount => _groups.Count;

    internal void Add(TKey key, TFirst first, TSecond second)
    {
        if (!_groups.TryGetValue(key, out Group group))
        {
            group = new Group();
            _groups[key] = group;
        }

        group.First.Add(first);
        group.Second.Add(second);
    }

    internal void BeginFrame()
    {
        foreach (KeyValuePair<TKey, Group> entry in _groups)
        {
            entry.Value.First.Clear();
            entry.Value.Second.Clear();
        }
    }

    internal void Reset() => _groups.Clear();

    /// <summary>The groups as a struct enumerator (no allocation); groups emptied this frame have Count 0.</summary>
    public Dictionary<TKey, Group>.Enumerator GetEnumerator() => _groups.GetEnumerator();

    internal sealed class Group
    {
        internal List<TFirst> First { get; } = new List<TFirst>();

        internal List<TSecond> Second { get; } = new List<TSecond>();

        internal int Count => First.Count;
    }
}
