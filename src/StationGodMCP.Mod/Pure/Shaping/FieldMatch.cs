#nullable enable

using System;
using System.Collections.Generic;
using System.Text;

namespace StationGodMCP.Pure.Shaping;

/// <summary>
/// The reply keys close to a fields name that matched none. A safe match is read in its place (FieldMapping): the same
/// words written another way (REFERENCE_ID, displayName), the same words in another order (slots_used for used_slots),
/// one naming qualifier more or fewer (name for display_name, distance for distance_m, used_slots for used_slot_count),
/// a unit spelt out (total_moles for total_mol), or one typing slip in a long name (prefab_nme). Any other near key is only named (fields_closest): one other word
/// more or fewer (refund for refund_enabled), singular for plural (network for networks), two slips.
/// </summary>
internal static class FieldMatch
{
    // Units spelt out, read as the short unit word the reply keys use (total_moles for total_mol).
    private static readonly Dictionary<string, string> UnitWords = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["mole"] = "mol", ["moles"] = "mol", ["mols"] = "mol",
        ["kelvin"] = "k",
        ["kilopascal"] = "kpa", ["kilopascals"] = "kpa",
        ["metre"] = "m", ["metres"] = "m", ["meter"] = "m", ["meters"] = "m",
        ["joule"] = "j", ["joules"] = "j",
        ["watt"] = "w", ["watts"] = "w",
        ["second"] = "s", ["seconds"] = "s",
        ["kilogram"] = "kg", ["kilograms"] = "kg",
        ["litre"] = "l", ["litres"] = "l", ["liter"] = "l", ["liters"] = "l",
        ["degree"] = "deg", ["degrees"] = "deg",
        ["percent"] = "pct"
    };

    /// <summary>The most keys Closest names.</summary>
    internal const int MaximumCandidates = 5;

    // Words the reply key names add to a plain name: the shown name, a count, and units.
    private static readonly HashSet<string> Qualifiers = new HashSet<string>(StringComparer.Ordinal)
    {
        "display", "count", "m", "k", "kpa", "l", "mol", "j", "w", "s", "ms", "kg", "deg", "pct", "n"
    };

    /// <summary>The one key a safe match reads instead; null when there is none or more than one.</summary>
    internal static string? Safe(string requested, IEnumerable<string> keys)
    {
        List<string> best = Best(requested, keys, SafeRank);
        return best.Count == 1 ? best[0] : null;
    }

    /// <summary>The keys of the closest kind of near match, safe or not, sorted, at most five; empty when none.</summary>
    internal static List<string> Closest(string requested, IEnumerable<string> keys)
    {
        List<string> best = Best(requested, keys, AnyRank);
        if (best.Count > MaximumCandidates)
        {
            best.RemoveRange(MaximumCandidates, best.Count - MaximumCandidates);
        }

        return best;
    }

    private const int NoMatch = int.MaxValue;

    private static List<string> Best(string requested, IEnumerable<string> keys, Func<Words, Words, int> rank)
    {
        Words wanted = Words.Of(requested);
        List<string> best = new List<string>();
        int bestRank = NoMatch;
        foreach (string key in keys)
        {
            if (string.Equals(key, requested, StringComparison.Ordinal))
            {
                continue;
            }

            int ranked = rank(wanted, Words.Of(key));
            if (ranked == NoMatch || ranked > bestRank)
            {
                continue;
            }

            if (ranked < bestRank)
            {
                best.Clear();
                bestRank = ranked;
            }

            best.Add(key);
        }

        best.Sort(StringComparer.Ordinal);
        return best;
    }

    // Lower is closer; NoMatch when the key is not a safe stand-in.
    private static int SafeRank(Words wanted, Words key)
    {
        if (wanted.Joined == key.Joined)
        {
            return 0;
        }

        if (wanted.Sorted == key.Sorted)
        {
            return 1;
        }

        if (wanted.ApartBy(key) is string extra && Qualifiers.Contains(extra) &&
            (extra != "count" || Math.Min(wanted.Count, key.Count) >= 2))
        {
            return 2;
        }

        return wanted.SlipFrom(key) is (string mistyped, string meant) && mistyped.Length >= 4 &&
               meant.Length >= 4 && Singular(mistyped) != Singular(meant) && Edits(mistyped, meant) == 1
            ? 3
            : NoMatch;
    }

    private static int AnyRank(Words wanted, Words key)
    {
        int safe = SafeRank(wanted, key);
        if (safe != NoMatch)
        {
            return safe;
        }

        if (wanted.ApartBy(key) != null || wanted.SingularJoined == key.SingularJoined)
        {
            return 4;
        }

        return wanted.Joined.Length >= 4 && Edits(wanted.Joined, key.Joined) <= 2 ? 5 : NoMatch;
    }

    private static string Singular(string word) =>
        word.Length > 3 && word.EndsWith("s", StringComparison.Ordinal) && !word.EndsWith("ss", StringComparison.Ordinal)
            ? word.Substring(0, word.Length - 1)
            : word;

    // Levenshtein distance with an adjacent swap counted as one edit.
    private static int Edits(string a, string b)
    {
        int[,] d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++)
        {
            d[i, 0] = i;
        }

        for (int j = 0; j <= b.Length; j++)
        {
            d[0, j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                int best = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    best = Math.Min(best, d[i - 2, j - 2] + 1);
                }

                d[i, j] = best;
            }
        }

        return d[a.Length, b.Length];
    }

    /// <summary>A name's words in lower case, split at '_' and where lower case turns upper (displayName).</summary>
    private sealed class Words
    {
        private readonly List<string> _words;
        private readonly List<string> _singular;

        private Words(List<string> words)
        {
            _words = words;
            _singular = new List<string>(words.Count);
            foreach (string word in words)
            {
                _singular.Add(Singular(word));
            }

            Joined = string.Join("_", words);
            SingularJoined = string.Join("_", _singular);
            List<string> sorted = new List<string>(words);
            sorted.Sort(StringComparer.Ordinal);
            Sorted = string.Join("_", sorted);
        }

        internal int Count => _words.Count;

        internal string Joined { get; }

        internal string SingularJoined { get; }

        internal string Sorted { get; }

        internal static Words Of(string name)
        {
            List<string> words = new List<string>();
            StringBuilder word = new StringBuilder();
            for (int index = 0; index < name.Length; index++)
            {
                char character = name[index];
                if (character == '_' || (char.IsUpper(character) && index > 0 && char.IsLower(name[index - 1])))
                {
                    Flush(words, word);
                }

                if (character != '_')
                {
                    word.Append(char.ToLowerInvariant(character));
                }
            }

            Flush(words, word);
            return new Words(words);
        }

        /// <summary>
        /// The one word the longer name has beyond the shorter when every other word matches (singular and plural
        /// alike); null otherwise.
        /// </summary>
        internal string? ApartBy(Words other)
        {
            Words shorter = _words.Count < other._words.Count ? this : other;
            Words longer = ReferenceEquals(shorter, this) ? other : this;
            if (shorter._words.Count == 0 || longer._words.Count != shorter._words.Count + 1)
            {
                return null;
            }

            List<string> left = new List<string>(longer._singular);
            foreach (string word in shorter._singular)
            {
                if (!left.Remove(word))
                {
                    return null;
                }
            }

            return left[0];
        }

        /// <summary>The one word, at the same place, that differs from other's; null when none or several do.</summary>
        internal (string Mistyped, string Meant)? SlipFrom(Words other)
        {
            if (_words.Count != other._words.Count)
            {
                return null;
            }

            (string, string)? slip = null;
            for (int index = 0; index < _words.Count; index++)
            {
                if (_words[index] == other._words[index])
                {
                    continue;
                }

                if (slip != null)
                {
                    return null;
                }

                slip = (_words[index], other._words[index]);
            }

            return slip;
        }

        private static void Flush(List<string> words, StringBuilder word)
        {
            if (word.Length > 0)
            {
                string text = word.ToString();
                words.Add(UnitWords.TryGetValue(text, out string unit) ? unit : text);
                word.Clear();
            }
        }
    }
}
