#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StationGodMCP.Client;
using Xunit;

namespace StationGodMCP.Tests.Sidecar;

/// <summary>The GameLink's admission ring on its own: limits, round robin by stream with stream 0 first, giving up.</summary>
public sealed class AdmissionTests
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ACallWithRoomGoesInAtOnce()
    {
        Admission admission = new(perStream: 2, perConnection: 4);

        ValueTask<bool> entered = admission.EnterAsync(5, Long, default);

        Assert.True(entered.IsCompletedSuccessfully);
        Assert.True(await entered);
        Assert.Equal(1, admission.InFlight);
    }

    [Fact]
    public async Task FreedSlotsGoRoundRobinWithStreamZeroFirstInEachRound()
    {
        Admission admission = new(perStream: 1, perConnection: 1);
        Assert.True(await admission.EnterAsync(9, Long, default));
        List<long> order = [];
        List<Task> waiting = [];
        foreach (long stream in (long[])[3, 3, 3, 4, 4, 0, 0])
        {
            waiting.Add(Admitted(admission, stream, order));
        }

        admission.Leave(9);
        await Task.WhenAll(waiting);

        Assert.Equal([0L, 3L, 4L, 0L, 3L, 4L, 3L], order);
    }

    [Fact]
    public async Task AStreamAtItsLimitDoesNotHoldBackAnother()
    {
        Admission admission = new(perStream: 1, perConnection: 8);
        Assert.True(await admission.EnterAsync(1, Long, default));
        ValueTask<bool> second = admission.EnterAsync(1, Long, default);

        ValueTask<bool> other = admission.EnterAsync(2, Long, default);

        Assert.False(second.IsCompleted);
        Assert.True(other.IsCompletedSuccessfully);
        admission.Leave(1);
        Assert.True(await second);
    }

    [Fact]
    public async Task AWaitThatTimesOutAnswersFalseAndLeavesNoSlotTaken()
    {
        Admission admission = new(perStream: 1, perConnection: 1);
        Assert.True(await admission.EnterAsync(1, Long, default));

        Assert.False(await admission.EnterAsync(2, TimeSpan.FromMilliseconds(50), default));
        admission.Leave(1);

        Assert.Equal(0, admission.InFlight);
        Assert.True((await admission.EnterAsync(3, Long, default)));
    }

    [Fact]
    public async Task ACancelledWaitThrowsAndIsSkipped()
    {
        Admission admission = new(perStream: 1, perConnection: 1);
        Assert.True(await admission.EnterAsync(1, Long, default));
        using CancellationTokenSource cancel = new();
        ValueTask<bool> cancelled = admission.EnterAsync(2, Long, cancel.Token);
        ValueTask<bool> next = admission.EnterAsync(3, Long, default);

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cancelled);
        admission.Leave(1);

        Assert.True(await next);
        Assert.Equal(1, admission.InFlight);
    }

    [Fact]
    public async Task AHigherLimitFromANewWelcomeLetsWaitersIn()
    {
        Admission admission = new(perStream: 1, perConnection: 1);
        Assert.True(await admission.EnterAsync(1, Long, default));
        ValueTask<bool> waiting = admission.EnterAsync(1, Long, default);

        admission.SetLimits(perStream: 2, perConnection: 2);

        Assert.True(await waiting);
        Assert.Equal(2, admission.InFlight);
    }

    // Records the stream when admitted, then leaves at once so the next in turn goes in.
    private static async Task Admitted(Admission admission, long stream, List<long> order)
    {
        Assert.True(await admission.EnterAsync(stream, Long, default));
        lock (order)
        {
            order.Add(stream);
        }

        admission.Leave(stream);
    }
}
