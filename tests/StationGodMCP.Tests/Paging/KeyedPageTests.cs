#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Paging;
using StationGodMCP.Pure.Protocol;
using Xunit;
using VerbPaging = StationGodMCP.Pure.Verbs.Paging;

namespace StationGodMCP.Tests.Paging;

/// <summary>
/// One thing's list in the one paging model (Api/Shared/KeyedPage): the first page describes only its own entries and
/// keeps the rest's keys under a cursor; later pages describe the kept keys as they are then, passing over gone ones;
/// a call that names several things opens no cursor; and a later page can read what the first page kept.
/// </summary>
public sealed class KeyedPageTests
{
    private static readonly CallContext Hub = new CallContext("c1", 2);

    [Fact]
    public void AFirstPageDescribesOnlyItsOwnEntriesAndKeepsTheRest()
    {
        WalkCursors cursors = new WalkCursors();
        using CallContext.Scope scope = CallContext.Enter(Hub);
        List<long> described = new List<long>();
        Dictionary<long, string> entries = Entries(10);

        Slice<string> first = KeyedPage.First(WalkRequest.Of(new VerbPaging(3, 2, ListOrder.Nearest, "open"), cursors),
            Keys(10), 2, 3, key => Describe(entries, described, key));

        Assert.Equal(new[] { "e2", "e3", "e4" }, first.Items);
        Assert.Equal(new long[] { 2, 3, 4 }, described);
        Assert.Equal(10, first.Total);
        Assert.Equal(2, first.Offset);
        Assert.True(first.HasMore);
        Assert.NotNull(first.CursorId);
        Assert.Equal(5, cursors.IdsHeld);
    }

    [Fact]
    public void LaterPagesComeFromTheKeptKeysPassingOverGoneEntries()
    {
        WalkCursors cursors = new WalkCursors();
        using CallContext.Scope scope = CallContext.Enter(Hub);
        List<long> described = new List<long>();
        Dictionary<long, string> entries = Entries(8);
        Slice<string> first = KeyedPage.First(WalkRequest.Of(new VerbPaging(3, 0, ListOrder.Nearest, "open"), cursors),
            Keys(8), 0, 3, key => Describe(entries, described, key));
        entries.Remove(4);

        Slice<string> second = KeyedPage.Later(WalkRequest.Of(new VerbPaging(3, 0, ListOrder.Nearest, first.CursorId),
            cursors), 3, key => Describe(entries, described, key));
        Slice<string> last = KeyedPage.Later(WalkRequest.Of(new VerbPaging(3, 0, ListOrder.Nearest, second.CursorId),
            cursors), 3, key => Describe(entries, described, key));

        Assert.Equal(new[] { "e3", "e5", "e6" }, second.Items);
        Assert.Equal(3, second.Offset);
        Assert.Equal(8, second.Total);
        Assert.True(second.HasMore);
        Assert.Equal(new[] { "e7" }, last.Items);
        Assert.False(last.HasMore);
        Assert.Null(last.CursorId);
        Assert.Equal(0, cursors.Count);
    }

    [Fact]
    public void ACallNamingSeveralThingsOpensNoCursorAndCannotGoOnOne()
    {
        using CallContext.Scope scope = CallContext.Enter(Hub);
        VerbPaging opened = new VerbPaging(2, 0, ListOrder.Nearest, "open");
        VerbPaging later = new VerbPaging(2, 0, ListOrder.Nearest, "k7");

        Assert.True(KeyedPage.WalkOf(opened, 1).Opens);
        Assert.False(KeyedPage.WalkOf(opened, 3).Opens);
        Assert.Null(KeyedPage.OneThing(later, 1));
        Assert.Equal("cursor", KeyedPage.OneThing(later, 3)!.Path);
    }

    [Fact]
    public void ALaterPageReadsWhatTheFirstKeptOnlyForItsOwner()
    {
        WalkCursors cursors = new WalkCursors();
        object kept = new object();
        string cursor;
        using (CallContext.Enter(Hub))
        {
            cursor = cursors.Open(CallContext.Current, new long[] { 5, 6 }, 1, 0, kept)!;
            Assert.Same(kept, WalkRequest.Of(new VerbPaging(2, 0, ListOrder.Nearest, cursor), cursors).Kept());
        }

        using (CallContext.Enter(new CallContext("c2", 2)))
        {
            Assert.Null(cursors.KeptOf(CallContext.Current, cursor));
        }

        Assert.Equal(2, cursors.IdsHeld);
    }

    private static long[] Keys(int count)
    {
        long[] keys = new long[count];
        for (int index = 0; index < count; index++)
        {
            keys[index] = index;
        }

        return keys;
    }

    private static Dictionary<long, string> Entries(int count)
    {
        Dictionary<long, string> entries = new Dictionary<long, string>();
        for (int index = 0; index < count; index++)
        {
            entries[index] = "e" + index;
        }

        return entries;
    }

    private static string? Describe(Dictionary<long, string> entries, List<long> described, long key)
    {
        if (!entries.TryGetValue(key, out string? entry))
        {
            return null;
        }

        described.Add(key);
        return entry;
    }
}
