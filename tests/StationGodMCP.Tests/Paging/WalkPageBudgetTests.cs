#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure.Paging;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Tests.Scheduling;
using Xunit;

namespace StationGodMCP.Tests.Paging;

/// <summary>
/// A frame's share of a walk's page (Pure/Paging/PageBudget): it stops at the frame's budget, always takes one entry,
/// limit still caps it, and the next share goes on exactly where it stopped; the serialise estimate (EntryCost) follows
/// what the replies cost.
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
            new PageBudget(clock, 3.5)));
        List<object> second = new List<object>();
        Assert.True(cursors.TryNext(Hub, id, 500, Costing(clock, 1.0), second, out WalkStep rest));

        Assert.Equal(new long[] { 1, 2, 3, 4 }, Unboxed(first));
        Assert.True(cut.HasMore);
        Assert.Equal(new long[] { 5, 6, 7, 8, 9, 10 }, Unboxed(second));
        Assert.Equal(4, rest.Offset);
        Assert.False(rest.HasMore);
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
            new PageBudget(clock, 4.0));

        Assert.Equal(new long[] { 2 }, Unboxed(page));
        Assert.Equal(1, step.Skipped);
    }

    [Fact]
    public void LimitStillCapsAPageTheBudgetWouldLetGrow()
    {
        FakeClock clock = new FakeClock();
        WalkCursors cursors = new WalkCursors();
        string id = cursors.Open(Hub, Ids(1, 2, 3, 4), 0)!;
        List<object> page = new List<object>();

        cursors.TryNext(Hub, id, 2, Costing(clock, 0.1), page, out WalkStep step, new PageBudget(clock, 100));

        Assert.Equal(new long[] { 1, 2 }, Unboxed(page));
        Assert.True(step.HasMore);
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
