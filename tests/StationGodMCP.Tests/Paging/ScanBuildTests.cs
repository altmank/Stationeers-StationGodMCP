#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Paging;
using StationGodMCP.Pure.Scheduling;
using StationGodMCP.Tests.Scheduling;
using Xunit;

namespace StationGodMCP.Tests.Paging;

/// <summary>
/// A walk's ids from a scan spread over frames (ScanBuild): the body's candidates one per step, then a bounded sort;
/// the ids come out as a one-shot sort of the kept keys gives them, in any order the scan names, and a slice never runs
/// past its end by more than one step.
/// </summary>
public sealed class ScanBuildTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheIdsAreAOneShotSortOfTheKeptKeys(bool nearest)
    {
        ListOrder order = nearest ? ListOrder.Nearest : ListOrder.ReferenceId;
        FakeClock clock = new FakeClock();
        List<ListKey> world = World(3000, new Random(4));
        ScanBuild build = new ScanBuild(keys => Scan(clock, world, keys), order);

        int slices = 0;
        while (!build.Run(clock, clock.NowMs + 2.0))
        {
            slices++;
        }

        List<ListKey> kept = world.FindAll(static key => key.Id % 3 != 0);
        kept.Sort((a, b) => ListKey.Compare(order, a, b));
        Assert.Equal(kept.ConvertAll(static key => key.Id).ToArray(), build.Ids());
        Assert.Equal(3000, build.Candidates);
        Assert.True(slices > 10);
    }

    [Fact]
    public void ACustomOrderSortsTheKeys()
    {
        FakeClock clock = new FakeClock();
        List<ListKey> world = World(500, new Random(8));
        ScanBuild build = new ScanBuild(keys => Scan(clock, world, keys), static (a, b) => b.Id.CompareTo(a.Id));
        while (!build.Run(clock, clock.NowMs + 1.0))
        {
        }

        long[] ids = build.Ids();
        for (int index = 1; index < ids.Length; index++)
        {
            Assert.True(ids[index - 1] > ids[index]);
        }
    }

    [Fact]
    public void ASliceEndsWithinItsBudgetWhenItsStepsAreAlike()
    {
        FakeClock clock = new FakeClock();
        List<ListKey> world = World(400, new Random(2));
        ScanBuild build = new ScanBuild(keys => Scan(clock, world, keys), ListOrder.Nearest);

        double end = clock.NowMs + 1.0;
        build.Run(clock, end);

        Assert.InRange(clock.NowMs, end - 0.1, end + 1e-9);
        Assert.False(build.Done);
    }

    [Fact]
    public void TheIdsAreNotReadyBeforeTheBuildIsDone()
    {
        FakeClock clock = new FakeClock();
        ScanBuild build = new ScanBuild(keys => Scan(clock, World(50, new Random(1)), keys), ListOrder.Nearest);
        build.Run(clock, clock.NowMs + 0.5);

        Assert.Throws<InvalidOperationException>(() => build.Ids());
    }

    // Every candidate costs 0.1 ms; ids divisible by 3 are not kept.
    private static IEnumerable<object?> Scan(FakeClock clock, List<ListKey> world, ScanKeys keys)
    {
        foreach (ListKey key in world)
        {
            clock.Advance(0.1);
            keys.Candidates++;
            if (key.Id % 3 != 0)
            {
                keys.Add(key);
            }

            yield return null;
        }
    }

    private static List<ListKey> World(int count, Random random)
    {
        List<ListKey> world = new List<ListKey>(count);
        for (int id = 1; id <= count; id++)
        {
            double? distance = random.Next(10) == 0 ? null : Math.Round(random.NextDouble() * 50.0, 1);
            world.Add(new ListKey(distance, "Thing" + random.Next(20), random.Next(2), id));
        }

        return world;
    }
}
