#nullable enable

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Facts;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// The followed facts' pure parts (cost design A2): the changed large cells the change points tell (ChangedCells) and
/// the set kept by change points from any thread (FollowedSet), and the world index telling a keeper of each thing it
/// files.
/// </summary>
public sealed class FollowedFactsTests
{
    // ---- ChangedCells ----

    [Theory]
    [InlineData(0, 10)]
    [InlineData(19, 10)]
    [InlineData(20, 30)]
    [InlineData(-1, -10)]
    [InlineData(-20, -10)]
    [InlineData(-21, -30)]
    public void APointIsInTheLargeCellCentredAtTheOddMetreBelowIt(int dm, int centreDm)
    {
        Assert.Equal(new GridCell(centreDm, centreDm, centreDm), ChangedCells.LargeCellOf(dm, dm, dm));
    }

    [Fact]
    public void AStructureGridIsToldAsItsLargeCell()
    {
        ChangedCells cells = new ChangedCells();
        cells.Add(15, 5, 25);
        List<GridCell> taken = new List<GridCell>();

        Assert.True(cells.Take(taken));
        Assert.Equal(new[] { new GridCell(10, 10, 30) }, taken);
        Assert.True(cells.Take(taken));
        Assert.Empty(taken);
    }

    [Fact]
    public void AVoxelReachesTheCellsWithinTwoMetresOnEachAxis()
    {
        ChangedCells cells = new ChangedCells();
        // A voxel at 4 m: centres within 2 m are 3 and 5 m. At 5 m: 3, 5 and 7 m.
        cells.AddVoxel(4f, 5f, 4f);
        List<GridCell> taken = new List<GridCell>();
        cells.Take(taken);

        HashSet<GridCell> expected = new HashSet<GridCell>();
        foreach (int x in new[] { 30, 50 })
        {
            foreach (int y in new[] { 30, 50, 70 })
            {
                foreach (int z in new[] { 30, 50 })
                {
                    expected.Add(new GridCell(x, y, z));
                }
            }
        }

        Assert.Equal(expected, new HashSet<GridCell>(taken));
    }

    [Fact]
    public void ACellsNeighboursAreTheSixLargeCellsAround()
    {
        List<GridCell> around = new List<GridCell>();
        ChangedCells.Around(new GridCell(10, 10, 10), around);

        Assert.Equal(7, around.Count);
        Assert.Contains(new GridCell(10, 10, 10), around);
        Assert.Contains(new GridCell(30, 10, 10), around);
        Assert.Contains(new GridCell(-10, 10, 10), around);
        Assert.Contains(new GridCell(10, 30, 10), around);
        Assert.Contains(new GridCell(10, -10, 10), around);
        Assert.Contains(new GridCell(10, 10, 30), around);
        Assert.Contains(new GridCell(10, 10, -10), around);
    }

    [Fact]
    public void TooManyCellsWaitingSayEverythingChangedOnce()
    {
        ChangedCells cells = new ChangedCells();
        for (int index = 0; index <= ChangedCells.MaximumWaiting; index++)
        {
            cells.Add(index * 20, 0, 0);
        }

        List<GridCell> taken = new List<GridCell>();
        Assert.False(cells.Take(taken));
        Assert.Empty(taken);
        Assert.True(cells.Take(taken));
        Assert.Equal(ChangedCells.MaximumWaiting + 1, cells.Told);
    }

    // ---- FollowedSet ----

    private sealed class Thing
    {
        internal volatile bool Damaged;

        // Unity objects override equality; the set must not depend on it.
        public override bool Equals(object? obj) => obj is Thing;

        public override int GetHashCode() => 0;
    }

    [Fact]
    public void AChangeThatFlipsAThingIsFiledAndOneThatDoesNotIsNotQueued()
    {
        FollowedSet<Thing> set = new FollowedSet<Thing>(static thing => thing.Damaged);
        Thing pipe = new Thing { Damaged = true };
        Thing wall = new Thing();

        set.Changed(pipe, true);
        set.Changed(wall, false);
        Assert.Equal(1, set.Waiting);
        Assert.True(set.Drain(10));
        Assert.True(set.Contains(pipe));
        Assert.False(set.Contains(wall));

        // Decay ticking an already damaged thing queues nothing.
        set.Changed(pipe, true);
        Assert.Equal(0, set.Waiting);

        pipe.Damaged = false;
        set.Changed(pipe, false);
        set.Drain(10);
        Assert.Equal(0, set.Count);
    }

    [Fact]
    public void FilingReadsTheThingNowNotWhatTheChangePointSaw()
    {
        FollowedSet<Thing> set = new FollowedSet<Thing>(static thing => thing.Damaged);
        Thing pipe = new Thing { Damaged = true };
        set.Changed(pipe, true);
        pipe.Damaged = false;

        set.Drain(10);

        Assert.False(set.Contains(pipe));
    }

    [Fact]
    public void AChangeRacingTheFilingIsFiledAgain()
    {
        Thing pipe = new Thing { Damaged = true };
        int reads = 0;
        // The second read of the filing sees a heal that ran between the read and the update.
        FollowedSet<Thing> set = new FollowedSet<Thing>(thing =>
        {
            reads++;
            if (reads == 2)
            {
                thing.Damaged = false;
            }

            return thing.Damaged;
        });

        set.Queue(pipe);
        set.Drain(1);
        Assert.True(set.Contains(pipe));
        Assert.Equal(1, set.Waiting);

        set.Drain(10);
        Assert.False(set.Contains(pipe));
    }

    [Fact]
    public void DrainStopsAtItsBudgetAndGoesOnNextTime()
    {
        FollowedSet<Thing> set = new FollowedSet<Thing>(static thing => thing.Damaged);
        for (int index = 0; index < 5; index++)
        {
            set.Queue(new Thing { Damaged = true });
        }

        Assert.False(set.Drain(3));
        Assert.Equal(3, set.Count);
        Assert.Equal(2, set.Waiting);
        Assert.True(set.Drain(3));
        Assert.Equal(5, set.Count);
    }

    [Fact]
    public async Task ChangesFromOtherThreadsAllReachTheSet()
    {
        FollowedSet<Thing> set = new FollowedSet<Thing>(static thing => thing.Damaged);
        List<Thing> things = new List<Thing>();
        for (int index = 0; index < 4000; index++)
        {
            things.Add(new Thing());
        }

        using CancellationTokenSource stop = new CancellationTokenSource();
        Task filing = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                set.Drain(64);
            }
        });
        Parallel.For(0, things.Count, index =>
        {
            Thing thing = things[index];
            thing.Damaged = true;
            set.Changed(thing, true);
            if (index % 3 == 0)
            {
                thing.Damaged = false;
                set.Changed(thing, false);
            }
        });
        stop.Cancel();
        await filing;
        set.Drain(int.MaxValue);

        int expected = 0;
        foreach (Thing thing in things)
        {
            Assert.Equal(thing.Damaged, set.Contains(thing));
            expected += thing.Damaged ? 1 : 0;
        }

        Assert.Equal(expected, set.Count);
    }

    [Fact]
    public void RemoveAndClearEmptyTheSet()
    {
        FollowedSet<Thing> set = new FollowedSet<Thing>(static thing => thing.Damaged);
        Thing a = new Thing { Damaged = true };
        Thing b = new Thing { Damaged = true };
        set.Queue(a);
        set.Queue(b);
        set.Drain(10);

        set.Remove(a);
        Assert.Equal(new[] { b }, set.Members());
        set.Queue(a);
        set.Clear();
        Assert.Equal(0, set.Count);
        Assert.Equal(0, set.Waiting);
    }

    // ---- WorldIndex tells a keeper of each thing it files ----

    [Fact]
    public void TheIndexTellsEachThingOnceWhenFirstFiledAndSettleFilesEverything()
    {
        List<string> filed = new List<string>();
        List<string> listed = new List<string> { "a", "b" };
        WorldIndex<string> index = new WorldIndex<string>(static name => name, null, filed.Add);

        index.Settle(() => new List<string>(listed), _ => true);
        Assert.Equal(new[] { "a", "b" }, filed);

        index.Arrived("c");
        index.Arrived("a");
        index.Settle(() => new List<string>(listed), _ => true);
        Assert.Equal(new[] { "a", "b", "c" }, filed);
    }
}
