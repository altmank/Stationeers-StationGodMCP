#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// The known name closest to a misspelt one, as the sidecar suggests it: a slip rather than another word (edit distance
/// at most two, or a quarter of the name), else the only name that is the given one with one word added before or after
/// it (prefab for prefab_contains).
/// </summary>
internal static class NearestName
{
    internal static string? Of(string given, IReadOnlyList<string> known)
    {
        string lower = given.Trim().ToLowerInvariant();
        string? slip = null;
        int best = int.MaxValue;
        foreach (string name in known)
        {
            int distance = Distance(lower, name.ToLowerInvariant());
            if (distance <= Math.Max(2, name.Length / 4) && distance < best)
            {
                slip = name;
                best = distance;
            }
        }

        if (slip != null || lower.Length == 0)
        {
            return slip;
        }

        string? extended = null;
        foreach (string name in known)
        {
            if (IsOneWordMore(lower, name.ToLowerInvariant()))
            {
                if (extended != null)
                {
                    return null;
                }

                extended = name;
            }
        }

        return extended;
    }

    private static bool IsOneWordMore(string given, string name)
    {
        string[] givenWords = given.Split('_');
        string[] words = name.Split('_');
        if (words.Length != givenWords.Length + 1)
        {
            return false;
        }

        bool before = true;
        bool after = true;
        for (int index = 0; index < givenWords.Length; index++)
        {
            before &= words[index] == givenWords[index];
            after &= words[index + 1] == givenWords[index];
        }

        return before || after;
    }

    private static int Distance(string first, string second)
    {
        int[] previous = new int[second.Length + 1];
        for (int column = 0; column <= second.Length; column++)
        {
            previous[column] = column;
        }

        for (int row = 1; row <= first.Length; row++)
        {
            int[] current = new int[second.Length + 1];
            current[0] = row;
            for (int column = 1; column <= second.Length; column++)
            {
                int substitution = previous[column - 1] + (first[row - 1] == second[column - 1] ? 0 : 1);
                current[column] = Math.Min(substitution, Math.Min(previous[column], current[column - 1]) + 1);
            }

            previous = current;
        }

        return previous[second.Length];
    }
}
