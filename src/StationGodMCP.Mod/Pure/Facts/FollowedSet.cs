#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace StationGodMCP.Pure.Facts;

/// <summary>
/// A set of things kept by the game's change points (a followed fact, cost design A2): the things that hold a property
/// now (holds, read live). A change point on any thread tells what it changed and whether the thing holds now
/// (Changed); only a thing whose answer differs from the set is queued, so a property that changes every tick (decay)
/// queues once per flip. The main thread files the queue (Drain): each thing read live again, the set updated, then
/// read once more; a change that raced the filing is queued again, so the set never stays wrong after a change point
/// has run. Things are compared by reference (Unity objects override equality).
/// </summary>
internal sealed class FollowedSet<T> where T : class
{
    private readonly Func<T, bool> _holds;
    private readonly ConcurrentDictionary<T, byte> _members = new ConcurrentDictionary<T, byte>(ByReference.Instance);
    private readonly object _gate = new object();
    private List<T> _queued = new List<T>();
    private List<T> _spare = new List<T>();
    private int _spareFiled;
    private long _filed;
    private long _moved;

    /// <summary>holds: whether a thing holds the property now (main thread).</summary>
    internal FollowedSet(Func<T, bool> holds)
    {
        _holds = holds;
    }

    /// <summary>Things in the set.</summary>
    internal int Count => _members.Count;

    /// <summary>Things filed since the mod loaded.</summary>
    internal long Filed => _filed;

    /// <summary>Times a thing joined or left the set since the mod loaded (a find subscription's news). Main thread.</summary>
    internal long Moved => _moved;

    /// <summary>Things waiting to be filed.</summary>
    internal int Waiting
    {
        get
        {
            lock (_gate)
            {
                return _queued.Count + _spare.Count - _spareFiled;
            }
        }
    }

    internal bool Contains(T thing) => _members.ContainsKey(thing);

    /// <summary>A change point changed the thing, which holds now or not (as it reads on that thread). Any thread.</summary>
    internal void Changed(T thing, bool holdsNow)
    {
        if (holdsNow != _members.ContainsKey(thing))
        {
            Queue(thing);
        }
    }

    /// <summary>A thing to read live at the next Drain, whatever the set says (a thing just filed by the world). Any thread.</summary>
    internal void Queue(T thing)
    {
        lock (_gate)
        {
            _queued.Add(thing);
        }
    }

    /// <summary>Files up to budget queued things, oldest first; true when none is left waiting. Main thread.</summary>
    internal bool Drain(int budget)
    {
        while (budget > 0)
        {
            if (_spareFiled == _spare.Count)
            {
                _spare.Clear();
                _spareFiled = 0;
                lock (_gate)
                {
                    if (_queued.Count == 0)
                    {
                        return true;
                    }

                    (_queued, _spare) = (_spare, _queued);
                }
            }

            File(_spare[_spareFiled++]);
            budget--;
        }

        return Waiting == 0;
    }

    /// <summary>The set's members now (a copy, proportional to the set). Main thread.</summary>
    internal List<T> Members()
    {
        List<T> members = new List<T>(_members.Count);
        foreach (KeyValuePair<T, byte> member in _members)
        {
            members.Add(member.Key);
        }

        return members;
    }

    /// <summary>Drops a thing the world no longer holds. Main thread.</summary>
    internal void Remove(T thing)
    {
        if (_members.TryRemove(thing, out _))
        {
            _moved++;
        }
    }

    /// <summary>A world left: empty, nothing waits.</summary>
    internal void Clear()
    {
        lock (_gate)
        {
            _queued.Clear();
            _spare.Clear();
            _spareFiled = 0;
        }

        _members.Clear();
    }

    private void File(T thing)
    {
        _filed++;
        bool holds = _holds(thing);
        if (holds ? _members.TryAdd(thing, 0) : _members.TryRemove(thing, out _))
        {
            _moved++;
        }

        // A change point that ran between the read and the update saw the set as it was: read again.
        if (_holds(thing) != holds)
        {
            Queue(thing);
        }
    }

    private sealed class ByReference : IEqualityComparer<T>
    {
        internal static readonly ByReference Instance = new ByReference();

        public bool Equals(T? x, T? y) => ReferenceEquals(x, y);

        public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
