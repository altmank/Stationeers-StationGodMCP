#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// The one paging form (PLAN 2.0, shared forms; D3): limit is the page size (left out, the default of the form the
/// call takes, or every entry), offset the entries to skip, order nearest (the default) or id (stable pages:
/// ListOrder.ReferenceId), and cursor a walk (SPEC-NOTES 11), a wire option the hub and sg send, never an agent:
/// "open" on a first page keeps the rest of the list under a cursor when more follows (the reply's cursor_id), and
/// that cursor id on a later call takes the walk's next page.
/// </summary>
internal sealed record Paging(int? Limit, int Offset, ListOrder Order, string? Cursor = null)
{
    /// <summary>The wire cursor's word on a first page: keep the rest under a cursor when more follows.</summary>
    internal const string OpenCursor = "open";

    internal static Paging All { get; } = new Paging(null, 0, ListOrder.Nearest);

    /// <summary>A first page that keeps the rest of its list under a cursor when more follows.</summary>
    internal bool OpensCursor => string.Equals(Cursor, OpenCursor, StringComparison.Ordinal);

    /// <summary>The walk a later page goes on with; null on a first page.</summary>
    internal string? CursorId => Cursor != null && !OpensCursor ? Cursor : null;

    internal static Paging Bind(OptionReader options)
    {
        long? limit = options.Integer("limit") ?? options.DefaultInteger("limit");
        long offset = options.Integer("offset") ?? 0;
        return new Paging(limit is { } most ? (int)Math.Min(most, int.MaxValue) : null,
            (int)Math.Min(offset, int.MaxValue), OrderOf(options.Text("order")), CursorOf(options.Text("cursor")));
    }

    /// <summary>The page of entries, in the order given: offset skipped, at most limit kept.</summary>
    internal Page<T> Of<T>(IReadOnlyList<T> entries)
    {
        int start = Math.Min(Offset, entries.Count);
        int count = Math.Min(Limit ?? int.MaxValue, entries.Count - start);
        List<T> shown = new List<T>(count);
        for (int index = start; index < start + count; index++)
        {
            shown.Add(entries[index]);
        }

        return new Page<T>(shown, entries.Count, Offset, Limit, start + count < entries.Count);
    }

    private static string? CursorOf(string? given) => string.IsNullOrWhiteSpace(given) ? null : given!.Trim();

    // The verb words: nearest, or id for the stable reference id order; absent is nearest.
    private static ListOrder OrderOf(string? word) =>
        string.Equals(word?.Trim(), "id", StringComparison.OrdinalIgnoreCase) ? ListOrder.ReferenceId : ListOrder.Nearest;
}

/// <summary>One page of a list: its entries, the list's total, where it starts, the page size, and whether more follow.</summary>
internal sealed record Page<T>(IReadOnlyList<T> Entries, int Total, int Offset, int? Limit, bool HasMore);
