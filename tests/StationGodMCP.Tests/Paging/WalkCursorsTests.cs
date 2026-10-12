#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.Pure.Paging;
using StationGodMCP.Pure.Protocol;
using Xunit;

namespace StationGodMCP.Tests.Paging;

/// <summary>
/// The walk cursors' store (Pure/Paging/WalkCursors): a walk's ordered ids kept per (connection, stream) under a cursor
/// id, pages taken from them with removed things skipped, and every way a cursor is freed (its end, cursor_close, its
/// stream's or connection's end, a world change, the memory bounds), each answered cursor_expired with its position.
/// </summary>
public sealed class WalkCursorsTests
{
    private static readonly CallContext Hub = new CallContext("c1", 3);
    private static readonly CallContext OtherStream = new CallContext("c1", 4);
    private static readonly CallContext OtherConnection = new CallContext("c2", 3);

    [Fact]
    public void PagesComeFromTheKeptIdsInOrderAndTheLastPageFreesTheCursor()
    {
        WalkCursors cursors = new WalkCursors();
        string id = cursors.Open(Hub, Ids(3, 4, 5, 6, 7), 2)!;

        WalkStep second = Next(cursors, Hub, id, 2, out List<string> page2);
        WalkStep last = Next(cursors, Hub, id, 2, out List<string> page3);

        Assert.Equal(new[] { "3", "4" }, page2);
        Assert.Equal(id, second.CursorId);
        Assert.Equal(2, second.Offset);
        Assert.Equal(7, second.Total);
        Assert.True(second.HasMore);
        Assert.Equal(new[] { "5", "6" }, page3);
        Assert.True(last.HasMore);
        WalkStep end = Next(cursors, Hub, id, 5, out List<string> page4);
        Assert.Equal(new[] { "7" }, page4);
        Assert.False(end.HasMore);
        Assert.Null(end.CursorId);
        Assert.Equal(0, cursors.Count);
        Assert.Equal(0, cursors.IdsHeld);
    }

    [Fact]
    public void CursorIdsAreShortAndNeverReused()
    {
        WalkCursors cursors = new WalkCursors();
        string first = cursors.Open(Hub, Ids(1), 1)!;
        cursors.Close("c1", first);
        string second = cursors.Open(Hub, Ids(1), 1)!;

        Assert.StartsWith("k", first);
        Assert.True(first.Length <= 8);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ThingsThatNoLongerExistAreSkippedAndCountedAndThePageStillFillsUp()
    {
        WalkCursors cursors = new WalkCursors();
        string id = cursors.Open(Hub, Ids(10, 11, 12, 13, 14, 15), 1)!;
        HashSet<long> gone = new HashSet<long> { 10, 12 };

        List<string> page = new List<string>();
        Assert.True(cursors.TryNext(Hub, id, 3, thing => gone.Contains(thing) ? null : thing.ToString(), page,
            out WalkStep step));

        Assert.Equal(new[] { "11", "13", "14" }, page);
        Assert.Equal(2, step.Skipped);
        Assert.Equal(1, step.Offset);
        Assert.True(step.HasMore);
        WalkStep last = Next(cursors, Hub, id, 3, out List<string> rest);
        Assert.Equal(new[] { "15" }, rest);
        Assert.Equal(0, last.Skipped);
        Assert.Equal(6, last.Offset);
        Assert.False(last.HasMore);
    }

    [Fact]
    public void AWalkWhoseRemainingThingsAreAllGoneEndsWithAnEmptyPage()
    {
        WalkCursors cursors = new WalkCursors();
        string id = cursors.Open(Hub, Ids(1, 2), 4)!;

        List<string> page = new List<string>();
        Assert.True(cursors.TryNext<string>(Hub, id, 8, _ => null, page, out WalkStep step));

        Assert.Empty(page);
        Assert.Equal(2, step.Skipped);
        Assert.False(step.HasMore);
        Assert.Equal(0, cursors.Count);
    }

    [Fact]
    public void AFreedCursorAnswersExpiredWithHowManyEntriesItHadReturned()
    {
        WalkCursors cursors = new WalkCursors();
        string id = cursors.Open(Hub, Ids(1, 2, 3), 8)!;
        Next(cursors, Hub, id, 1, out _);
        Next(cursors, Hub, id, 5, out _);

        Assert.False(cursors.TryNext(Hub, id, 5, Live, new List<string>(), out WalkStep expired));
        Assert.Equal(11, expired.ExpiredPosition);
    }

    [Fact]
    public void AnUnknownOrMalformedCursorIdIsExpiredWithNoPosition()
    {
        WalkCursors cursors = new WalkCursors();

        foreach (string id in new[] { "k99", "k", "x1", "", "k-1", "k99999999999999999999" })
        {
            Assert.False(cursors.TryNext(Hub, id, 5, Live, new List<string>(), out WalkStep step));
            Assert.Null(step.ExpiredPosition);
        }
    }

    [Fact]
    public void CursorCloseFreesItForItsOwnConnectionOnlyAndAnUnknownIdDoesNothing()
    {
        WalkCursors cursors = new WalkCursors();
        string id = cursors.Open(Hub, Ids(1, 2), 3)!;

        cursors.Close("c2", id);
        cursors.Close("c1", "k12345");
        Assert.Equal(1, cursors.Count);

        cursors.Close("c1", id);
        Assert.Equal(0, cursors.Count);
        Assert.False(cursors.TryNext(Hub, id, 5, Live, new List<string>(), out WalkStep step));
        Assert.Equal(3, step.ExpiredPosition);
    }

    [Fact]
    public void ACursorUsedFromAnotherStreamOrConnectionIsExpiredWithNoPositionAndStaysWithItsOwner()
    {
        WalkCursors cursors = new WalkCursors();
        string id = cursors.Open(Hub, Ids(1, 2), 3)!;

        Assert.False(cursors.TryNext(OtherStream, id, 5, Live, new List<string>(), out WalkStep fromStream));
        Assert.False(cursors.TryNext(OtherConnection, id, 5, Live, new List<string>(), out WalkStep fromConnection));

        Assert.Null(fromStream.ExpiredPosition);
        Assert.Null(fromConnection.ExpiredPosition);
        Next(cursors, Hub, id, 5, out List<string> page);
        Assert.Equal(new[] { "1", "2" }, page);
    }

    [Fact]
    public void AStreamsEndFreesOnlyThatStreamsCursors()
    {
        WalkCursors cursors = new WalkCursors();
        string mine = cursors.Open(Hub, Ids(1, 2), 1)!;
        string beside = cursors.Open(OtherStream, Ids(1, 2), 1)!;
        string elsewhere = cursors.Open(OtherConnection, Ids(1, 2), 1)!;

        cursors.Ended(StreamEnd.Of("c1", 3));

        Assert.False(cursors.TryNext(Hub, mine, 5, Live, new List<string>(), out WalkStep step));
        Assert.Equal(1, step.ExpiredPosition);
        Assert.True(cursors.TryNext(OtherStream, beside, 1, Live, new List<string>(), out _));
        Assert.True(cursors.TryNext(OtherConnection, elsewhere, 1, Live, new List<string>(), out _));
    }

    [Fact]
    public void AConnectionsEndFreesEveryStreamOfItAndNoOther()
    {
        WalkCursors cursors = new WalkCursors();
        cursors.Open(Hub, Ids(1, 2), 1);
        cursors.Open(OtherStream, Ids(1, 2), 1);
        string elsewhere = cursors.Open(OtherConnection, Ids(1, 2), 1)!;

        cursors.Ended(StreamEnd.AllOf("c1"));

        Assert.Equal(1, cursors.Count);
        Assert.Equal(2, cursors.IdsHeld);
        Assert.True(cursors.TryNext(OtherConnection, elsewhere, 1, Live, new List<string>(), out _));
    }

    [Fact]
    public void AWorldChangeFreesEveryCursor()
    {
        WalkCursors cursors = new WalkCursors();
        string id = cursors.Open(Hub, Ids(1, 2), 5)!;
        cursors.Open(OtherConnection, Ids(1, 2), 1);

        cursors.Ended(StreamEnd.World);

        Assert.Equal(0, cursors.Count);
        Assert.Equal(0, cursors.IdsHeld);
        Assert.False(cursors.TryNext(Hub, id, 5, Live, new List<string>(), out WalkStep step));
        Assert.Equal(5, step.ExpiredPosition);
    }

    [Fact]
    public void TheIdBoundEvictsTheLeastRecentlyUsedCursorsFirst()
    {
        WalkCursors cursors = new WalkCursors(maximumIds: 10, maximumCursors: 100);
        string first = cursors.Open(Hub, Ids(1, 2, 3, 4), 1)!;
        string second = cursors.Open(Hub, Ids(1, 2, 3, 4), 1)!;
        Next(cursors, Hub, first, 1, out _);

        string third = cursors.Open(Hub, Ids(1, 2, 3, 4), 1)!;

        Assert.Equal(8, cursors.IdsHeld);
        Assert.False(cursors.TryNext(Hub, second, 1, Live, new List<string>(), out WalkStep evicted));
        Assert.Equal(1, evicted.ExpiredPosition);
        Assert.True(cursors.TryNext(Hub, first, 1, Live, new List<string>(), out _));
        Assert.True(cursors.TryNext(Hub, third, 1, Live, new List<string>(), out _));
    }

    [Fact]
    public void TheCursorCountBoundEvictsTheOldestFirst()
    {
        WalkCursors cursors = new WalkCursors(maximumIds: 1000, maximumCursors: 3);
        List<string> ids = new List<string>();
        for (int index = 0; index < 5; index++)
        {
            ids.Add(cursors.Open(Hub, Ids(1, 2), index)!);
        }

        Assert.Equal(3, cursors.Count);
        Assert.False(cursors.TryNext(Hub, ids[0], 1, Live, new List<string>(), out WalkStep oldest));
        Assert.Equal(0, oldest.ExpiredPosition);
        Assert.False(cursors.TryNext(Hub, ids[1], 1, Live, new List<string>(), out WalkStep next));
        Assert.Equal(1, next.ExpiredPosition);
        Assert.True(cursors.TryNext(Hub, ids[2], 1, Live, new List<string>(), out _));
    }

    [Fact]
    public void AWalkLongerThanTheWholeBoundIsNotKept()
    {
        WalkCursors cursors = new WalkCursors(maximumIds: 4, maximumCursors: 10);
        string kept = cursors.Open(Hub, Ids(1, 2), 1)!;

        Assert.Null(cursors.Open(Hub, Ids(1, 2, 3, 4, 5), 1));
        Assert.Equal(1, cursors.Count);
        Assert.True(cursors.TryNext(Hub, kept, 1, Live, new List<string>(), out _));
    }

    [Fact]
    public void ExpiredPositionsAreRememberedForTheLastFreedCursorsOnly()
    {
        WalkCursors cursors = new WalkCursors();
        string first = cursors.Open(Hub, Ids(1), 7)!;
        cursors.Close("c1", first);
        for (int index = 0; index < WalkCursors.ExpiredKept; index++)
        {
            cursors.Close("c1", cursors.Open(Hub, Ids(1), 1)!);
        }

        Assert.False(cursors.TryNext(Hub, first, 1, Live, new List<string>(), out WalkStep forgotten));
        Assert.Null(forgotten.ExpiredPosition);
    }

    [Fact]
    public void ACursorNeedsAnOwner()
    {
        WalkCursors cursors = new WalkCursors();

        Assert.Throws<ArgumentException>(() => cursors.Open(CallContext.None, Ids(1), 1));
    }

    [Fact]
    public void ALaterPageAllocatesNothingInTheStore()
    {
        WalkCursors cursors = new WalkCursors();
        long[] ids = new long[6000];
        for (int index = 0; index < ids.Length; index++)
        {
            ids[index] = index + 1;
        }

        string id = cursors.Open(Hub, ids, 0)!;
        List<object> page = new List<object>(500);
        Func<long, object?> live = static thing => thing % 7 == 0 ? null : Boxed;
        cursors.TryNext(Hub, id, 500, live, page, out _);
        page.Clear();

        long before = GC.GetAllocatedBytesForCurrentThread();
        cursors.TryNext(Hub, id, 500, live, page, out WalkStep step);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(step.HasMore);
        Assert.Equal(500, page.Count);
        Assert.Equal(0, allocated);
    }

    private static readonly object Boxed = new object();

    private static string? Live(long id) => id.ToString();

    private static long[] Ids(params long[] ids) => ids;

    private static WalkStep Next(WalkCursors cursors, CallContext caller, string id, int limit, out List<string> page)
    {
        page = new List<string>();
        Assert.True(cursors.TryNext(caller, id, limit, Live, page, out WalkStep step));
        return step;
    }
}
