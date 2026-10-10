#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace StationGodMCP.Pure;

/// <summary>
/// Which things carry which prefab name, kept beside the game's master lists so a list filtered by prefab reads a few
/// buckets instead of every thing. The game side reports each thing a master list took in (Arrived), each one a list
/// may have let go (Left) and each clearing of the lists (Reset). A thing is filed under the name it has when the next
/// query runs, because the game sets a new thing's prefab name after its first registration (DynamicThing.Awake runs
/// inside Instantiate). The first query after a Reset files every thing the lists hold (the seed), so the index is
/// whole even when the lists were filled before it heard of them.
/// <para>
/// The index only proposes: every answer is checked against a master list's own membership (isMember), and a filed
/// thing that is in no list any more (inAnyList) is dropped. Each thing sits in one bucket, its name's; a name that
/// several classes or prefabs carry is one bucket, names compared ignoring case as PrefabMatch compares them.
/// </para>
/// </summary>
internal sealed class PrefabIndex<T> where T : class
{
    /// <summary>Arrivals and leaves held for the next query before the index forgets and seeds again.</summary>
    internal const int MaximumBacklog = 200_000;

    private readonly object _gate = new object();
    private readonly Func<T, string?> _nameOf;
    private readonly Dictionary<string, HashSet<T>> _byName =
        new Dictionary<string, HashSet<T>>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<T, string> _filedUnder = new Dictionary<T, string>(ByReference.Instance);
    private readonly List<T> _arrived = new List<T>();
    private readonly List<T> _left = new List<T>();
    private bool _seeded;
    private long _generation;
    private long _arrivedTotal;
    private long _leftTotal;

    internal PrefabIndex(Func<T, string?> nameOf)
    {
        _nameOf = nameOf;
    }

    /// <summary>Things filed, after the last query (pending arrivals are not counted).</summary>
    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _filedUnder.Count;
            }
        }
    }

    /// <summary>Distinct prefab names filed.</summary>
    internal int NameCount
    {
        get
        {
            lock (_gate)
            {
                return _byName.Count;
            }
        }
    }

    /// <summary>Arrivals reported since the index was made; a Reset keeps the count.</summary>
    internal long ArrivedTotal
    {
        get
        {
            lock (_gate)
            {
                return _arrivedTotal;
            }
        }
    }

    /// <summary>Leaves reported since the index was made; a Reset keeps the count.</summary>
    internal long LeftTotal
    {
        get
        {
            lock (_gate)
            {
                return _leftTotal;
            }
        }
    }

    /// <summary>A master list took the thing in; filed at the next query. Any thread.</summary>
    internal void Arrived(T thing)
    {
        lock (_gate)
        {
            _arrivedTotal++;
            _arrived.Add(thing);
            ForgetWhenBacklogged();
        }
    }

    /// <summary>A master list may have let the thing go; dropped at the next query when no list holds it. Any thread.</summary>
    internal void Left(T thing)
    {
        lock (_gate)
        {
            _leftTotal++;
            _left.Add(thing);
            ForgetWhenBacklogged();
        }
    }

    /// <summary>The master lists were cleared, or a world was left: the next query seeds again.</summary>
    internal void Reset()
    {
        lock (_gate)
        {
            Forget();
        }
    }

    // Arrivals and leaves wait for a query; a game nobody queries would hold every thing it ever made. Past
    // MaximumBacklog the index forgets everything and the next query seeds from the lists, which hold the same truth.
    private void ForgetWhenBacklogged()
    {
        if (_arrived.Count + _left.Count > MaximumBacklog)
        {
            Forget();
        }
    }

    private void Forget()
    {
        _byName.Clear();
        _filedUnder.Clear();
        _arrived.Clear();
        _left.Clear();
        _seeded = false;
        _generation++;
    }

    /// <summary>
    /// The things of one master list (isMember) whose filed name the match keeps: by bucket for exact names, by every
    /// bucket name holding the text for prefab_contains alone. seed lists every thing the master lists hold, read only
    /// on the first query after a Reset. examined counts every filed thing in the matching buckets.
    /// </summary>
    internal List<T> Find(PrefabMatch match, Func<IEnumerable<T>> seed, Func<T, bool> inAnyList,
        Func<T, bool> isMember, out int examined)
    {
        if (!match.IsActive)
        {
            throw new ArgumentException("An index query needs a prefab, prefabs or prefab_contains.", nameof(match));
        }

        // The seed copies the game's lists under their own locks, so it is read outside the gate: a hook never waits
        // for the gate while a list's lock is held here.
        // A Reset between the copy and the filing (another thread) takes the copy again.
        while (true)
        {
            long generation;
            lock (_gate)
            {
                if (_seeded)
                {
                    break;
                }

                generation = _generation;
            }

            IEnumerable<T> seeded = seed();
            lock (_gate)
            {
                if (_seeded || generation != _generation)
                {
                    continue;
                }

                foreach (T thing in seeded)
                {
                    File(thing);
                }

                _seeded = true;
                break;
            }
        }

        lock (_gate)
        {
            Drain(inAnyList);
            List<HashSet<T>> buckets = BucketsFor(match);
            List<T> found = new List<T>();
            List<T>? gone = null;
            examined = 0;
            foreach (HashSet<T> bucket in buckets)
            {
                foreach (T thing in bucket)
                {
                    examined++;
                    if (isMember(thing))
                    {
                        found.Add(thing);
                    }
                    else if (!inAnyList(thing))
                    {
                        (gone ??= new List<T>()).Add(thing);
                    }
                }
            }

            if (gone != null)
            {
                foreach (T thing in gone)
                {
                    Unfile(thing);
                }
            }

            return found;
        }
    }

    private void Drain(Func<T, bool> inAnyList)
    {
        foreach (T thing in _arrived)
        {
            File(thing);
        }

        _arrived.Clear();
        foreach (T thing in _left)
        {
            if (!inAnyList(thing))
            {
                Unfile(thing);
            }
        }

        _left.Clear();
    }

    private List<HashSet<T>> BucketsFor(PrefabMatch match)
    {
        List<HashSet<T>> buckets = new List<HashSet<T>>();
        if (match.Exact != null)
        {
            foreach (string name in match.Exact)
            {
                if (_byName.TryGetValue(name, out HashSet<T> bucket))
                {
                    buckets.Add(bucket);
                }
            }

            return buckets;
        }

        foreach (KeyValuePair<string, HashSet<T>> entry in _byName)
        {
            if (match.Keeps(entry.Key))
            {
                buckets.Add(entry.Value);
            }
        }

        return buckets;
    }

    private void File(T thing)
    {
        string name = _nameOf(thing) ?? string.Empty;
        if (_filedUnder.TryGetValue(thing, out string? filed))
        {
            if (string.Equals(filed, name, StringComparison.Ordinal))
            {
                return;
            }

            Unfile(thing);
        }

        if (!_byName.TryGetValue(name, out HashSet<T> bucket))
        {
            bucket = new HashSet<T>(ByReference.Instance);
            _byName.Add(name, bucket);
        }

        bucket.Add(thing);
        _filedUnder[thing] = name;
    }

    private void Unfile(T thing)
    {
        if (!_filedUnder.TryGetValue(thing, out string? filed))
        {
            return;
        }

        _filedUnder.Remove(thing);
        if (_byName.TryGetValue(filed, out HashSet<T> bucket))
        {
            bucket.Remove(thing);
            if (bucket.Count == 0)
            {
                _byName.Remove(filed);
            }
        }
    }

    // Unity objects compare by native instance and a destroyed one equals null; the index holds the managed object
    // itself, so by reference.
    private sealed class ByReference : IEqualityComparer<T>
    {
        internal static readonly ByReference Instance = new ByReference();

        public bool Equals(T? x, T? y) => ReferenceEquals(x, y);

        public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
