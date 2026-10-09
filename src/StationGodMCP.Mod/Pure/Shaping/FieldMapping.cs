#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure.Shaping;

/// <summary>
/// A second reading of shape.fields after a reply reported selectors unmatched: each one whose key has exactly one
/// safe match in the reply (FieldMatch.Safe) is read as that key, and the reply says so in fields_mapped. Any other
/// stays unmatched; fields_closest names its near keys, if any. Only a single name (name) and a
/// list's key (containers.name) are read again; a single name that is no entry key but the last key of one deeper path
/// (DeepPaths) is read as that path.
/// </summary>
internal static class FieldMapping
{
    /// <summary>The shape with the near misses read as their one close key; null when no selector maps.</summary>
    internal static ShapeRequest? Remap(ShapeRequest shape, ShapeOutcome outcome)
    {
        FieldSelectors? fields = shape.Fields;
        if (fields == null || outcome.Unmatched.Count == 0)
        {
            return null;
        }

        Dictionary<string, string>? mapped = null;
        FieldSelector[] selectors = new FieldSelector[fields.Count];
        for (int index = 0; index < fields.Count; index++)
        {
            selectors[index] = fields[index];
        }

        foreach (int index in outcome.Unmatched)
        {
            FieldSelector selector = fields[index];
            string? key = KeyOf(selector);
            // A costly key the handler skipped is never read instead (the reply may lack it); one as close as another
            // key makes that one ambiguous.
            string? close = key != null ? FieldMatch.Safe(key, outcome.ValidKeys) : null;
            if (close == null && selector is FieldSelector.Name single &&
                DeepPaths.Safe(single.Key, outcome.DeepPaths) is string deep)
            {
                string[] names = deep.Split('.');
                string[] keys = new string[names.Length - 1];
                Array.Copy(names, 1, keys, 0, keys.Length);
                selectors[index] = new FieldSelector.Path(selector.Text, names[0], keys);
                (mapped ??= new Dictionary<string, string>(StringComparer.Ordinal))[selector.Text] = deep;
                continue;
            }

            if (close == null || !Contains(outcome.SeenKeys, close) || Contains(shape.SkippedKeys, close))
            {
                continue;
            }

            selectors[index] = selector switch
            {
                FieldSelector.Path path => new FieldSelector.Path(selector.Text, path.List, new[] { close }),
                _ => new FieldSelector.Name(selector.Text, close)
            };
            (mapped ??= new Dictionary<string, string>(StringComparer.Ordinal))[selector.Text] = close;
        }

        return mapped == null
            ? null
            : new ShapeRequest(FieldSelectors.Of(selectors), shape.Limits, shape.MaxBytes, shape.Omit, mapped);
    }

    private static bool Contains(IEnumerable<string> keys, string key)
    {
        foreach (string seen in keys)
        {
            if (string.Equals(seen, key, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The key fields_closest and the mapping look up: a single name, or a list's key; null otherwise.</summary>
    internal static string? KeyOf(FieldSelector selector) => selector switch
    {
        FieldSelector.Name name => name.Key,
        FieldSelector.Path { Keys.Count: 1 } path => path.Keys[0],
        _ => null
    };
}

/// <summary>
/// A single fields name read against the key paths below the entries' own keys (atmospheres.atmosphere.total_mol): a
/// name that is the last key of exactly one such path, or its one safe match (FieldMatch.Safe), is read as that path;
/// the paths whose last key is the name or one of its near keys are named in fields_closest.
/// </summary>
internal static class DeepPaths
{
    /// <summary>The one path the name stands for; null when none or several do.</summary>
    internal static string? Safe(string name, IReadOnlyCollection<string> paths)
    {
        Dictionary<string, List<string>> byLast = ByLastKey(paths);
        string? last = byLast.ContainsKey(name) ? name : FieldMatch.Safe(name, byLast.Keys);
        return last != null && byLast.TryGetValue(last, out List<string> ending) && ending.Count == 1
            ? ending[0]
            : null;
    }

    /// <summary>The paths ending in the name or a near key of it, sorted, at most FieldMatch.MaximumCandidates.</summary>
    internal static List<string> Closest(string name, IReadOnlyCollection<string> paths)
    {
        Dictionary<string, List<string>> byLast = ByLastKey(paths);
        List<string> lasts = byLast.ContainsKey(name)
            ? new List<string> { name }
            : FieldMatch.Closest(name, byLast.Keys);
        List<string> closest = new List<string>();
        foreach (string last in lasts)
        {
            closest.AddRange(byLast[last]);
        }

        closest.Sort(StringComparer.Ordinal);
        if (closest.Count > FieldMatch.MaximumCandidates)
        {
            closest.RemoveRange(FieldMatch.MaximumCandidates, closest.Count - FieldMatch.MaximumCandidates);
        }

        return closest;
    }

    private static Dictionary<string, List<string>> ByLastKey(IReadOnlyCollection<string> paths)
    {
        Dictionary<string, List<string>> byLast = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (string path in paths)
        {
            string last = path.Substring(path.LastIndexOf('.') + 1);
            if (!byLast.TryGetValue(last, out List<string> ending))
            {
                byLast[last] = ending = new List<string>();
            }

            ending.Add(path);
        }

        return byLast;
    }
}
