#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure.Catalogue;

/// <summary>
/// Argument names the handlers read that their method's catalogue entry does not declare, counted per method and
/// name since the mod loaded (mod_info.runtime.catalogue_drift; it should stay empty). Only misses are counted, so a
/// declared name costs the caller one set lookup and nothing else. Thread-safe.
/// </summary>
internal sealed class CatalogueDrift
{
    private readonly object _gate = new object();
    private readonly Dictionary<string, Dictionary<string, long>> _misses =
        new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);

    /// <summary>Counts a miss; true the first time this method read this name (the caller logs that one).</summary>
    internal bool Miss(string method, string argument)
    {
        lock (_gate)
        {
            if (!_misses.TryGetValue(method, out Dictionary<string, long> names))
            {
                names = new Dictionary<string, long>(StringComparer.Ordinal);
                _misses.Add(method, names);
            }

            bool first = !names.TryGetValue(argument, out long count);
            names[argument] = count + 1;
            return first;
        }
    }

    /// <summary>Every miss, by method then argument name.</summary>
    internal List<DriftCount> Snapshot()
    {
        List<DriftCount> counts = new List<DriftCount>();
        lock (_gate)
        {
            foreach (KeyValuePair<string, Dictionary<string, long>> method in _misses)
            {
                foreach (KeyValuePair<string, long> name in method.Value)
                {
                    counts.Add(new DriftCount(method.Key, name.Key, name.Value));
                }
            }
        }

        counts.Sort(static (a, b) =>
        {
            int byMethod = string.CompareOrdinal(a.Method, b.Method);
            return byMethod != 0 ? byMethod : string.CompareOrdinal(a.Argument, b.Argument);
        });
        return counts;
    }
}

/// <summary>One undeclared argument name a method's handler read, and how often.</summary>
internal sealed class DriftCount
{
    internal DriftCount(string method, string argument, long reads)
    {
        Method = method;
        Argument = argument;
        Reads = reads;
    }

    internal string Method { get; }

    internal string Argument { get; }

    internal long Reads { get; }
}
