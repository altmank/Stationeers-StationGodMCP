#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Paging;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Shaping;
using StationGodMCP.Tests.Scheduling;
using Xunit;

namespace StationGodMCP.Tests.Paging;

/// <summary>
/// Walk pages sized by cost (protocol.md, Walks): a page stops once its main-thread time, with the serialising its
/// entries will cost, reaches the frame's budget, whatever limit asked; it always takes one entry, limit still caps it,
/// and the next page goes on exactly where it stopped. The reply says so: next, and the note's reason frame_budget.
/// </summary>
public sealed class WalkPageBudgetTests
{
    private static readonly CallContext Hub = new CallContext("c1", 2);

    [Fact]
    public void APageStopsAtTheBudgetAndTheNextGoesOnWhereItStopped()
    {
        FakeClock clock = new FakeClock();
        WalkCursors cursors = new WalkCursors();
        string id = cursors.Open(Hub, Ids(1, 2, 3, 4, 5, 6, 7, 8, 9, 10), 0)!;

        List<object> first = new List<object>();
        Assert.True(cursors.TryNext(Hub, id, 500, Costing(clock, 1.0), first, out WalkStep cut,
            new PageBudget(clock, 3.5, 0.0)));
        List<object> second = new List<object>();
        Assert.True(cursors.TryNext(Hub, id, 500, Costing(clock, 1.0), second, out WalkStep rest));

        Assert.Equal(new long[] { 1, 2, 3, 4 }, Unboxed(first));
        Assert.True(cut.Cut);
        Assert.True(cut.HasMore);
        Assert.Equal(4, cut.Next);
        Assert.Equal(new long[] { 5, 6, 7, 8, 9, 10 }, Unboxed(second));
        Assert.Equal(4, rest.Offset);
        Assert.False(rest.Cut);
        Assert.False(rest.HasMore);
    }

    [Fact]
    public void TheSerialisingStillToComeCountsAgainstTheBudget()
    {
        FakeClock clock = new FakeClock();
        WalkCursors cursors = new WalkCursors();
        string id = cursors.Open(Hub, Ids(1, 2, 3, 4, 5, 6), 0)!;

        List<object> page = new List<object>();
        cursors.TryNext(Hub, id, 500, Costing(clock, 1.0), page, out WalkStep step, new PageBudget(clock, 3.5, 0.5));

        Assert.Equal(new long[] { 1, 2 }, Unboxed(page));
        Assert.True(step.Cut);
    }

    [Fact]
    public void APageTakesOneEntryEvenWithTheBudgetSpent()
    {
        FakeClock clock = new FakeClock();
        clock.Advance(10);
        WalkCursors cursors = new WalkCursors();
        string id = cursors.Open(Hub, Ids(1, 2, 3), 0)!;
        List<object> page = new List<object>();
        HashSet<long> gone = new HashSet<long> { 1 };

        cursors.TryNext(Hub, id, 500, thing => gone.Contains(thing) ? null : Boxed(thing), page, out WalkStep step,
            new PageBudget(clock, 4.0, 0.02));

        Assert.Equal(new long[] { 2 }, Unboxed(page));
        Assert.Equal(1, step.Skipped);
        Assert.Equal(2, step.Next);
        Assert.True(step.Cut);
    }

    [Fact]
    public void LimitStillCapsAPageTheBudgetWouldLetGrow()
    {
        FakeClock clock = new FakeClock();
        WalkCursors cursors = new WalkCursors();
        string id = cursors.Open(Hub, Ids(1, 2, 3, 4), 0)!;
        List<object> page = new List<object>();

        cursors.TryNext(Hub, id, 2, Costing(clock, 0.1), page, out WalkStep step, new PageBudget(clock, 100, 0.02));

        Assert.Equal(new long[] { 1, 2 }, Unboxed(page));
        Assert.False(step.Cut);
        Assert.True(step.HasMore);
    }

    [Fact]
    public void WithoutAFrameThereIsNoBudget()
    {
        PageBudget none = PageBudget.OfFrame(new FakeClock(), 1.0);

        Assert.True(none.Allows(100000));
    }

    [Fact]
    public void ACutPageNotesTheReasonAndTheReplyGivesNext()
    {
        WalkCursors cursors = new WalkCursors();
        using CallContext.Scope scope = CallContext.Enter(Hub);
        WalkRequest walk = WalkRequest.From(new Args(new JObject { ["cursor_id"] = "k1" }), cursors);
        PageRequest page = PageRequest.From(new Args(new JObject()), 8, 500);
        Truncations.Begin();

        walk.Note("things", page, 3, 20, "k1", cut: true);
        Truncation note = Assert.Single(Truncations.Take());
        Slice<FoundThingView> slice = Slice<FoundThingView>.Walked(new List<FoundThingView>(), 4, 8, 20, true);
        string json = WireCheck.New(new FindThingsView(slice, 0, null, "k1", 0, 9));
        string last = WireCheck.New(new FindThingsView(slice, 0, null));

        Assert.Equal(Truncation.FrameBudget, note.Reason);
        Assert.Contains("cursor_id k1", note.More);
        Assert.Contains("\"next\":9", json);
        Assert.DoesNotContain("next", last);
    }

    [Fact]
    public void TheSerialiseEstimateFollowsWhatTheRepliesCost()
    {
        EntryCost cost = new EntryCost();
        double before = cost.MsPerEntry;

        for (int reply = 0; reply < 50; reply++)
        {
            EntryCost.Begin();
            cost.Expect(100);
            EntryCost.Observed(10.0);
        }

        EntryCost.Begin();
        EntryCost.Observed(1000.0);

        Assert.Equal(EntryCost.InitialMsPerEntry, before);
        Assert.InRange(cost.MsPerEntry, 0.099, 0.101);
    }

    private static long[] Ids(params long[] ids) => ids;

    // Each thing found costs the clock costMs, as describing it would.
    private static System.Func<long, object?> Costing(FakeClock clock, double costMs) => id =>
    {
        clock.Advance(costMs);
        return Boxed(id);
    };

    private static object Boxed(long id) => id;

    private static List<long> Unboxed(List<object> things) => things.ConvertAll(thing => (long)thing);
}
