#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using StationGodMCP.Pure.Paging;
using StationGodMCP.Pure.Protocol;

namespace StationGodMCP.Api.Shared;

/// <summary>
/// The walk form of a paged find (protocol.md, Walks), for a handler that sorts its whole list before it pages it.
/// cursor true on a first call: the handler runs as without it (scan, filter, sort, the first limit entries), and when
/// more follow, the rest of the list's ids stay in WalkCursors under the caller's (connection, stream) and the reply
/// carries cursor_id; offset does not go with cursor. A later call passes cursor_id and, if it likes, limit, and
/// nothing else (a walk keeps its first call's filter and order, so another filter would be ignored: it is refused
/// instead): the page comes from the kept ids with no scan, skipping things that no longer exist and checking nothing
/// else, so a thing that stopped matching is still walked, with its current values. A cursor that is gone answers
/// cursor_expired, data {cursor_id, position}.
/// <para>
/// The handler asks Continues first; on a later page it calls Next and describes what it gets as its first page does,
/// otherwise it scans, calls Keep with its sorted list and the page's size, and both call Note instead of
/// PageRequest.Note.
/// </para>
/// </summary>
internal sealed class WalkRequest
{
    internal const string CursorArgument = "cursor";
    internal const string CursorIdArgument = "cursor_id";

    private readonly WalkCursors _cursors;

    private WalkRequest(bool opens, string? cursorId, WalkCursors cursors)
    {
        Opens = opens;
        CursorId = cursorId;
        _cursors = cursors;
    }

    /// <summary>cursor true: a first call that keeps a cursor when more follows.</summary>
    internal bool Opens { get; }

    /// <summary>A later page's cursor_id; null on a first call.</summary>
    internal string? CursorId { get; }

    internal bool Continues => CursorId != null;

    /// <summary>
    /// cursor and cursor_id from args, refusing what does not go with them; cursors is the store (WalkCursors.Main when
    /// null). A cursor needs a call's context, which every protocol call has.
    /// </summary>
    internal static WalkRequest From(Args args, WalkCursors? cursors = null)
    {
        string? cursorId = args.OptionalString(CursorIdArgument);
        bool opens = args.OptionalBool(CursorArgument) ?? false;
        if (cursorId != null)
        {
            args.RejectAllBut("{arg:cursor_id}", CursorIdArgument, "limit");
        }
        else if (opens)
        {
            args.Reject("{arg:cursor}", "offset");
            if (CallContext.Current.IsNone)
            {
                throw ApiErrors.InvalidArgument(
                    Vocabulary.Text("Argument '{arg:cursor}' needs a call on a protocol 2 connection, whose stream " +
                        "owns the cursor."));
            }
        }

        return new WalkRequest(opens, cursorId, cursors ?? WalkCursors.Main);
    }

    /// <summary>
    /// After the first page: when the call asked for a cursor and sorted holds more than the returned entries, keeps
    /// the ids of the rest and returns the cursor id; null otherwise. A rest longer than the store holds in all is
    /// invalid_argument.
    /// </summary>
    internal string? Keep<T>(List<T> sorted, int returned, Func<T, long> idOf)
    {
        if (!Opens || returned >= sorted.Count)
        {
            return null;
        }

        long[] rest = new long[sorted.Count - returned];
        for (int index = 0; index < rest.Length; index++)
        {
            rest[index] = idOf(sorted[returned + index]);
        }

        return _cursors.Open(CallContext.Current, rest, returned) ?? throw TooLong(rest);
    }

    private ApiException TooLong(long[] rest) => ApiErrors.InvalidArgument(
        $"The walk has {rest.Length.ToString(CultureInfo.InvariantCulture)} more entries than a cursor holds " +
        $"({_cursors.MaximumIds.ToString(CultureInfo.InvariantCulture)}); narrow the filter, or page with " +
        $"{Vocabulary.Arg("offset")}.");

    /// <summary>
    /// After the first page of a walk whose ordered ids were built ahead (WalkBuild): when the call asked for a cursor
    /// and ids remain after next (the page returned returned things and passed over skipped gone ones), keeps them in
    /// WalkCursors under the caller's (connection, stream) and returns the cursor id; null otherwise. A rest longer
    /// than the store holds in all is invalid_argument.
    /// </summary>
    internal string? KeepRest(long[] ids, int next, int returned, int skipped)
    {
        if (!Opens || next >= ids.Length)
        {
            return null;
        }

        long[] rest = new long[ids.Length - next];
        Array.Copy(ids, next, rest, 0, rest.Length);
        return _cursors.Open(CallContext.Current, rest, returned, skipped) ?? throw TooLong(rest);
    }

    /// <summary>
    /// A later page: up to limit things that still exist (live gives null for a gone one), in the walk's order, into
    /// into, fewer when budget stops it (at least one; the rest stays for the next call to Next, so a page may be
    /// taken over frames). A cursor that is not the caller's to use is cursor_expired.
    /// </summary>
    internal WalkStep Next<T>(int limit, Func<long, T?> live, List<T> into, PageBudget budget = default)
        where T : class
    {
        string cursorId = CursorId ?? throw new InvalidOperationException(Vocabulary.Text("Next is for a call with " +
            "{arg:cursor_id}."));
        if (!_cursors.TryNext(CallContext.Current, cursorId, limit, live, into, out WalkStep step, budget))
        {
            throw Expired(cursorId, step.ExpiredPosition);
        }

        return step;
    }

    /// <summary>
    /// The page's truncation note: with a cursor kept, the way on is that cursor_id; a first call without one notes as
    /// PageRequest.Note does; a walk's last page notes nothing.
    /// </summary>
    internal void Note(string list, PageRequest page, int returned, int total, string? cursorId, string advice = "")
    {
        if (cursorId != null)
        {
            Pure.Shaping.Truncations.Note(list, returned, total,
                Vocabulary.Text("pass {arg:cursor_id} ") + cursorId + Vocabulary.Text(" for the next page " +
                    "({arg:limit} up to ") +
                page.Maximum.ToString(CultureInfo.InvariantCulture) + ")");
        }
        else if (!Continues)
        {
            page.Note(list, returned, total, advice);
        }
    }

    /// <summary>cursor_expired: the cursor id and the things its walk had returned (null when not known).</summary>
    internal static ApiException Expired(string cursorId, int? position) =>
        new ApiException(WalkCursors.CursorExpiredCode,
            $"Cursor {cursorId} is gone: its walk ended, it was closed, its stream ended, the world changed, it was " +
            "evicted to bound memory, or another stream owns it. Start the walk again with " +
            $"{Vocabulary.Arg("cursor")} true.",
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
