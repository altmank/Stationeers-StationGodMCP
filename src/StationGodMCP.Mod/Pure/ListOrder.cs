#nullable enable

using System;

namespace StationGodMCP.Pure;

/// <summary>
/// The order a paged world list comes in. Nearest follows the player: distances are measured again on every call,
/// so a player who moves between two calls reorders the list, and paging it with offset sees some entries twice and
/// skips others. ReferenceId depends on nothing that moves, so its pages fit together while the set itself holds.
/// </summary>
internal enum ListOrder
{
    Nearest,
    ReferenceId,
}

/// <summary>The order argument's words, and what a cut list says about paging it.</summary>
internal static class ListOrders
{
    internal const string Nearest = "nearest";
    internal const string ReferenceId = "reference_id";

    /// <summary>nearest or reference_id, any case and trimmed; absent is nearest.</summary>
    internal static bool TryParse(string? text, out ListOrder order)
    {
        string word = (text ?? Nearest).Trim();
        if (string.Equals(word, ReferenceId, StringComparison.OrdinalIgnoreCase))
        {
            order = ListOrder.ReferenceId;
            return true;
        }

        order = ListOrder.Nearest;
        return string.Equals(word, Nearest, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The paging advice a cut list adds to its truncation note: only a list measured from a player in nearest order
    /// can shift between pages.
    /// </summary>
    internal static string PagingAdvice(ListOrder order, bool measuredFromPlayer) =>
        order == ListOrder.Nearest && measuredFromPlayer
            ? "; nearest-first pages shift when the player moves: page with order " + ReferenceId
            : string.Empty;
}

/// <summary>
/// One row of a paged world list as the order sees it: distance from the player (null without one), a name, a rank
/// that tells apart rows sharing a name and an id (find_items: an item before a machine's stock), and a reference id.
/// </summary>
internal readonly struct ListKey
{
    internal ListKey(double? distance, string? name, int rank, long id)
    {
        Distance = distance;
        Name = name;
        Rank = rank;
        Id = id;
    }

    internal double? Distance { get; }

    internal string? Name { get; }

    internal int Rank { get; }

    internal long Id { get; }

    /// <summary>
    /// Nearest: nearest first (no distance last), then name, rank, id. ReferenceId: lowest id first, then name, rank;
    /// distance plays no part.
    /// </summary>
    internal static int Compare(ListOrder order, ListKey a, ListKey b)
    {
        switch (order)
        {
            case ListOrder.Nearest:
                int byDistance = (a.Distance ?? double.MaxValue).CompareTo(b.Distance ?? double.MaxValue);
                return byDistance != 0 ? byDistance : ByName(a, b, true);
            case ListOrder.ReferenceId:
                int byId = a.Id.CompareTo(b.Id);
                return byId != 0 ? byId : ByName(a, b, false);
            default:
                throw new ArgumentOutOfRangeException(nameof(order), order, "An order without a comparison.");
        }
    }

    private static int ByName(ListKey a, ListKey b, bool thenId)
    {
        int byName = string.CompareOrdinal(a.Name, b.Name);
        if (byName != 0)
        {
            return byName;
        }

        int byRank = a.Rank.CompareTo(b.Rank);
        return byRank != 0 || !thenId ? byRank : a.Id.CompareTo(b.Id);
    }
}
