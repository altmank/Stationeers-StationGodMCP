#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// The last lines a console printed, newest replacing oldest past Capacity. Thread safe: the game prints from any
/// thread. A dedicated server's console keeps no buffer of its own, so this is what read_console reads there.
/// </summary>
internal sealed class ConsoleRing<T>
{
    private readonly T[] _lines;
    private readonly object _gate = new object();
    private int _next;
    private int _count;

    internal ConsoleRing(int capacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "A ring holds at least one line.");
        }

        _lines = new T[capacity];
    }

    internal int Capacity => _lines.Length;

    internal void Add(T line)
    {
        lock (_gate)
        {
            _lines[_next] = line;
            _next = (_next + 1) % _lines.Length;
            _count = Math.Min(_count + 1, _lines.Length);
        }
    }

    /// <summary>The newest count lines (or fewer, all it holds), oldest first.</summary>
    internal List<T> Recent(int count)
    {
        lock (_gate)
        {
            int taken = Math.Min(Math.Max(count, 0), _count);
            List<T> recent = new List<T>(taken);
            int start = (_next - taken + _lines.Length) % _lines.Length;
            for (int index = 0; index < taken; index++)
            {
                recent.Add(_lines[(start + index) % _lines.Length]);
            }

            return recent;
        }
    }
}
