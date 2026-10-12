namespace StationGodMCP.Server;

/// <summary>
/// The valid names nearest a name given: by edit distance (case ignored), at most three, closest first. A name counts
/// as near when it is a slip (distance at most a third of the longer name, and at least 2 allowed) or one holds the
/// other.
/// </summary>
internal static class Nearest
{
    internal const int Most = 3;

    internal static List<string> Of(string given, IReadOnlyList<string> candidates)
    {
        List<(int Distance, string Name)> near = [];
        string lowered = given.ToLowerInvariant();
        foreach (string candidate in candidates)
        {
            string other = candidate.ToLowerInvariant();
            int distance = Distance(lowered, other);
            int allowed = Math.Max(2, Math.Max(lowered.Length, other.Length) / 3);
            bool holds = lowered.Length >= 3 && (other.Contains(lowered, StringComparison.Ordinal) ||
                                                 lowered.Contains(other, StringComparison.Ordinal));
            if (distance <= allowed || holds)
            {
                near.Add((distance, candidate));
            }
        }

        near.Sort(static (a, b) => a.Distance != b.Distance
            ? a.Distance.CompareTo(b.Distance)
            : string.CompareOrdinal(a.Name, b.Name));
        List<string> names = [];
        for (int index = 0; index < near.Count && index < Most; index++)
        {
            names.Add(near[index].Name);
        }

        return names;
    }

    /// <summary>All the candidates, the nearest (up to three) first and the rest in their order.</summary>
    internal static List<string> NearestFirst(string given, IReadOnlyList<string> candidates)
    {
        List<string> ordered = Of(given, candidates);
        foreach (string candidate in candidates)
        {
            if (!ordered.Contains(candidate))
            {
                ordered.Add(candidate);
            }
        }

        return ordered;
    }

    /// <summary>Levenshtein distance.</summary>
    internal static int Distance(string a, string b)
    {
        int[] previous = new int[b.Length + 1];
        int[] current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
