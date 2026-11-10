#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// What a ghost removal says back. An id is "named no ghost" only when it was not on the board before the removal; a
/// ghost that is there and was not taken off was refused (Lattice's reason) or is still waiting for Lattice's answer.
/// </summary>
internal static class GhostRemoveReport
{
    /// <summary>Takes the ids off: the ids gone, and Lattice's refusals (empty with no Lattice).</summary>
    internal delegate List<long> Remover(IReadOnlyList<long> ids, List<string> refused);

    /// <summary>Removes the ids that are on the board and returns those gone, with the notes for the reply.</summary>
    internal static List<long> Run(IEnumerable<long> ids, Func<long, bool> onBoard, Remover remove, List<string> notes)
    {
        List<long> present = new List<long>();
        HashSet<long> seen = new HashSet<long>();
        int absent = 0;
        foreach (long id in ids)
        {
            if (!seen.Add(id))
            {
                continue;
            }

            if (onBoard(id))
            {
                present.Add(id);
            }
            else
            {
                absent++;
            }
        }

        List<string> refused = new List<string>();
        List<long> gone = remove(present, refused);
        notes.AddRange(Notes(absent, present.Count, gone.Count, refused));
        return gone;
    }

    /// <summary>The notes for a removal: absent ids, each refusal, and the removals sent but not yet answered.</summary>
    internal static List<string> Notes(int absent, int present, int gone, IReadOnlyList<string> refused)
    {
        List<string> notes = new List<string>();
        if (absent > 0)
        {
            notes.Add($"{absent} of the ids named no ghost on the board.");
        }

        foreach (string refusal in refused)
        {
            notes.Add(refusal);
        }

        // Lattice answers a removal as one command, so a refusal covers every id sent in it: nothing is left waiting.
        int waiting = refused.Count == 0 ? present - gone : 0;
        if (waiting == 1)
        {
            notes.Add("1 ghost removal was sent to Lattice and is not yet answered.");
        }
        else if (waiting > 1)
        {
            notes.Add($"{waiting} ghost removals were sent to Lattice and are not yet answered.");
        }

        return notes;
    }
}
