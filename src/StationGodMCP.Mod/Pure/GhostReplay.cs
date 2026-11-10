#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace StationGodMCP.Pure;

/// <summary>
/// What became of one group of early ghosts replayed into Lattice: which got which new id, and which were refused
/// (and are gone for good), each with Lattice's reason.
/// </summary>
internal sealed class GhostReplay
{
    private static readonly Regex Indexed = new Regex(@"^(\d+): (.*)$", RegexOptions.Singleline);

    private GhostReplay(List<(long Old, long New)> mapped, List<string> refusedLines)
    {
        Mapped = mapped;
        RefusedLines = refusedLines;
    }

    /// <summary>Each ghost placed: its old id and the id Lattice gave it.</summary>
    internal List<(long Old, long New)> Mapped { get; }

    /// <summary>One line per ghost not placed: old id, prefab, position and Lattice's reason.</summary>
    internal List<string> RefusedLines { get; }

    /// <summary>
    /// Pairs the ghosts sent (in order) with the ids Lattice gave back and the refusals it reported. A refusal reading
    /// "n: reason" names the nth ghost; any other refusal is the whole batch's. With none and nothing placed the answer
    /// never came ("no answer").
    /// </summary>
    internal static GhostReplay Of(IReadOnlyList<BoardGhost> sent, IReadOnlyList<long> placedIds,
        IReadOnlyList<string> refused)
    {
        Dictionary<int, string> byIndex = new Dictionary<int, string>();
        List<string> batch = new List<string>();
        foreach (string refusal in refused)
        {
            Match match = Indexed.Match(refusal);
            if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture,
                    out int index) && index >= 0 && index < sent.Count)
            {
                byIndex[index] = match.Groups[2].Value;
            }
            else
            {
                batch.Add(refusal);
            }
        }

        List<(long Old, long New)> mapped = new List<(long Old, long New)>();
        List<string> lines = new List<string>();
        int next = 0;
        string batchReason = batch.Count > 0 ? string.Join("; ", batch) : "no answer";
        for (int index = 0; index < sent.Count; index++)
        {
            BoardGhost ghost = sent[index];
            if (byIndex.TryGetValue(index, out string? reason))
            {
                lines.Add(Describe(ghost, reason));
            }
            else if (batch.Count == 0 && next < placedIds.Count)
            {
                mapped.Add((ghost.Id, placedIds[next++]));
            }
            else
            {
                lines.Add(Describe(ghost, batch.Count > 0 || placedIds.Count == 0 ? batchReason : "not placed"));
            }
        }

        return new GhostReplay(mapped, lines);
    }

    private static string Describe(BoardGhost ghost, string reason) =>
        string.Format(CultureInfo.InvariantCulture, "{0} {1} at ({2:0.##}, {3:0.##}, {4:0.##}): {5}", ghost.Id,
            ghost.PrefabName, ghost.Position.X, ghost.Position.Y, ghost.Position.Z, reason);
}
