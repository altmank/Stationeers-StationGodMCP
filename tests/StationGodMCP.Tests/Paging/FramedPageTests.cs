#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Paging;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Shaping;
using StationGodMCP.Tests.Scheduling;
using Xunit;
using static StationGodMCP.Tests.Paging.WalkBuildTests;

namespace StationGodMCP.Tests.Paging;

/// <summary>
/// A find's page taken over frames (Api/Shared/FramedPage), as find_things takes every page: the reply built over many
/// frames is the one-shot reply byte for byte (walk first page, its later page, a page without cursor at an offset),
/// with the same truncation note and nothing kept without a cursor; a small query answers in its first slice; a page
/// whose serialising would not fit what is left of the frame answers at the start of the next slice.
/// </summary>
public sealed class FramedPageTests
{
    private const double BudgetMs = 4.0;
    private const double SerialiseMs = 0.02;
    private static readonly CallContext Hub = new CallContext("c1", 2);

    [Fact]
    public void AWalksPagesBuiltOverFramesAreTheOneShotPages()
    {
        using CallContext.Scope scope = CallContext.Enter(Hub);
        JObject args = new JObject { ["cursor"] = true, ["limit"] = 500 };
        WalkCursors framedCursors = new WalkCursors();
        WalkCursors oneShotCursors = new WalkCursors();

        Reply framed = Framed(args, framedCursors, 20000, out int frames);
        Reply oneShot = OneShot(args, oneShotCursors, 20000);

        Assert.True(frames > 10, $"{frames} frames");
        Assert.Equal(oneShot.Json, framed.Json);
        Assert.Equal(oneShot.Notes, framed.Notes);
        JObject later = new JObject { ["cursor_id"] = "k1", ["limit"] = 500 };
        Reply framedLater = FramedLater(later, framedCursors, out int laterFrames);
        Reply oneShotLater = OneShotLater(later, oneShotCursors);
        Assert.True(laterFrames > 3, $"{laterFrames} frames");
        Assert.Equal(oneShotLater.Json, framedLater.Json);
        Assert.Equal(oneShotLater.Notes, framedLater.Notes);
    }

    [Fact]
    public void APageWithoutCursorBuiltOverFramesIsTheOneShotPageAndKeepsNothing()
    {
        JObject args = new JObject { ["offset"] = 10000, ["limit"] = 500, ["order"] = "reference_id" };
        WalkCursors cursors = new WalkCursors();

        Reply framed = Framed(args, cursors, 20000, out int frames);
        Reply oneShot = OneShot(args, new WalkCursors(), 20000);

        Assert.True(frames > 10, $"{frames} frames");
        Assert.Equal(oneShot.Json, framed.Json);
        Assert.Equal(oneShot.Notes, framed.Notes);
        Assert.Equal(0, cursors.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ASmallQueryAnswersInItsFirstSliceAsTodayByteForByte(bool cursor)
    {
        using CallContext.Scope scope = CallContext.Enter(Hub);
        JObject args = cursor ? new JObject { ["cursor"] = true } : new JObject { ["offset"] = 3 };

        Reply framed = Framed(args, new WalkCursors(), 50, out int frames);
        Reply oneShot = OneShot(args, new WalkCursors(), 50);

        Assert.Equal(1, frames);
        Assert.Equal(oneShot.Json, framed.Json);
        Assert.Equal(oneShot.Notes, framed.Notes);
    }

    [Fact]
    public void ASerialiseThatWouldNotFitWaitsForTheNextSlice()
    {
        FakeClock clock = new FakeClock();
        FramedPage<FoundThingView> page = FramedPage<FoundThingView>.Scan(
            WalkRequest.From(new Args(new JObject { ["limit"] = 500 }), new WalkCursors()),
            PageRequest.From(new Args(new JObject { ["limit"] = 500 }), 8, 500),
            new WalkBuild<FakeThing>(World(20, new Random(1)), new FakeFilter(clock, 0.0), ListOrder.Nearest),
            Costing(clock, 0.1));

        bool first = page.Step(clock, clock.NowMs + BudgetMs, 0.5);
        double before = clock.NowMs;
        bool second = page.Step(clock, clock.NowMs + BudgetMs, 0.5);

        Assert.False(first);
        Assert.True(second);
        Assert.Equal(before, clock.NowMs);
    }

    // The page over frames of 4 ms: building the list costs 0.01 ms a candidate, describing a thing 0.05 ms.
    private static Reply Framed(JObject parameters, WalkCursors cursors, int things, out int frames)
    {
        FakeClock clock = new FakeClock();
        Args args = new Args(parameters);
        WalkRequest walk = WalkRequest.From(args, cursors);
        PageRequest page = PageRequest.From(args, 8, 500);
        List<FakeThing> world = World(things, new Random(13));
        FramedPage<FoundThingView> framed = FramedPage<FoundThingView>.Scan(walk, page,
            new WalkBuild<FakeThing>(world, new FakeFilter(clock, 0.01), OrderOf(parameters)), Costing(clock, 0.05));
        return Run(framed, clock, Advice(parameters), out frames);
    }

    private static Reply FramedLater(JObject parameters, WalkCursors cursors, out int frames)
    {
        FakeClock clock = new FakeClock();
        Args args = new Args(parameters);
        FramedPage<FoundThingView> framed = FramedPage<FoundThingView>.Later(WalkRequest.From(args, cursors),
            PageRequest.From(args, 8, 500), Costing(clock, 0.05));
        return Run(framed, clock, string.Empty, out frames);
    }

    private static Reply Run(FramedPage<FoundThingView> framed, FakeClock clock, string advice, out int frames)
    {
        frames = 1;
        while (!framed.Step(clock, clock.NowMs + BudgetMs, SerialiseMs))
        {
            frames++;
        }

        Truncations.Begin();
        Slice<FoundThingView> slice = framed.Note("things", advice, out string? cursorId, out int? skipped);
        return new Reply(new FindThingsView(slice, framed.Scanned, null, cursorId, skipped), Truncations.Take());
    }

    // Today's one-shot find: every candidate filtered, one sort, the page cut from the sorted list, the rest kept.
    private static Reply OneShot(JObject parameters, WalkCursors cursors, int things)
    {
        Args args = new Args(parameters);
        WalkRequest walk = WalkRequest.From(args, cursors);
        PageRequest page = PageRequest.From(args, 8, 500);
        List<FakeThing> world = World(things, new Random(13));
        ListOrder order = OrderOf(parameters);
        List<FakeThing> hits = new List<FakeThing>();
        foreach (FakeThing thing in world)
        {
            if (thing.Alive && thing.Matches)
            {
                hits.Add(thing);
            }
        }

        hits.Sort((a, b) => ListKey.Compare(order, a.Key, b.Key));
        Slice<FakeThing> slice = Slice<FakeThing>.Of(hits, page);
        List<FoundThingView> views = slice.Items.ConvertAll(thing => View(thing.Id));
        Truncations.Begin();
        string? cursorId = walk.Keep(hits, views.Count, static thing => thing.Id);
        walk.Note("things", page, views.Count, hits.Count, cursorId, Advice(parameters));
        return new Reply(new FindThingsView(Slice<FoundThingView>.Page(views, page, hits.Count), world.Count, null,
            cursorId), Truncations.Take());
    }

    // Today's later page: the next ids from the cursor, in one go.
    private static Reply OneShotLater(JObject parameters, WalkCursors cursors)
    {
        Args args = new Args(parameters);
        WalkRequest walk = WalkRequest.From(args, cursors);
        PageRequest page = PageRequest.From(args, 8, 500);
        List<FoundThingView> views = new List<FoundThingView>();
        Truncations.Begin();
        WalkStep step = walk.Next(page.Limit, id => View(id), views);
        walk.Note("things", page, views.Count, step.Total, step.CursorId);
        return new Reply(new FindThingsView(
            Slice<FoundThingView>.Walked(views, step.Offset, page.Limit, step.Total, step.HasMore), 0, null,
            step.CursorId, step.Skipped), Truncations.Take());
    }

    private static ListOrder OrderOf(JObject parameters) =>
        (string?)parameters["order"] == "reference_id" ? ListOrder.ReferenceId : ListOrder.Nearest;

    private static string Advice(JObject parameters) => ListOrders.PagingAdvice(OrderOf(parameters), true);

    // Describing a thing costs the clock costMs.
    private static Func<long, FoundThingView?> Costing(FakeClock clock, double costMs) => id =>
    {
        clock.Advance(costMs);
        return View(id);
    };

    private static FoundThingView View(long id) =>
        new FoundThingView(new ThingView(new ThingId(id), "Prefab", "Thing"), null, "Thing", "item", "Item", false,
            "ground", null, new List<HeldInView>(), new PositionView(1.0, 2.0, 3.0), 1.5, false, false);

    private sealed class Reply
    {
        internal Reply(FindThingsView view, List<Truncation> notes)
        {
            Json = WireCheck.New(view);
            Notes = notes.ConvertAll(note => $"{note.List}|{note.Returned}|{note.Total}|{note.AtLeast}|{note.More}");
        }

        internal string Json { get; }

        internal List<string> Notes { get; }
    }
}
