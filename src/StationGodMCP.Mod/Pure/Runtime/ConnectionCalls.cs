#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>One method's calls on one connection: how many, how many answered an error, main-thread ms in total.</summary>
internal sealed class MethodCallCount
{
    internal MethodCallCount(string method)
    {
        Method = method;
    }

    internal string Method { get; }

    internal long Calls { get; private set; }

    internal long Errors { get; private set; }

    internal double TotalMs { get; private set; }

    internal void Add(bool ok, double ms)
    {
        Calls++;
        Errors += ok ? 0 : 1;
        TotalMs += ms;
    }
}

/// <summary>
/// The calls one connection made, per method, for mod_info's connections. Only method names the mod answers are
/// counted (the caller passes known names), so the table is bounded by the method list. Recorded and read on the main
/// thread only, where calls run and mod_info answers.
/// </summary>
internal sealed class ConnectionCalls
{
    private readonly Dictionary<string, MethodCallCount> _byMethod =
        new Dictionary<string, MethodCallCount>(StringComparer.Ordinal);

    /// <summary>Methods called at least once.</summary>
    internal int MethodCount => _byMethod.Count;

    internal void Record(string method, bool ok, double ms)
    {
        if (!_byMethod.TryGetValue(method, out MethodCallCount count))
        {
            count = new MethodCallCount(method);
            _byMethod.Add(method, count);
        }

        count.Add(ok, ms);
    }

    /// <summary>The most-called methods, most first (then by name), at most limit of them.</summary>
    internal List<MethodCallCount> Top(int limit)
    {
        List<MethodCallCount> all = new List<MethodCallCount>(_byMethod.Values);
        all.Sort(static (a, b) =>
        {
            int byCalls = b.Calls.CompareTo(a.Calls);
            return byCalls != 0 ? byCalls : string.CompareOrdinal(a.Method, b.Method);
        });
        if (all.Count > limit)
        {
            all.RemoveRange(limit, all.Count - limit);
        }

        return all;
    }
}
