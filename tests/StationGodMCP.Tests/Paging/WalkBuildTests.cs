#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Paging;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Scheduling;
using StationGodMCP.Tests.Scheduling;
using Xunit;

namespace StationGodMCP.Tests.Paging;

/// <summary>
/// A walk's first call built in slices (Pure/Paging/WalkBuild): over a synthetic world the slice-built walk serves the
/// same ids in the same order as today's one-shot scan and sort, with things removed and built while it is built and
/// paged; every slice stops at its end; a small world is built in one; a frame of steady work allocates nothing.
/// </summary>
public sealed class WalkBuildTests
{
    private const double BudgetMs = 4.0;
    private static readonly CallContext Hub = new CallContext("c1", 2);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ASliceBuiltWalkServesTheOneShotWalkWithRemovedThingsSkippedAndNewOnesLeftOut(bool nearest)
    {
        ListOrder order = nearest ? ListOrder.Nearest : ListOrder.ReferenceId;
        Random random = new Random(7);
        List<FakeThing> world = World(5000, random);
        List<FakeThing> snapshot = new List<FakeThing>(world);
        long[] expected = OneShot(snapshot, order);
        FakeClock clock = new FakeClock();
        WalkBuild<FakeThing> build = new WalkBuild<FakeThing>(snapshot, new FakeFilter(clock, 0.01), order);
        Dictionary<long, FakeThing> byId = ById(world);

        int frames = 1;
        while (!build.Run(clock, clock.NowMs + BudgetMs))
        {
            frames++;
            Churn(world, byId, random);
        }

        List<long> served = Serve(build.Ids(), byId, world, random);

        // Served: the one-shot walk in its order, each thing once, less only things that are gone; none built later.
        int at = 0;
        List<long> missed = new List<long>();
        foreach (long id in expected)
        {
            if (at < served.Count && served[at] == id)
            {
                at++;
            }
            else
            {
                missed.Add(id);
            }
        }

        Assert.True(frames > 10, $"{frames} frames: the build was not spread");
        Assert.Equal(served.Count, at);
        Assert.NotEmpty(missed);
        Assert.All(missed, id => Assert.False(byId[id].Alive));
    }

    [Fact]
    public void NoSliceRunsPastItsEnd()
    {
        TickingClock clock = new TickingClock(0.5);
        WalkBuild<FakeThing> build =
            new WalkBuild<FakeThing>(World(2000, new Random(3)), new FakeFilter(clock, 0.0), ListOrder.Nearest);

        int frames = 0;
        bool done = false;
        while (!done)
        {
            double startMs = clock.Peek;
            done = build.Run(clock, startMs + BudgetMs);
            frames++;
            Assert.True(clock.Peek - startMs <= BudgetMs, $"frame {frames} spent {clock.Peek - startMs} ms");
            Assert.True(done || clock.Peek - startMs == BudgetMs);
        }

        Assert.True(frames > 1);
    }

    [Fact]
    public void ASmallWorldIsBuiltInOneSlice()
    {
        FakeClock clock = new FakeClock();
        WalkBuild<FakeThing> build =
            new WalkBuild<FakeThing>(World(50, new Random(5)), new FakeFilter(clock, 0.01), ListOrder.Nearest);

        Assert.True(build.Run(clock, clock.NowMs + BudgetMs));
        Assert.Equal(OneShot(World(50, new Random(5)), ListOrder.Nearest), build.Ids());
    }

    [Fact]
    public void AnEmptyWalkIsBuiltAtOnce()
    {
        FakeClock clock = new FakeClock();
        WalkBuild<FakeThing> build =
            new WalkBuild<FakeThing>(new List<FakeThing>(), new FakeFilter(clock, 0.01), ListOrder.Nearest);

        Assert.True(build.Run(clock, clock.NowMs));
        Assert.Empty(build.Ids());
    }

    [Fact]
    public void ASteadyFrameAllocatesNothing()
    {
        Build(new TickingClock(0.05));
        TickingClock clock = new TickingClock(0.05);
        WalkBuild<FakeThing> build =
            new WalkBuild<FakeThing>(World(5000, new Random(9)), new FakeFilter(clock, 0.0), ListOrder.Nearest);

        int allocating = 0;
        int frames = 0;
        bool done = false;
        while (!done)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            done = build.Run(clock, clock.NowMs + BudgetMs);
            if (GC.GetAllocatedBytesForCurrentThread() != before)
            {
                allocating++;
            }

            frames++;
        }

        Assert.True(frames > 50);
        Assert.True(allocating <= 1, $"{allocating} of {frames} frames allocated");
    }

    [Fact]
    public void TheFirstPageComesFromTheBuiltIdsAndKeepsTheRestUnderACursor()
    {
        WalkCursors cursors = new WalkCursors();
        using CallContext.Scope scope = CallContext.Enter(Hub);
        WalkRequest walk = WalkRequest.From(new Args(new JObject { ["cursor"] = true }), cursors);
        List<object> page = new List<object>();
        HashSet<long> gone = new HashSet<long> { 2 };

        WalkStep first = walk.First(new long[] { 1, 2, 3, 4, 5 }, 2, id => gone.Contains(id) ? null : Boxed(id), page,
            PageBudget.None);
        WalkStep all = WalkRequest.From(new Args(new JObject { ["cursor"] = true }), cursors)
            .First(new long[] { 1, 2 }, 8, Boxed, new List<object>(), PageBudget.None);

        Assert.Equal(new object[] { 1L, 3L }, page);
        Assert.Equal(1, first.Skipped);
        Assert.Equal(3, first.Next);
        Assert.Equal(5, first.Total);
        Assert.True(first.HasMore);
        Assert.NotNull(first.CursorId);
        List<object> second = new List<object>();
        WalkStep next = WalkRequest.From(new Args(new JObject { ["cursor_id"] = first.CursorId }), cursors)
            .Next(8, Boxed, second);
        Assert.Equal(new object[] { 4L, 5L }, second);
        Assert.Equal(3, next.Offset);
        Assert.Null(all.CursorId);
        Assert.False(all.HasMore);
        Assert.Equal(0, cursors.Count);
    }

    // A whole build on clock, for warming the code up.
    private static void Build(IMonotonicClock clock)
    {
        WalkBuild<FakeThing> build =
            new WalkBuild<FakeThing>(World(5000, new Random(9)), new FakeFilter(clock, 0.0), ListOrder.Nearest);
        while (!build.Run(clock, clock.NowMs + BudgetMs))
        {
        }

        build.Ids();
    }

    // Pages of 37 through a walk, things removed between pages too.
    private static List<long> Serve(long[] ids, Dictionary<long, FakeThing> byId, List<FakeThing> world, Random random)
    {
        WalkCursors cursors = new WalkCursors();
        using CallContext.Scope scope = CallContext.Enter(Hub);
        Func<long, object?> live = id => byId.TryGetValue(id, out FakeThing? thing) && thing.Alive ? Boxed(id) : null;
        List<object> page = new List<object>();
        WalkStep step = WalkRequest.From(new Args(new JObject { ["cursor"] = true }), cursors)
            .First(ids, 37, live, page, PageBudget.None);
        List<long> served = page.ConvertAll(thing => (long)thing);
        while (step.HasMore)
        {
            Churn(world, byId, random);
            page.Clear();
            step = WalkRequest.From(new Args(new JObject { ["cursor_id"] = step.CursorId }), cursors)
                .Next(37, live, page);
            served.AddRange(page.ConvertAll(thing => (long)thing));
        }

        return served;
    }

    // Between two frames: three things go, two are built.
    private static void Churn(List<FakeThing> world, Dictionary<long, FakeThing> byId, Random random)
    {
        for (int gone = 0; gone < 3; gone++)
        {
            world[random.Next(world.Count)].Alive = false;
        }

        for (int built = 0; built < 2; built++)
        {
            FakeThing thing = Thing(random, 1000000 + byId.Count);
            world.Add(thing);
            byId.Add(thing.Id, thing);
        }
    }

    // Today's walk: every candidate filtered, then one sort.
    private static long[] OneShot(List<FakeThing> things, ListOrder order)
    {
        List<ListKey> keys = new List<ListKey>();
        foreach (FakeThing thing in things)
        {
            if (thing.Alive && thing.Matches)
            {
                keys.Add(thing.Key);
            }
        }

        keys.Sort((a, b) => ListKey.Compare(order, a, b));
        return keys.ConvertAll(key => key.Id).ToArray();
    }

    internal static List<FakeThing> World(int count, Random random)
    {
        List<FakeThing> world = new List<FakeThing>(count);
        for (int index = 0; index < count; index++)
        {
            world.Add(Thing(random, 0));
        }

        for (int index = 0; index < count; index++)
        {
            world[index].Id = 5000000 - index * 7L;
        }

        return world;
    }

    // Few distances and names, so ties are broken by name and then by id.
    private static FakeThing Thing(Random random, long id) => new FakeThing
    {
        Id = id,
        Prefab = "Prefab" + random.Next(12),
        Distance = random.Next(10) == 0 ? null : random.Next(40),
        Matches = random.Next(4) != 0
    };

    private static Dictionary<long, FakeThing> ById(List<FakeThing> world)
    {
        Dictionary<long, FakeThing> byId = new Dictionary<long, FakeThing>();
        foreach (FakeThing thing in world)
        {
            byId.Add(thing.Id, thing);
        }

        return byId;
    }

    private static object Boxed(long id) => id;

    internal sealed class FakeThing
    {
        internal long Id { get; set; }

        internal string Prefab { get; set; } = string.Empty;

        internal double? Distance { get; set; }

        internal bool Matches { get; set; }

        internal bool Alive { get; set; } = true;

        internal ListKey Key => new ListKey(Distance, Prefab, 0, Id);
    }

    /// <summary>The walk's filter on a fake world: each candidate costs the clock costMs; gone ones never match.</summary>
    internal sealed class FakeFilter : IWalkFilter<FakeThing>
    {
        private readonly IMonotonicClock _clock;
        private readonly double _costMs;

        internal FakeFilter(IMonotonicClock clock, double costMs)
        {
            _clock = clock;
            _costMs = costMs;
        }

        public bool TryKey(FakeThing thing, out ListKey key)
        {
            (_clock as FakeClock)?.Advance(_costMs);
            key = thing.Key;
            return thing.Alive && thing.Matches;
        }
    }

    /// <summary>A clock that moves by tickMs each time it is read: every step of a build costs the same.</summary>
    internal sealed class TickingClock : IMonotonicClock
    {
        private readonly double _tickMs;
        private double _nowMs;

        internal TickingClock(double tickMs) => _tickMs = tickMs;

        public double NowMs => _nowMs += _tickMs;

        /// <summary>The time, without moving it.</summary>
        internal double Peek => _nowMs;
    }
}
