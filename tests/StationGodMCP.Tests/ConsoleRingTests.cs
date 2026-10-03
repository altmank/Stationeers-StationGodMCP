#nullable enable

using System;
using System.Threading.Tasks;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// The ring a dedicated server's console lines are kept in (live: run_console_command on the dedicated server
/// captured nothing, "console buffer not filled"): newest replacing oldest, read oldest first, safe across threads.
/// </summary>
public sealed class ConsoleRingTests
{
    [Fact]
    public void ItReadsTheNewestLinesOldestFirst()
    {
        ConsoleRing<string> ring = new ConsoleRing<string>(3);
        ring.Add("a");
        ring.Add("b");

        Assert.Equal(new[] { "a", "b" }, ring.Recent(10));
        Assert.Equal(new[] { "b" }, ring.Recent(1));
        Assert.Empty(ring.Recent(0));
    }

    [Fact]
    public void PastCapacityTheOldestGo()
    {
        ConsoleRing<string> ring = new ConsoleRing<string>(3);
        foreach (string line in new[] { "a", "b", "c", "d", "e" })
        {
            ring.Add(line);
        }

        Assert.Equal(new[] { "c", "d", "e" }, ring.Recent(5));
        Assert.Equal(new[] { "d", "e" }, ring.Recent(2));
    }

    [Fact]
    public void LinesFromManyThreadsAreAllCounted()
    {
        ConsoleRing<int> ring = new ConsoleRing<int>(500);
        Parallel.For(0, 2000, line => ring.Add(line));

        Assert.Equal(500, ring.Recent(1000).Count);
    }

    [Fact]
    public void ARingHoldsAtLeastOneLine()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConsoleRing<string>(0));
    }
}
