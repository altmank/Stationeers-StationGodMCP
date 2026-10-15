#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using StationGodMCP.Pure.Protocol;

namespace StationGodMCP.Pure;

/// <summary>
/// The world model's one index (cost design A1): which things carry which prefab name, and which things are of which
/// class, kept beside the game's master lists so a list filtered by prefab or class reads a few buckets instead of
/// every thing. The game side reports each thing a master list took in (Arrived), each one a list may have let go
/// (Left) and each clearing of the lists (Reset). A thing is filed under the name it has when the next query runs,
/// because the game sets a new thing's prefab name after its first registration (DynamicThing.Awake runs inside
/// Instantiate); its classes (a bit set, bit n for class n) are read once, when it is first filed, since a thing's class
/// never changes. The first query or Warm after a Reset copies every thing the lists hold (the seed), so the index is
/// whole even when the lists were filled before it heard of them.
/// <para>
/// Filing waits for a query, or for Warm, which the game side calls once a frame: it files up to a budget of the seed and
/// of the arrivals told before its previous call (a whole frame back, so a new thing has its name), so a world's load is
/// filed a slice a frame and no query pays for it. A query files whatever is still waiting.
/// </para>
/// <para>
/// The index only proposes: every answer is checked against a list's own membership (isMember: for a class, its own
/// pool), and a filed thing that is in no list any more (inAnyList) is dropped. Each thing sits in one name bucket, its
/// name's (a name that several classes or prefabs carry is one bucket, names compared ignoring case as PrefabMatch
/// compares them), and in one class bucket per class it has.
/// </para>
/// </summary>
internal sealed class WorldIndex<T> where T : class
{
    /// <summary>Arrivals and leaves held for the next query before the index forgets and seeds again.</summary>
    internal const int MaximumBacklog = 200_000;

    /// <summary>Classes a thing can have: one bit each.</summary>
    internal const int MaximumClasses = 31;

    private readonly object _gate = new object();
    private readonly Func<T, string?> _nameOf;
    private readonly Func<T, int> _classesOf;
    private readonly Dictionary<string, HashSet<T>> _byName =
        new Dictionary<string, HashSet<T>>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<T>[] _byClass = new HashSet<T>[MaximumClasses];
    private readonly Dictionary<T, Filing> _filedUnder = new Dictionary<T, Filing>(ByReference.Instance);
    private readonly List<T> _arrived = new List<T>();
    private readonly List<T> _left = new List<T>();
    private readonly List<T> _seedWaiting = new List<T>();
    private int _seedFiled;
    private int _arrivedFiled;
    private int _arrivedSettled;
    private bool _seeded;
    private long _generation;
    private long _arrivedTotal;
    private long _leftTotal;

    /// <summary>nameOf: a thing's prefab name now; classesOf: its class bits, read once per thing.</summary>
    internal WorldIndex(Func<T, string?> nameOf, Func<T, int>? classesOf = null)
    {
        _nameOf = nameOf;
        _classesOf = classesOf ?? (static _ => 0);
        for (int index = 0; index < MaximumClasses; index++)
        {
            _byClass[index] = new HashSet<T>(ByReference.Instance);
        }
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

    /// <summary>Things filed under one class, after the last query.</summary>
    internal int ClassCount(int classNumber)
    {
        HashSet<T> bucket = _byClass[ClassIndex(classNumber)];
        lock (_gate)
        {
            return bucket.Count;
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

    // Arrivals and leaves wait for a query or Warm; a game whose frames stop would hold every thing it ever made. Past
    // MaximumBacklog waiting the index forgets everything and seeds again from the lists, which hold the same truth.
    private void ForgetWhenBacklogged()
    {
        if (_arrived.Count - _arrivedFiled + _left.Count > MaximumBacklog)
        {
            Forget();
        }
    }

    private void Forget()
    {
        _byName.Clear();
        foreach (HashSet<T> bucket in _byClass)
        {
            bucket.Clear();
        }

        _filedUnder.Clear();
        _arrived.Clear();
        _left.Clear();
        _seedWaiting.Clear();
        _seedFiled = 0;
        _arrivedFiled = 0;
        _arrivedSettled = 0;
        _seeded = false;
        _generation++;
    }

    /// <summary>
    /// The things of one master list (isMember) whose filed name the match keeps: by bucket for exact names, by every
    /// bucket name holding the text for prefab_contains alone. seed lists every thing the master lists hold, read only
    /// on the first query or Warm after a Reset. examined counts every filed thing in the matching buckets.
    /// </summary>
    internal List<T> Find(PrefabMatch match, Func<IEnumerable<T>> seed, Func<T, bool> inAnyList,
        Func<T, bool> isMember, out int examined)
    {
        if (!match.IsActive)
        {
            throw new ArgumentException("An index query needs a prefab, prefabs or " +
                "prefab_contains.", nameof(match));
        }

        Seed(seed);
        lock (_gate)
        {
            Drain(inAnyList);
            List<T> found = new List<T>();
            List<T>? gone = null;
            examined = 0;
            foreach (HashSet<T> bucket in BucketsFor(match))
            {
                examined += bucket.Count;
                Collect(bucket, inAnyList, isMember, found, ref gone);
            }

            Unfile(gone);
            return found;
        }
    }

    /// <summary>
    /// The things of one class (classNumber, 0 to MaximumClasses - 1) its own list holds (isMember); a filed thing in no
    /// list any more is dropped. seed as for Find.
    /// </summary>
    internal List<T> OfClass(int classNumber, Func<IEnumerable<T>> seed, Func<T, bool> inAnyList,
        Func<T, bool> isMember)
    {
        HashSet<T> bucket = _byClass[ClassIndex(classNumber)];
        Seed(seed);
        lock (_gate)
        {
            Drain(inAnyList);
            List<T> found = new List<T>(bucket.Count);
            List<T>? gone = null;
            Collect(bucket, inAnyList, isMember, found, ref gone);
            Unfile(gone);
            return found;
        }
    }

    /// <summary>
    /// A class's bucket as it is now, its membership unchecked: the candidates of a scan spread over frames, which checks
    /// each one as its turn comes (a thing gone meanwhile is refused then). Proportional to the class, never the world.
    /// </summary>
    internal List<T> Candidates(int classNumber, Func<IEnumerable<T>> seed, Func<T, bool> inAnyList)
    {
        HashSet<T> bucket = _byClass[ClassIndex(classNumber)];
        Seed(seed);
        lock (_gate)
        {
            Drain(inAnyList);
            List<T> candidates = new List<T>(bucket.Count);
            foreach (T thing in bucket)
            {
                candidates.Add(thing);
            }

            return candidates;
        }
    }

    /// <summary>
    /// Once a frame: files up to budget things, the seed's first, then the arrivals told before the previous Warm; with
    /// every arrival filed, drops the leaves no list holds. Main thread.
    /// </summary>
    internal void Warm(Func<IEnumerable<T>> seed, Func<T, bool> inAnyList, int budget)
    {
        Seed(seed);
        lock (_gate)
        {
            int left = budget;
            while (left > 0 && _seedFiled < _seedWaiting.Count)
            {
                File(_seedWaiting[_seedFiled++]);
                left--;
            }

            if (_seedFiled == _seedWaiting.Count)
            {
                _seedWaiting.Clear();
                _seedFiled = 0;
            }

            while (left > 0 && _arrivedFiled < _arrivedSettled)
            {
                File(_arrived[_arrivedFiled++]);
                left--;
            }

            if (_arrivedFiled == _arrived.Count)
            {
                _arrived.Clear();
                _arrivedFiled = 0;
                DropLeaves(inAnyList);
            }

            _arrivedSettled = _arrived.Count;
        }
    }

    // The seed copies the game's lists under their own locks, so it is read outside the gate: a hook never waits for the
    // gate while a list's lock is held here. A Reset between the copy and the filing (another thread) takes the copy
    // again.
    private void Seed(Func<IEnumerable<T>> seed)
    {
        while (true)
        {
            long generation;
            lock (_gate)
            {
                if (_seeded)
                {
                    return;
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

                _seedWaiting.AddRange(seeded);
                _seeded = true;
                return;
            }
        }
    }

    private static void Collect(HashSet<T> bucket, Func<T, bool> inAnyList, Func<T, bool> isMember, List<T> found,
        ref List<T>? gone)
    {
        foreach (T thing in bucket)
        {
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

    private void Drain(Func<T, bool> inAnyList)
    {
        for (int index = _seedFiled; index < _seedWaiting.Count; index++)
        {
            File(_seedWaiting[index]);
        }

        _seedWaiting.Clear();
        _seedFiled = 0;
        for (int index = _arrivedFiled; index < _arrived.Count; index++)
        {
            File(_arrived[index]);
        }

        _arrived.Clear();
        _arrivedFiled = 0;
        _arrivedSettled = 0;
        DropLeaves(inAnyList);
    }

    private void DropLeaves(Func<T, bool> inAnyList)
    {
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

    private static int ClassIndex(int classNumber) =>
        classNumber >= 0 && classNumber < MaximumClasses
            ? classNumber
            : throw new ArgumentOutOfRangeException(nameof(classNumber));

    private void File(T thing)
    {
        string name = _nameOf(thing) ?? string.Empty;
        int classes;
        if (_filedUnder.TryGetValue(thing, out Filing filed))
        {
            if (string.Equals(filed.Name, name, StringComparison.Ordinal))
            {
                return;
            }

            // A new name (the game names a thing after its first registration): its classes stay as filed.
            classes = filed.Classes;
            Unfile(thing);
        }
        else
        {
            classes = _classesOf(thing);
        }

        if (!_byName.TryGetValue(name, out HashSet<T> bucket))
        {
            bucket = new HashSet<T>(ByReference.Instance);
            _byName.Add(name, bucket);
        }

        bucket.Add(thing);
        for (int index = 0; index < MaximumClasses; index++)
        {
            if ((classes & (1 << index)) != 0)
            {
                _byClass[index].Add(thing);
            }
        }

        _filedUnder[thing] = new Filing(name, classes);
    }

    private void Unfile(List<T>? gone)
    {
        if (gone == null)
        {
            return;
        }

        foreach (T thing in gone)
        {
            Unfile(thing);
        }
    }

    private void Unfile(T thing)
    {
        if (!_filedUnder.TryGetValue(thing, out Filing filed))
        {
            return;
        }

        _filedUnder.Remove(thing);
        if (_byName.TryGetValue(filed.Name, out HashSet<T> bucket))
        {
            bucket.Remove(thing);
            if (bucket.Count == 0)
            {
                _byName.Remove(filed.Name);
            }
        }

        for (int index = 0; index < MaximumClasses; index++)
        {
            if ((filed.Classes & (1 << index)) != 0)
            {
                _byClass[index].Remove(thing);
            }
        }
    }

    // Where a thing is filed: its name's bucket and its classes' buckets.
    private readonly struct Filing
    {
        internal Filing(string name, int classes)
        {
            Name = name;
            Classes = classes;
        }

        internal string Name { get; }

        internal int Classes { get; }
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
