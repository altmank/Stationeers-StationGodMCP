#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using StationGodMCP.Api.Shared.Game.Upgrades;

namespace StationGodMCP.Api.Shared.Game.Runs;

/// <summary>
/// assume_removed as read for a planner: the things still standing, split into the kind's own pieces (the plan removes
/// them in the same job) and anything else on the small grid (only freed for the search and placement; its own tool
/// must remove it first), and the ids that name nothing standing (already gone, which is what was assumed).
/// </summary>
internal sealed class AssumedRemovals
{
    private AssumedRemovals(List<SmallGrid> pieces, List<SmallGrid> others, List<ThingId> missing)
    {
        Pieces = pieces;
        Others = others;
        Missing = missing;
    }

    internal List<SmallGrid> Pieces { get; }

    internal List<SmallGrid> Others { get; }

    internal List<ThingId> Missing { get; }

    internal bool IsEmpty => Pieces.Count == 0 && Others.Count == 0 && Missing.Count == 0;

    /// <summary>Every standing thing assumed gone.</summary>
    internal HashSet<long> Ids
    {
        get
        {
            HashSet<long> ids = new HashSet<long>();
            Pieces.ForEach(piece => ids.Add(piece.ReferenceId));
            Others.ForEach(thing => ids.Add(thing.ReferenceId));
            return ids;
        }
    }

    internal static AssumedRemovals Read(Args args, RunKind kind)
    {
        List<SmallGrid> pieces = new List<SmallGrid>();
        List<SmallGrid> others = new List<SmallGrid>();
        List<ThingId> missing = new List<ThingId>();
        if (!args.Has("assume_removed"))
        {
            return new AssumedRemovals(pieces, others, missing);
        }

        HashSet<long> seen = new HashSet<long>();
        foreach (ThingId id in args.ThingIds("assume_removed", RunPlanner.MaximumRemovals))
        {
            if (!seen.Add(id.Value))
            {
                continue;
            }

            if (!GameLookup.TryFindThing(id, out Thing thing) || thing.IsBeingDestroyed)
            {
                missing.Add(id);
            }
            else if (!(thing is SmallGrid small))
            {
                throw ApiErrors.InvalidArgument(
                    $"assume_removed: {thing.DisplayName} ({thing.PrefabName} {id}) does not stand on the small grid.");
            }
            else if (kind.Family.IsPiece(small))
            {
                pieces.Add(small);
            }
            else
            {
                others.Add(small);
            }
        }

        return new AssumedRemovals(pieces, others, missing);
    }
}
