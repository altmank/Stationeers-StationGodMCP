#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace StationGodMCP.Tests;

public sealed class ManualTimeProviderTests
{
    [Fact]
    public async Task ADelayCompletesOnlyAfterTheClockAdvancesPastIt()
    {
        ManualTimeProvider time = new();
        Task delay = Task.Delay(TimeSpan.FromSeconds(2), time);

        await Task.Delay(50);
        Assert.False(delay.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(delay.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(1));
        await delay.WaitAsync(TestWait.DefaultLimit);
    }

    [Fact]
    public void ElapsedTimeFollowsAdvance()
    {
        ManualTimeProvider time = new();
        long started = time.GetTimestamp();

        time.Advance(TimeSpan.FromMilliseconds(750));

        Assert.Equal(TimeSpan.FromMilliseconds(750), time.GetElapsedTime(started));
    }

    [Fact]
    public void TwoTimersFireInDueOrderWithTheClockAtEachDueTime()
    {
        ManualTimeProvider time = new();
        long start = time.GetTimestamp();
        List<string> fired = [];
        using ITimer later = time.CreateTimer(_ => fired.Add($"later@{time.GetElapsedTime(start).TotalSeconds}"), null,
            TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
        using ITimer sooner = time.CreateTimer(_ => fired.Add($"sooner@{time.GetElapsedTime(start).TotalSeconds}"), null,
            TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);

        time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(["sooner@1", "later@3"], fired);
        Assert.Equal(TimeSpan.FromSeconds(5), time.GetElapsedTime(start));
    }

    [Fact]
    public void ADisposedTimerNeverFires()
    {
        ManualTimeProvider time = new();
        int fired = 0;
        ITimer timer = time.CreateTimer(_ => fired++, null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);

        timer.Dispose();
        time.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(0, fired);
    }

    [Fact]
    public void ATimerFiresOnceAndChangeRearmsIt()
    {
        ManualTimeProvider time = new();
        int fired = 0;
        using ITimer timer = time.CreateTimer(_ => fired++, null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);

        time.Advance(TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(1, fired);

        timer.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, fired);
    }
}
