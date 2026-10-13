#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// What a swap or remove job names, read against the world: the ids (one job for a list, each id that cannot be read a
/// problem at its index) and the holders materials names.
/// </summary>
internal static class JobTargets
{
    /// <summary>
    /// The ids in the order given, null where one cannot be read; each such id is a problem at its index.
    /// </summary>
    internal static List<ThingId?> Ids(IReadOnlyList<string> given, string label, List<ReportProblem> problems)
    {
        List<ThingId?> ids = new List<ThingId?>(given.Count);
        for (int index = 0; index < given.Count; index++)
        {
            try
            {
                ids.Add(TargetIds.Of(given[index], label));
            }
            catch (ApiException refused)
            {
                ids.Add(null);
                problems.Add(new ReportProblem(index, null, VerbError.Of(refused)));
            }
        }

        return ids;
    }

    /// <summary>The ids that could be read, in order.</summary>
    internal static List<ThingId> Read(List<ThingId?> ids)
    {
        List<ThingId> read = new List<ThingId>(ids.Count);
        foreach (ThingId? id in ids)
        {
            if (id is { } known)
            {
                read.Add(known);
            }
        }

        return read;
    }

    /// <summary>
    /// The holders that pay, as the planners take them: the first (null when the player alone pays, as when materials
    /// is left out) and the rest; "player" anywhere else is the player's body.
    /// </summary>
    internal static ThingId? Payers(MaterialsSource materials, string label, List<ThingId> more)
    {
        if (materials.Payers.Count == 0 || (materials.Payers.Count == 1 && materials.Payers[0] is Payer.Player))
        {
            return null;
        }

        List<ThingId> all = new List<ThingId>(materials.Payers.Count);
        foreach (Payer payer in materials.Payers)
        {
            ThingId id = payer is Payer.Holder holder
                ? TargetIds.Of(holder.Id, label)
                : new ThingId(PlayerOrigin.RequireHuman().ReferenceId);
            if (!all.Contains(id))
            {
                all.Add(id);
            }
        }

        if (all.Count == 0)
        {
            return null;
        }

        more.AddRange(all.GetRange(1, all.Count - 1));
        return all[0];
    }
}
