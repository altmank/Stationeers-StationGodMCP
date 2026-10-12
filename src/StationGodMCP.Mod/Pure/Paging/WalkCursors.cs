#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using StationGodMCP.Pure.Protocol;

namespace StationGodMCP.Pure.Paging;

/// <summary>
/// The walk cursors (protocol.md, Walks): a paged find called with cursor true whose first page leaves more keeps the
/// reference ids of the rest of its list, in the handler's own order (nearest stays nearest), under a cursor id for its
/// caller's (owner, stream) (CallContext: the connection, or its resume token). Later pages come from those ids, not from a new scan, so a walk sees each thing that
/// matched at the first call at most once, in order; things built later are not in it.
/// <para>
/// A later page checks existence only: an id whose thing is gone (or going) is skipped and counted, and the page goes
/// on to the next id until it holds limit things or the ids run out. The first call's filter is not checked again: a
/// thing that stopped matching is still walked, with its current values.
/// </para>
/// <para>
/// A cursor is freed when its walk reaches the end (the page that takes the last ids), by cursor_close from a
/// connection of its owner, when its stream ends or its owner's state ends (a closed connection without a resume token,
/// or a resume window that passed), on a world change, and by the bounds: at most MaximumIds ids (8 bytes
/// each, 8 MB for the default) and MaximumCursors cursors held in all, the least recently used evicted first, so a walk
/// still being paged outlives the short walks a loop opens and drops. Using a freed, evicted or unknown cursor, or one
/// another (owner, stream) holds, is cursor_expired; the last ExpiredKept freed cursors remember how many things
/// their walk had returned, which the error gives as position (null when not remembered, and for another owner's
/// cursor, so a stream learns nothing of another's walks).
/// </para>
/// <para>
/// Main thread only, like every per-stream store; a later page allocates nothing here (the dictionary lookup, the id
/// parse and the recency move reuse what the cursor already holds).
/// </para>
/// </summary>
internal sealed class WalkCursors : ICursorStore
{
    internal const string CursorExpiredCode = "cursor_expired";

    /// <summary>Ids held across all cursors: 1,000,000 longs, 8 MB, about 160 walks of a 6,000-thing world.</summary>
    internal const int DefaultMaximumIds = 1000000;

    /// <summary>
    /// Cursors held: a cursor costs about 150 bytes beside its ids, so the count bounds the many small walks that the
    /// id bound would let pile up (a script's loop of finds), each of which holds its stream's attention.
    /// </summary>
    internal const int DefaultMaximumCursors = 1024;

    /// <summary>Freed cursors whose position cursor_expired can still give (12 bytes each).</summary>
    internal const int ExpiredKept = 256;

    internal const string IdPrefix = "k";

    // A cursor number's digits at most: long.MaxValue has 19, and numbers never come near it.
    private const int MaximumDigits = 18;

    private readonly Dictionary<long, Cursor> _open = new Dictionary<long, Cursor>();
    private readonly long[] _expiredNumbers = new long[ExpiredKept];
    private readonly int[] _expiredPositions = new int[ExpiredKept];
    private int _expiredNext;
    private long _lastNumber;
    private Cursor? _leastRecent;
    private Cursor? _mostRecent;

    internal WalkCursors(int maximumIds = DefaultMaximumIds, int maximumCursors = DefaultMaximumCursors)
    {
        MaximumIds = maximumIds > 0 ? maximumIds : throw new ArgumentOutOfRangeException(nameof(maximumIds));
        MaximumCursors = maximumCursors > 0 ? maximumCursors : throw new ArgumentOutOfRangeException(nameof(maximumCursors));
    }

    /// <summary>The mod's cursors, told of ended streams and world changes through StreamStores.Main.</summary>
    internal static WalkCursors Main { get; } = new WalkCursors();

    internal int MaximumIds { get; }

    internal int MaximumCursors { get; }

    /// <summary>Cursors held.</summary>
    internal int Count => _open.Count;

    /// <summary>Ids held by every cursor together.</summary>
    internal long IdsHeld { get; private set; }

    /// <summary>
    /// Keeps the rest of a walk whose first page returned returned things (and passed over skipped gone ones): rest is
    /// the ids after that page, in order, and the store takes the array. Evicts the least recently used cursors until
    /// the new one fits. Null when rest alone is longer than MaximumIds: such a walk is not kept.
    /// </summary>
    internal string? Open(CallContext owner, long[] rest, int returned, int skipped = 0)
    {
        if (owner.IsNone)
        {
            throw new ArgumentException("A cursor belongs to a call's connection and stream.", nameof(owner));
        }

        if (rest.Length > MaximumIds)
        {
            return null;
        }

        while (_leastRecent != null && (_open.Count >= MaximumCursors || IdsHeld + rest.Length > MaximumIds))
        {
            Free(_leastRecent);
        }

        long number = ++_lastNumber;
        Cursor cursor = new Cursor(number, IdPrefix + number.ToString(CultureInfo.InvariantCulture), owner, rest,
            returned, skipped);
        _open.Add(number, cursor);
        IdsHeld += rest.Length;
        Append(cursor);
        return cursor.Id;
    }

    /// <summary>
    /// The next page of the caller's walk: live gives an id's thing, or null when it no longer exists, and every thing
    /// found goes into into until it holds limit, or until budget stops it (at least one thing; the cursor goes on
    /// from there at the next call). False when the cursor is not the caller's to use (cursor_expired);
    /// step then carries only ExpiredPosition. The page that takes the last ids frees the cursor.
    /// </summary>
    internal bool TryNext<T>(CallContext caller, string cursorId, int limit, Func<long, T?> live, List<T> into,
        out WalkStep step, PageBudget budget = default)
        where T : class
    {
        long number = NumberOf(cursorId);
        if (number <= 0 || !_open.TryGetValue(number, out Cursor? cursor) || cursor == null)
        {
            step = WalkStep.Expired(number > 0 ? ExpiredPosition(number) : null);
            return false;
        }

        if (!cursor.Owner.Equals(caller))
        {
            step = WalkStep.Expired(null);
            return false;
        }

        int offset = cursor.Passed;
        long[] ids = cursor.Ids;
        int count = into.Count;
        int index = Take(ids, cursor.Next, limit, live, into, budget, out int skipped);
        int found = into.Count - count;
        cursor.Next = index;
        cursor.Returned += found;
        bool more = index < ids.Length;
        step = new WalkStep(more ? cursor.Id : null, offset, skipped, cursor.Total, more, null);
        if (more)
        {
            Unlink(cursor);
            Append(cursor);
        }
        else
        {
            Free(cursor);
        }

        return true;
    }

    /// <summary>
    /// One page of a walk's ids from index from: live gives an id's thing, or null when it no longer exists (skipped and
    /// counted), and every thing found goes into into until the page holds limit or budget stops it (never before the
    /// first). Returns the index after the last id taken.
    /// </summary>
    internal static int Take<T>(long[] ids, int from, int limit, Func<long, T?> live, List<T> into, PageBudget budget,
        out int skipped)
        where T : class
    {
        skipped = 0;
        int found = 0;
        int index = from;
        while (index < ids.Length && found < limit && budget.Allows(found))
        {
            T? thing = live(ids[index]);
            index++;
            if (thing == null)
            {
                skipped++;
            }
            else
            {
                into.Add(thing);
                found++;
            }
        }

        return index;
    }

    /// <summary>cursor_close: frees the cursor when the owner holds it; anything else does nothing.</summary>
    public void Close(string owner, string cursorId)
    {
        long number = NumberOf(cursorId);
        if (number > 0 && _open.TryGetValue(number, out Cursor? cursor) && cursor != null &&
            string.Equals(cursor.Owner.Owner, owner, StringComparison.Ordinal))
        {
            Free(cursor);
        }
    }

    /// <summary>Frees every cursor of the contexts the end covers.</summary>
    public void Ended(StreamEnd end)
    {
        Cursor? cursor = _leastRecent;
        while (cursor != null)
        {
            Cursor? newer = cursor.Newer;
            if (end.Covers(cursor.Owner))
            {
                Free(cursor);
            }

            cursor = newer;
        }
    }

    /// <summary>A cursor id's number: "k" and 1 to 18 digits; 0 for anything else.</summary>
    private static long NumberOf(string? cursorId)
    {
        if (cursorId == null || cursorId.Length < 2 || cursorId.Length > MaximumDigits + 1 ||
            !cursorId.StartsWith(IdPrefix, StringComparison.Ordinal))
        {
            return 0;
        }

        long number = 0;
        for (int index = 1; index < cursorId.Length; index++)
        {
            char digit = cursorId[index];
            if (digit < '0' || digit > '9')
            {
                return 0;
            }

            number = number * 10 + (digit - '0');
        }

        return number;
    }

    private int? ExpiredPosition(long number)
    {
        for (int index = 0; index < ExpiredKept; index++)
        {
            if (_expiredNumbers[index] == number)
            {
                return _expiredPositions[index];
            }
        }

        return null;
    }

    private void Free(Cursor cursor)
    {
        _open.Remove(cursor.Number);
        IdsHeld -= cursor.Ids.Length;
        Unlink(cursor);
        _expiredNumbers[_expiredNext] = cursor.Number;
        _expiredPositions[_expiredNext] = cursor.Returned;
        _expiredNext = (_expiredNext + 1) % ExpiredKept;
    }

    private void Append(Cursor cursor)
    {
        cursor.Older = _mostRecent;
        cursor.Newer = null;
        if (_mostRecent != null)
        {
            _mostRecent.Newer = cursor;
        }
        else
        {
            _leastRecent = cursor;
        }

        _mostRecent = cursor;
    }

    private void Unlink(Cursor cursor)
    {
        if (cursor.Older != null)
        {
            cursor.Older.Newer = cursor.Newer;
        }
        else
        {
            _leastRecent = cursor.Newer;
        }

        if (cursor.Newer != null)
        {
            cursor.Newer.Older = cursor.Older;
        }
        else
        {
            _mostRecent = cursor.Older;
        }

        cursor.Older = null;
        cursor.Newer = null;
    }

    /// <summary>One walk: its ids after the first page, how far it has got, and its place in the recency list.</summary>
    private sealed class Cursor
    {
        internal Cursor(long number, string id, CallContext owner, long[] ids, int returned, int skipped)
        {
            Number = number;
            Id = id;
            Owner = owner;
            Ids = ids;
            First = returned + skipped;
            Returned = returned;
        }

        internal long Number { get; }

        internal string Id { get; }

        internal CallContext Owner { get; }

        internal long[] Ids { get; }

        /// <summary>Entries the first page passed (returned or skipped), before Ids.</summary>
        internal int First { get; }

        /// <summary>The index in Ids of the next id to take.</summary>
        internal int Next { get; set; }

        /// <summary>Things the walk has returned, the first page's included.</summary>
        internal int Returned { get; set; }

        /// <summary>Entries of the walk passed so far, returned or skipped.</summary>
        internal int Passed => First + Next;

        internal int Total => First + Ids.Length;

        internal Cursor? Older { get; set; }

        internal Cursor? Newer { get; set; }
    }
}

/// <summary>
/// One later page of a walk, or why there is none. Offset: the walk's entries passed before this page (returned or
/// skipped); Skipped: ids on this page whose thing no longer existed; Total: the walk's length at its first call;
/// HasMore and CursorId (null once the walk has ended). ExpiredPosition: for cursor_expired, the things the walk had
/// returned, null when unknown.
/// </summary>
internal readonly struct WalkStep
{
    internal WalkStep(string? cursorId, int offset, int skipped, int total, bool hasMore, int? expiredPosition)
    {
        CursorId = cursorId;
        Offset = offset;
        Skipped = skipped;
        Total = total;
        HasMore = hasMore;
        ExpiredPosition = expiredPosition;
    }

    internal string? CursorId { get; }

    internal int Offset { get; }

    internal int Skipped { get; }

    internal int Total { get; }

    internal bool HasMore { get; }

    internal int? ExpiredPosition { get; }

    internal static WalkStep Expired(int? position) => new WalkStep(null, 0, 0, 0, false, position);
}
