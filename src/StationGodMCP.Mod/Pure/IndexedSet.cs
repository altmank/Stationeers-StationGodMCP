#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// A set of references kept as a list with each member's place (the world index's buckets, WorldIndex): add, remove and
/// contains by reference in constant time (a removal moves the last member into the gap), and the members read by place,
/// so copying a bucket is one array copy rather than a hash set's walk. Not thread-safe: the index locks around it.
/// </summary>
internal sealed class IndexedSet<T> where T : class
{
    private readonly List<T> _members = new List<T>();
    private readonly Dictionary<T, int> _places;

    internal IndexedSet(IEqualityComparer<T> comparer)
    {
        _places = new Dictionary<T, int>(comparer ?? throw new ArgumentNullException(nameof(comparer)));
    }

    internal int Count => _members.Count;

    internal T this[int place] => _members[place];

    internal bool Contains(T member) => _places.ContainsKey(member);

    /// <summary>Adds the member; false when it is in already.</summary>
    internal bool Add(T member)
    {
        if (_places.ContainsKey(member))
        {
            return false;
        }

        _places.Add(member, _members.Count);
        _members.Add(member);
        return true;
    }

    /// <summary>Takes the member out, the last member moving into its place; false when it was not in.</summary>
    internal bool Remove(T member)
    {
        if (!_places.TryGetValue(member, out int place))
        {
            return false;
        }

        int last = _members.Count - 1;
        T moved = _members[last];
        _members[place] = moved;
        _places[moved] = place;
        _members.RemoveAt(last);
        _places.Remove(member);
        return true;
    }

    internal void Clear()
    {
        _members.Clear();
        _places.Clear();
    }

    /// <summary>Appends every member to into, in place order (one array copy).</summary>
    internal void CopyTo(List<T> into) => into.AddRange(_members);
}
