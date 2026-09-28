#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Api.Shared;

/// <summary>
/// Jobs waiting for the one job slot (wait: true), first in first out, each with the id it was given when queued and
/// what starts it. Bounded: a full line refuses more.
/// </summary>
internal sealed class JobLine<T>
{
    private readonly List<KeyValuePair<string, T>> _waiting = new List<KeyValuePair<string, T>>();

    internal JobLine(int capacity)
    {
        Capacity = capacity;
    }

    internal int Capacity { get; }

    internal int Count => _waiting.Count;

    internal bool IsFull => _waiting.Count >= Capacity;

    /// <summary>Adds a job at the back; false when the line is full.</summary>
    internal bool Add(string id, T start)
    {
        if (IsFull)
        {
            return false;
        }

        _waiting.Add(new KeyValuePair<string, T>(id, start));
        return true;
    }

    /// <summary>1 for the next job to start, 2 for the one after it; null when the id is not waiting.</summary>
    internal int? PositionOf(string id)
    {
        for (int index = 0; index < _waiting.Count; index++)
        {
            if (_waiting[index].Key == id)
            {
                return index + 1;
            }
        }

        return null;
    }

    /// <summary>Takes the front job; false when none waits.</summary>
    internal bool TryTake(out string id, out T start)
    {
        if (_waiting.Count == 0)
        {
            id = string.Empty;
            start = default!;
            return false;
        }

        id = _waiting[0].Key;
        start = _waiting[0].Value;
        _waiting.RemoveAt(0);
        return true;
    }

    /// <summary>The waiting jobs, front first.</summary>
    internal IReadOnlyList<KeyValuePair<string, T>> Entries() => _waiting;

    /// <summary>Forgets every waiting job (the mod is unloading), returning their ids.</summary>
    internal List<string> Clear()
    {
        List<string> ids = new List<string>(_waiting.Count);
        foreach (KeyValuePair<string, T> entry in _waiting)
        {
            ids.Add(entry.Key);
        }

        _waiting.Clear();
        return ids;
    }
}
