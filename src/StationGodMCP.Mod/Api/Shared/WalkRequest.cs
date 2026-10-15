#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using StationGodMCP.Pure.Paging;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Api.Shared;

/// <summary>
/// A walk over a paged list of things (SPEC-NOTES 11; protocol.md, Walks), from the call's wire cursor (Paging): "open"
/// on a first page runs as without it (scan, filter, sort, the page at its offset), and when more follows the rest of
/// the list's ids stay in WalkCursors under the caller's (connection, stream) and the reply carries cursor_id. A later
/// call passes that id: its page comes from the kept ids with no scan, skipping things that no longer exist and
/// checking nothing else, so a thing that stopped matching is still walked, with its current values; the first call's
/// filters and order hold, as do the counts its first page answered (kept), and the later call's other options are not
/// read. A cursor that is gone answers
/// cursor_expired, data {cursor_id, position}.
/// </summary>
internal sealed class WalkRequest
{
    private readonly WalkCursors _cursors;

    private WalkRequest(bool opens, string? cursorId, WalkCursors cursors)
    {
        Opens = opens;
        CursorId = cursorId;
        _cursors = cursors;
    }

    /// <summary>No walk: a first page that keeps nothing (a reply that shows a list's head only).</summary>
    internal static WalkRequest None { get; } = new WalkRequest(false, null, WalkCursors.Main);

    /// <summary>A first page that keeps a cursor when more follows.</summary>
    internal bool Opens { get; }

    /// <summary>A later page's cursor id; null on a first page.</summary>
    internal string? CursorId { get; }

    internal bool Continues => CursorId != null;

    /// <summary>
    /// The walk a call's paging asks for; cursors is the store (WalkCursors.Main when null). A cursor belongs to a
    /// call's stream, which every call on a connection has.
    /// </summary>
    internal static WalkRequest Of(Paging paging, WalkCursors? cursors = null)
    {
        if (paging.OpensCursor && CallContext.Current.IsNone)
        {
            throw ApiErrors.InvalidArgument("A cursor belongs to a call's stream; this call came on none.");
        }

        return new WalkRequest(paging.OpensCursor, paging.CursorId, cursors ?? WalkCursors.Main);
    }

    /// <summary>
    /// After the first page of a walk whose ordered ids were built ahead (WalkBuild): when the call opens a cursor and
    /// ids remain after next (the walk passed returned things up to the page's end, offset included, and the page passed
    /// over skipped gone ones), keeps them in
    /// WalkCursors under the caller's (connection, stream), with what the first page counted (kept), and returns the
    /// cursor id; null otherwise. A rest longer than the store holds in all is invalid_argument.
    /// </summary>
    internal string? KeepRest(long[] ids, int next, int returned, int skipped, object? kept = null)
    {
        if (!Opens || next >= ids.Length)
        {
            return null;
        }

        long[] rest = new long[ids.Length - next];
        Array.Copy(ids, next, rest, 0, rest.Length);
        return _cursors.Open(CallContext.Current, rest, returned, skipped, kept) ??
               throw ApiErrors.InvalidArgument(
                   $"The walk has {rest.Length.ToString(CultureInfo.InvariantCulture)} more entries than a cursor " +
                   $"holds ({_cursors.MaximumIds.ToString(CultureInfo.InvariantCulture)}); narrow the filters, or " +
                   "page with offset.");
    }

    /// <summary>
    /// A later page: up to limit things that still exist (live gives null for a gone one), in the walk's order, into
    /// into, fewer when budget stops it (at least one; the rest stays for the next call to Next, so a page may be
    /// taken over frames). A cursor that is not the caller's to use is cursor_expired.
    /// </summary>
    internal WalkStep Next<T>(int limit, Func<long, T?> live, List<T> into, PageBudget budget = default)
        where T : class
    {
        string cursorId = CursorId ?? throw new InvalidOperationException("Next is for a later page, which names its cursor.");
        if (!_cursors.TryNext(CallContext.Current, cursorId, limit, live, into, out WalkStep step, budget))
        {
            throw Expired(cursorId, step.ExpiredPosition);
        }

        return step;
    }

    /// <summary>
    /// A later page: what the walk's first page kept with its cursor (null when it kept nothing or the cursor is not the
    /// caller's; Next then answers cursor_expired).
    /// </summary>
    internal object? Kept() =>
        CursorId is { } cursorId ? _cursors.KeptOf(CallContext.Current, cursorId) : null;

    /// <summary>cursor_expired: the cursor id and the things its walk had returned (null when not known).</summary>
    internal static ApiException Expired(string cursorId, int? position) =>
        new ApiException(WalkCursors.CursorExpiredCode,
            $"Cursor {cursorId} is gone: its walk ended, it was closed, its stream ended, the world changed, it was " +
            "evicted to bound memory, or another stream owns it. Start the walk again.",
            new ExpiredCursorView(cursorId, position));
}

/// <summary>cursor_expired's data: the cursor id and how many things its walk had returned, null when not known.</summary>
internal sealed class ExpiredCursorView
{
    internal ExpiredCursorView(string cursorId, int? position)
    {
        CursorId = cursorId;
        Position = position;
    }

    public string CursorId { get; }

    public int? Position { get; }
}
