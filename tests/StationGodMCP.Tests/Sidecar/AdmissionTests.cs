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

    // perStream 1, perConnection 3; X, Y, Z in flight on 6, 5, 7; B (5), C (7), W (6) wait; B and C give up; X leaves:
    // W must go in, though the lanes of B and C drop out of turn during the same pump.
    [Fact]
    public async Task AWaiterBehindLanesWhoseWaitersAllGaveUpStillGoesIn()
    {
        Admission admission = new(perStream: 1, perConnection: 3);
        Assert.True(await admission.EnterAsync(6, Long, default));
        Assert.True(await admission.EnterAsync(5, Long, default));
        Assert.True(await admission.EnterAsync(7, Long, default));
        Task<bool> b = admission.EnterAsync(5, TimeSpan.FromMilliseconds(50), default).AsTask();
        Task<bool> c = admission.EnterAsync(7, TimeSpan.FromMilliseconds(50), default).AsTask();
        Task<bool> w = admission.EnterAsync(6, Long, default).AsTask();
        Assert.False(await b);
        Assert.False(await c);

        admission.Leave(6);

        Assert.True(await w.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(3, admission.InFlight);
    }

    [Fact]
    public async Task AWaiterCancelledAsItIsAdmittedGivesItsSlotBack()
    {
        for (int round = 0; round < 50; round++)
        {
            Admission admission = new(perStream: 1, perConnection: 1);
            Assert.True(await admission.EnterAsync(1, Long, default));
            using CancellationTokenSource cancel = new();
            ValueTask<bool> waiting = admission.EnterAsync(2, Long, cancel.Token);

            admission.Leave(1);
            cancel.Cancel();

            bool admitted;
            try
            {
                admitted = await waiting;
            }
            catch (OperationCanceledException)
            {
                admitted = false;
            }

            // Either it went in (and holds the slot) or it gave up (and gave the slot back): never a slot lost.
            Assert.Equal(admitted ? 1 : 0, admission.InFlight);
        }
    }

    [Fact]
    public async Task CloseFailsWaitersAndLaterCalls()
    {
        Admission admission = new(perStream: 1, perConnection: 1);
        Assert.True(await admission.EnterAsync(1, Long, default));
        ValueTask<bool> waiting = admission.EnterAsync(2, Long, default);

        admission.Close();

        Assert.False(await waiting);
        Assert.False(await admission.EnterAsync(3, Long, default));
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
