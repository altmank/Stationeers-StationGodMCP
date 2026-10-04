#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// The things a run takes its materials from: the source (from_id's first thing, or the player) and the further things
/// from_id lists, tried in order for each material.
/// </summary>
internal static class PaySources
{
    /// <summary>The further things the ids name, in order; each id naming nothing standing is added to missing.</summary>
    internal static List<Thing> Resolve(IReadOnlyList<ThingId> ids, List<ThingId> missing)
    {
        List<Thing> found = new List<Thing>(ids.Count);
        foreach (ThingId id in ids)
        {
            if (GameLookup.TryFindThing(id, out Thing thing) && thing != null && !thing.IsBeingDestroyed)
            {
                found.Add(thing);
            }
            else
            {
                missing.Add(id);
            }
        }

        return found;
    }

    /// <summary>The stock of the item across the source and the further things; empty without a source.</summary>
    internal static ItemStock StockOf(Thing? from, IReadOnlyList<Thing> more, Item item) =>
        from == null ? ItemStock.Empty(item) : ItemStock.In(All(from, more), item);

    /// <summary>The holders' names for a shortage message: "Locker A", or "Locker A, Locker B and Player".</summary>
    internal static string Holders(Thing from, IReadOnlyList<Thing> more)
    {
        List<string> names = new List<string>(more.Count + 1);
        foreach (Thing thing in All(from, more))
        {
            names.Add(Names.Of(thing));
        }

        return SourceSplit.Joined(names);
    }

    /// <summary>What each source pays for the stock, when more than one source is in play; null otherwise.</summary>
    internal static List<PaidByView>? PaidBy(ItemStock? stock)
    {
        if (stock == null || stock.Sources.Count < 2)
        {
            return null;
        }

        List<int> available = stock.Sources.ConvertAll(static source => source.Available);
        List<int> paid = SourceSplit.Of(available, stock.Needed);
        List<PaidByView> views = new List<PaidByView>(paid.Count);
        for (int index = 0; index < paid.Count; index++)
        {
            Thing source = stock.Sources[index].Thing;
            views.Add(new PaidByView(new ThingId(source.ReferenceId), Names.Of(source), paid[index],
                stock.Sources[index].Available));
        }

        return views;
    }

    private static List<Thing> All(Thing from, IReadOnlyList<Thing> more)
    {
        List<Thing> all = new List<Thing>(more.Count + 1) { from };
        foreach (Thing thing in more)
        {
            if (thing.ReferenceId != from.ReferenceId && !all.Contains(thing))
            {
                all.Add(thing);
            }
        }

        return all;
    }
}
