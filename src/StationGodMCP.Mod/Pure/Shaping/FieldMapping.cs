#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure.Shaping;

/// <summary>
/// A second reading of shape.fields after a reply reported selectors unmatched: each one whose key has exactly one
/// safe match in the reply (FieldMatch.Safe) is read as that key, and the reply says so in fields_mapped. Any other
/// stays unmatched; fields_closest names its near keys, if any. Only a single name (name) and a
/// list's key (containers.name) are read again; a deeper path's last key is not among the keys the reply reports.
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
