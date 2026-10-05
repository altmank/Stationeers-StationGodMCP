#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Api.Shared;

/// <summary>Which page of a world-sized list to return: offset and limit, with the tool's default and cap.</summary>
internal sealed class PageRequest
{
    private PageRequest(int offset, int limit, int maximum)
    {
        Offset = offset;
        Limit = limit;
        Maximum = maximum;
    }

    internal int Offset { get; }

    internal int Limit { get; }

    /// <summary>The largest limit the tool takes.</summary>
    internal int Maximum { get; }

    internal static PageRequest From(Args args, int defaultLimit, int maximumLimit) =>
        new PageRequest(
            args.OptionalInt("offset", 0, int.MaxValue) ?? 0,
            args.OptionalInt("limit", 1, maximumLimit) ?? defaultLimit, maximumLimit);

    /// <summary>
    /// Notes the reply's list (its JSON key) as cut when this page holds fewer than the total; advice is added to the
    /// note's way to get more.
    /// </summary>
    internal void Note(string list, int returned, int total, string advice = "") =>
        Pure.Shaping.Truncations.Page(list, Offset, returned, total, Maximum, advice);
}

/// <summary>The order argument of a paged world list: nearest (default) or reference_id.</summary>
internal static class ListOrderArg
{
    internal static Pure.ListOrder From(Args args)
    {
        string? given = args.OptionalString("order");
        if (!Pure.ListOrders.TryParse(given, out Pure.ListOrder order))
        {
            throw ApiErrors.InvalidArgument(
                $"Argument 'order' must be {Pure.ListOrders.Nearest} or {Pure.ListOrders.ReferenceId}; '{given}' is neither.");
        }

        return order;
    }
}

/// <summary>One page of an already sorted list, with the list's total and whether more follows.</summary>
internal sealed class Slice<T>
{
    private Slice(List<T> items, int offset, int limit, int total)
    {
        Items = items;
        Offset = offset;
        Limit = limit;
        Total = total;
        HasMore = offset + items.Count < total;
    }

    internal List<T> Items { get; }

    internal int Offset { get; }

    internal int Limit { get; }

    internal int Total { get; }

    internal bool HasMore { get; }

    /// <summary>A page already cut from a list of the given total, e.g. after its rows were read in full.</summary>
    internal static Slice<T> Page(List<T> items, PageRequest page, int total) =>
        new Slice<T>(items, page.Offset, page.Limit, total);

    internal static Slice<T> Of(List<T> sorted, PageRequest page)
    {
        int start = page.Offset < sorted.Count ? page.Offset : sorted.Count;
        int count = sorted.Count - start < page.Limit ? sorted.Count - start : page.Limit;
        List<T> items = new List<T>(count);
        for (int index = start; index < start + count; index++)
        {
            items.Add(sorted[index]);
        }

        return new Slice<T>(items, page.Offset, page.Limit, sorted.Count);
    }
}
