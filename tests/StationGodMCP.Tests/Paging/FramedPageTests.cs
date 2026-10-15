#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Paging;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Verbs;
using StationGodMCP.Tests.Scheduling;
using Xunit;
using VerbPaging = StationGodMCP.Pure.Verbs.Paging;
using static StationGodMCP.Tests.Paging.WalkBuildTests;

namespace StationGodMCP.Tests.Paging;

/// <summary>
/// find thing's page taken over frames (Api/Shared/FramedPage): the reply built over many frames is the one-shot reply
/// byte for byte (a walk's first page, its later page, a page without a cursor at an offset), with nothing kept without
/// a cursor; a small query answers in its first slice; a page whose serialising would not fit what is left of the frame
/// answers at the start of the next slice.
/// </summary>
public sealed class FramedPageTests
{
    private const double BudgetMs = 4.0;
    private static readonly CallContext Hub = new CallContext("c1", 2);

    [Fact]
    public void AWalksPagesBuiltOverFramesAreTheOneShotPages()
    {
        using CallContext.Scope scope = CallContext.Enter(Hub);
        VerbPaging paging = PagingOf("""{"cursor": "open", "limit": 500}""");
        WalkCursors framedCursors = new WalkCursors();
        WalkCursors oneShotCursors = new WalkCursors();

        string framed = Framed(paging, framedCursors, 20000, out int frames);
        string oneShot = OneShot(paging, oneShotCursors, 20000);

        Assert.True(frames > 10, $"{frames} frames");
        Assert.Equal(oneShot, framed);
        Assert.Equal("k1", (string?)JObject.Parse(framed)["cursor_id"]);
        VerbPaging later = PagingOf("""{"cursor": "k1", "limit": 500}""");
        string framedLater = FramedLater(later, framedCursors, out int laterFrames);
        string oneShotLater = OneShotLater(later, oneShotCursors);
        Assert.True(laterFrames > 3, $"{laterFrames} frames");
        Assert.Equal(oneShotLater, framedLater);
    }

    [Fact]
    public void APageWithoutCursorBuiltOverFramesIsTheOneShotPageAndKeepsNothing()
    {
        VerbPaging paging = PagingOf("""{"offset": 10000, "limit": 500, "order": "id"}""");
        WalkCursors cursors = new WalkCursors();

        string framed = Framed(paging, cursors, 20000, out int frames);
        string oneShot = OneShot(paging, new WalkCursors(), 20000);

        Assert.True(frames > 10, $"{frames} frames");
        Assert.Equal(oneShot, framed);
        Assert.Null(JObject.Parse(framed)["cursor_id"]);
        Assert.Equal(0, cursors.Count);
    }

    [Theory]
    [InlineData("""{"cursor": "open"}""")]
    [InlineData("""{"offset": 3}""")]
    public void ASmallQueryAnswersInItsFirstSliceAsOneShotByteForByte(string options)
    {
        using CallContext.Scope scope = CallContext.Enter(Hub);
        VerbPaging paging = PagingOf(options);

        string framed = Framed(paging, new WalkCursors(), 50, out int frames);
        string oneShot = OneShot(paging, new WalkCursors(), 50);

        Assert.Equal(1, frames);
        Assert.Equal(oneShot, framed);
    }

    /// <summary>find thing's paging as its options bind it: limit's default from the catalogue (8).</summary>
    internal static VerbPaging PagingOf(string options) =>
        FindThingOptions.Bind(new OptionReader(FindThingOptions.Spec, JObject.Parse(options))).Paging;

    // The page over frames of 4 ms: building the list costs 0.01 ms a candidate, describing a thing 0.05 ms.
    private static string Framed(VerbPaging paging, WalkCursors cursors, int things, out int frames)
    {
        FakeClock clock = new FakeClock();
        List<FakeThing> world = World(things, new Random(13));
        FramedPage<FoundThingView> framed = FramedPage<FoundThingView>.Scan(WalkRequest.Of(paging, cursors),
            paging.Offset, paging.Limit!.Value,
            new WalkBuild<FakeThing>(world, new FakeFilter(clock, 0.01), paging.Order), Costing(clock, 0.05));
        return Run(framed, clock, out frames);
    }

    private static string FramedLater(VerbPaging paging, WalkCursors cursors, out int frames)
    {
        FakeClock clock = new FakeClock();
        FramedPage<FoundThingView> framed = FramedPage<FoundThingView>.Later(WalkRequest.Of(paging, cursors),
            paging.Limit!.Value, Costing(clock, 0.05));
        return Run(framed, clock, out frames);
    }

    private static string Run(FramedPage<FoundThingView> framed, FakeClock clock, out int frames)
    {
        frames = 1;
        while (!framed.Step(clock, clock.NowMs + BudgetMs))
        {
            frames++;
        }

        FramedResult<FoundThingView> result = framed.Result();
        return Json(FoundThings.Reply(result.Page, framed.Scanned, null, result.CursorId));
    }

    // A one-shot find: every candidate filtered, one sort, the page cut from the sorted list, the rest kept.
    private static string OneShot(VerbPaging paging, WalkCursors cursors, int things)
    {
        List<FakeThing> world = World(things, new Random(13));
        List<FakeThing> hits = new List<FakeThing>();
        foreach (FakeThing thing in world)
        {
            if (thing.Alive && thing.Matches)
            {
                hits.Add(thing);
            }
        }

        hits.Sort((a, b) => ListKey.Compare(paging.Order, a.Key, b.Key));
        Page<FakeThing> page = paging.Of(hits);
        string? cursorId = null;
        if (paging.OpensCursor && page.HasMore)
        {
            int next = page.Offset + page.Entries.Count;
            long[] rest = hits.GetRange(next, hits.Count - next)
                .ConvertAll(thing => thing.Id).ToArray();
            cursorId = cursors.Open(CallContext.Current, rest, next);
        }

        List<FoundThingView> views = new List<FoundThingView>();
        foreach (FakeThing thing in page.Entries)
        {
            views.Add(View(thing.Id));
        }

        return Json(FoundThings.Reply(
            new Page<FoundThingView>(views, page.Total, page.Offset, page.Limit, page.HasMore), world.Count, null,
            cursorId));
    }

    // A later page in one go: the next ids from the cursor.
    private static string OneShotLater(VerbPaging paging, WalkCursors cursors)
    {
        List<FoundThingView> views = new List<FoundThingView>();
        WalkStep step = WalkRequest.Of(paging, cursors).Next(paging.Limit!.Value, id => View(id), views);
        return Json(FoundThings.Reply(
            new Page<FoundThingView>(views, step.Total, step.Offset, paging.Limit, step.HasMore), 0, null,
            step.CursorId));
    }

    private static string Json(object reply) => StationGodMCP.Api.Shared.ApiJson.WriteShared(reply);

    // Describing a thing costs the clock costMs.
    private static Func<long, FoundThingView?> Costing(FakeClock clock, double costMs) => id =>
    {
        clock.Advance(costMs);
        return View(id);
    };

    private static FoundThingView View(long id) =>
        new FoundThingView(new ThingView(new ThingId(id), "Prefab", "Thing"), null, "Thing", "item", "Item", false,
            "ground", null, new List<HeldInView>(), new PositionView(1.0, 2.0, 3.0), 1.5, false, false);
}
