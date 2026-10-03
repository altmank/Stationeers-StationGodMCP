#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.Pure.Subscriptions;
using Xunit;

namespace StationGodMCP.Tests.Subscriptions;

/// <summary>
/// Stage 11's engine: updates only on change with the clock left out, seq without gaps under coalescing, resync,
/// admission and its polling signal, world identity, and sampling on the game clock.
/// </summary>
public sealed class SubscriptionEngineTests
{
    private static readonly ConnectionId A = new ConnectionId("c1");
    private static readonly ConnectionId B = new ConnectionId("c2");

    private readonly FakeReader _reader = new FakeReader();
    private readonly RecordingEvents _events = new RecordingEvents();

    private SubscriptionEngine<FakeReading> Engine(SubscriptionLimits? limits = null) =>
        new SubscriptionEngine<FakeReading>(_reader, FakeComparer.Instance, limits ?? SubscriptionLimits.Default);

    private static SamplingTick At(double gameTimeS, long frame) => new SamplingTick(frame, gameTimeS);

    private static SubscribeOutcome<FakeReading>.Subscribed Subscribed(SubscribeOutcome<FakeReading> outcome) =>
        Assert.IsType<SubscribeOutcome<FakeReading>.Subscribed>(outcome);

    private static SubscriptionRefusal Refused(SubscribeOutcome<FakeReading> outcome) =>
        Assert.IsType<SubscribeOutcome<FakeReading>.Refused>(outcome).Refusal;

    private SubscriptionId Subscribe(SubscriptionEngine<FakeReading> engine, ConnectionId connection,
        double intervalS = 1.0, double atS = 0.0) =>
        Subscribed(engine.Subscribe(connection, Queries.Logic(), Queries.Interval(intervalS), At(atS, 0)))
            .Subscription;

    private int Poll(SubscriptionEngine<FakeReading> engine, double gameTimeS, long frame, ISamplingBudget? budget = null) =>
        engine.Poll(At(gameTimeS, frame), budget ?? CountBudget.Unlimited, _events);

    // ---- subscribing ----

    [Fact]
    public void SubscribeRepliesWithTheFirstReadingAndCounts()
    {
        _reader.Value = 7;
        SubscriptionEngine<FakeReading> engine = Engine();

        SubscribeOutcome<FakeReading>.Subscribed subscribed = Subscribed(
            engine.Subscribe(A, Queries.Logic(items: 3, logicPerItem: 2), Queries.Interval(2.5), At(10.0, 42)));

        Assert.Equal("s1", subscribed.Subscription.ToString());
        Assert.Equal(2.5, subscribed.Interval.Seconds);
        Assert.Equal(6, subscribed.Values);
        Assert.Equal(42, subscribed.Frame);
        Assert.Equal(7, subscribed.Reading!.Value);
        Assert.Equal(1, engine.CountOf(A));
        Assert.Equal(6, engine.ValuesOf(A));
    }

    [Fact]
    public void SubscriptionIdsAreNeverReused()
    {
        SubscriptionEngine<FakeReading> engine = Engine();
        SubscriptionId first = Subscribe(engine, A);
        Assert.True(engine.Unsubscribe(A, first));

        Assert.Equal("s2", Subscribe(engine, B).ToString());
    }

    [Fact]
    public void AReadThatThrowsOnSubscribeLeavesNoSubscription()
    {
        SubscriptionEngine<FakeReading> engine = Engine();
        _reader.Throw = new InvalidOperationException("gateway gone");

        Assert.Throws<InvalidOperationException>(() =>
            engine.Subscribe(A, Queries.Logic(), Queries.Interval(1.0), At(0.0, 0)));
        Assert.Equal(0, engine.CountOf(A));
    }

    // ---- updates only on change ----

    [Fact]
    public void AnUnchangedReadingSendsNothingThoughTheClockMoves()
    {
        SubscriptionEngine<FakeReading> engine = Engine();
        Subscribe(engine, A);

        for (int second = 1; second <= 5; second++)
        {
            Poll(engine, second, second);
        }

        Assert.Equal(6, _reader.Reads);
        Assert.Empty(_events.Queue);
    }

    [Fact]
    public void AChangeSendsTheWholeReadingWithSeqOne()
    {
        SubscriptionEngine<FakeReading> engine = Engine();
        SubscriptionId id = Subscribe(engine, A);
        _reader.Value = 1;

        Poll(engine, 1.0, 7);

        SubscriptionUpdate<FakeReading> update = Assert.Single(_events.Drain());
        Assert.Equal(id, update.Subscription);
        Assert.Equal(1, update.Seq);
        Assert.Equal(1, update.Reading.Value);
        Assert.Equal(7, update.Frame);
        Assert.Equal(1.0, update.GameTimeS);
    }

    [Fact]
    public void AChangeBackIsAChangeToo()
    {
        SubscriptionEngine<FakeReading> engine = Engine();
        Subscribe(engine, A);
        _reader.Value = 1;
        Poll(engine, 1.0, 1);
        List<SubscriptionUpdate<FakeReading>> sent = _events.Drain();
        _reader.Value = 0;
        Poll(engine, 2.0, 2);
        sent.AddRange(_events.Drain());

        Assert.Equal(new long[] { 1, 2 }, new[] { sent[0].Seq, sent[1].Seq });
        Assert.Equal(0, sent[1].Reading.Value);
    }

    // ---- seq, coalescing, gaps ----

    [Fact]
    public void ASlowClientGetsOneFreshUpdateThatKeepsItsSeq()
    {
        SubscriptionEngine<FakeReading> engine = Engine();
        Subscribe(engine, A);
        for (int second = 1; second <= 20; second++)
        {
            _reader.Value = second;
            Poll(engine, second, second);
        }

        UpdateSlot<FakeReading> queued = Assert.Single(_events.Queue);
        Assert.True(queued.TryTake(out SubscriptionUpdate<FakeReading> update));
        Assert.Equal(1, update.Seq);
        Assert.Equal(20, update.Reading.Value);
        Assert.Equal(20, update.Frame);
    }

    [Fact]
    public void SeqHasNoGapAcrossMergedUpdates()
    {
        SubscriptionEngine<FakeReading> engine = Engine();
        Subscribe(engine, A);
        List<long> seen = new List<long>();
        for (int second = 1; second <= 30; second++)
        {
            _reader.Value = second;
            Poll(engine, second, second);
            if (second % 4 == 0)
            {
                // The client reads its pipe every fourth second; the updates in between merge.
                foreach (SubscriptionUpdate<FakeReading> update in _events.Drain())
                {
                    seen.Add(update.Seq);
                }
            }
        }

        for (int index = 0; index < seen.Count; index++)
        {
            Assert.Equal(index + 1, seen[index]);
        }

        Assert.Equal(7, seen.Count);
    }

    [Fact]
    public void ComparisonIsWithTheLastReadingOfferedNotTheLastWritten()
    {
        SubscriptionEngine<FakeReading> engine = Engine();
        Subscribe(engine, A);
        _reader.Value = 5;
        Poll(engine, 1.0, 1);
        Poll(engine, 2.0, 2);

        Assert.Single(_events.Queue);
        Assert.Single(_events.Drain());
        Poll(engine, 3.0, 3);
        Assert.Empty(_events.Queue);
    }

    // ---- resync ----

    [Fact]
    public void ResyncSendsTheNextReadingUnchangedAtOnce()
    {
        SubscriptionEngine<FakeReading> engine = Engine();
        SubscriptionId id = Subscribe(engine, A, intervalS: 10.0);

        Assert.True(engine.Resync(A, id));
        Poll(engine, 0.5, 1);

        SubscriptionUpdate<FakeReading> update = Assert.Single(_events.Drain());
        Assert.Equal(1, update.Seq);
        Assert.Equal(0.0, update.LateMs);
    }

    [Fact]
    public void AfterResyncOnlyChangesAreSentAgain()
    {
        SubscriptionEngine<FakeReading> engine = Engine();
        SubscriptionId id = Subscribe(engine, A);
        engine.Resync(A, id);
        Poll(engine, 0.1, 1);
        _events.Drain();

        Assert.Equal(1, Poll(engine, 1.2, 2));
        Assert.Equal(1, Poll(engine, 2.3, 3));

        Assert.Empty(_events.Queue);
    }

    [Fact]
    public void ResyncOfAnUnknownSubscriptionIsRefused()
    {
        SubscriptionEngine<FakeReading> engine = Engine();
        SubscriptionId id = Subscribe(engine, A);

        Assert.False(engine.Resync(B, id));
        Assert.False(engine.Resync(A, new SubscriptionId(99)));
    }

    // ---- admission and the polling signal ----

    [Fact]
    public void The65thSubscriptionOnAConnectionIsRefused()
    {
        SubscriptionEngine<FakeReading> engine = Engine(new SubscriptionLimits(64, 8192, 100.0));
        for (int index = 0; index < 64; index++)
        {
            Subscribe(engine, A);
        }

        SubscriptionRefusal refusal = Refused(engine.Subscribe(A, Queries.Logic(), Queries.Interval(1.0), At(0, 0)));

        Assert.Equal(SubscriptionLimitKind.SubscriptionsPerConnection, refusal.Kind);
        Assert.Equal("max_subscriptions", refusal.Name);
        Assert.Equal(64, refusal.Maximum);
        Assert.Equal(65, refusal.Requested);
        Assert.Equal("subscription_limit", SubscriptionRefusal.ErrorCode);
        Subscribe(engine, B);
    }

    [Fact]
    public void The8193rdValueOnAConnectionIsRefused()
    {
        SubscriptionEngine<FakeReading> engine = Engine(new SubscriptionLimits(64, 8192, 100.0));
        for (int index = 0; index < 8; index++)
        {
            Subscribed(engine.Subscribe(A, Queries.Logic(items: 16, logicPerItem: 64), Queries.Interval(1.0),
                At(0, 0)));
        }

        Assert.Equal(8192, engine.ValuesOf(A));
        SubscriptionRefusal refusal = Refused(engine.Subscribe(A, Queries.Logic(), Queries.Interval(1.0), At(0, 0)));

        Assert.Equal(SubscriptionLimitKind.ValuesPerConnection, refusal.Kind);
        Assert.Equal("max_subscription_values", refusal.Name);
        Assert.Equal(8193, refusal.Requested);
    }

    [Fact]
    public void ValuesFreedByUnsubscribingCanBeTakenAgain()
    {
        SubscriptionEngine<FakeReading> engine = Engine(new SubscriptionLimits(64, 100, 100.0));
        SubscriptionId first = Subscribed(engine.Subscribe(A, Queries.Logic(items: 2, logicPerItem: 50),
            Queries.Interval(1.0), At(0, 0))).Subscription;
        Refused(engine.Subscribe(A, Queries.Logic(), Queries.Interval(1.0), At(0, 0)));

        engine.Unsubscribe(A, first);

        Subscribed(engine.Subscribe(A, Queries.Logic(), Queries.Interval(1.0), At(0, 0)));
        Assert.Equal(1, engine.ValuesOf(A));
    }

    [Fact]
    public void TheProjectedLoadRefusesWithTheProjection()
    {
        // Default limits: 1.5 ms lane share, half of it at 30 frames a second = 22.5 ms per game second.
        SubscriptionEngine<FakeReading> engine = Engine();
        Assert.Equal(22.5, engine.Limits.MaxProjectedMsPerSecond, 9);
        // 1,024 values at 0.005 ms = 5.12 ms per sample; every 0.5 s = 10.24 ms per second.
        DeviceSubscriptionQuery heavy = Queries.Logic(items: 16, logicPerItem: 64);
        Subscribed(engine.Subscribe(A, heavy, Queries.Interval(0.5), At(0, 0)));
        Subscribed(engine.Subscribe(B, heavy, Queries.Interval(0.5), At(0, 0)));

        SubscriptionRefusal refusal = Refused(engine.Subscribe(A, heavy, Queries.Interval(0.5), At(0, 0)));

        Assert.Equal(SubscriptionLimitKind.ProjectedLoad, refusal.Kind);
        Assert.Equal(30.72, refusal.Requested, 9);
        Assert.Equal(30.72, refusal.ProjectedMsPerSecond, 9);
        Assert.Equal(22.5, refusal.Maximum, 9);
        Subscribed(engine.Subscribe(A, heavy, Queries.Interval(5.0), At(0, 0)));
    }

    [Fact]
    public void ABudgetOfZeroTurnsSubscriptionsOff()
    {
        SubscriptionEngine<FakeReading> engine = Engine(SubscriptionLimits.From(0.0, 4.0));

        SubscriptionRefusal refusal = Refused(engine.Subscribe(A, Queries.Logic(), Queries.Interval(1.0), At(0, 0)));

        Assert.Equal(SubscriptionLimitKind.SubscriptionsOff, refusal.Kind);
        Assert.Equal(0, _reader.Reads);
    }

    [Theory]
    [InlineData(1.5, 4.0, 1.5)]
    [InlineData(1.5, 2.0, 1.0)]
    [InlineData(3.0, 0.0, 3.0)]
    public void TheLaneShareIsAtMostHalfTheRequestBudget(double subscriptionMs, double requestMs, double share)
    {
        Assert.Equal(share, SubscriptionLimits.From(subscriptionMs, requestMs).LaneShareMs);
    }

    [Fact]
    public void ARefusedSubscribeReadsNothing()
    {
        SubscriptionEngine<FakeReading> engine = Engine(new SubscriptionLimits(0, 8192, 1.5));

        Refused(engine.Subscribe(A, Queries.Logic(), Queries.Interval(1.0), At(0, 0)));

        Assert.Equal(0, _reader.Reads);
    }

    [Fact]
    public void LoweredLimitsEndTheNewestSubscriptionsWithReasonLimit()
    {
        SubscriptionEngine<FakeReading> engine = Engine(new SubscriptionLimits(64, 8192, 100.0));
        SubscriptionId first = Subscribe(engine, A);
        SubscriptionId second = Subscribe(engine, A);
        SubscriptionId third = Subscribe(engine, A);

        engine.ApplyLimits(new SubscriptionLimits(1, 8192, 100.0), _events);

        Assert.Equal(new[] { third, second }, new[] { _events.Ended[0].Subscription, _events.Ended[1].Subscription });
        Assert.All(_events.Ended, ended => Assert.Equal("limit", ended.End.Reason));
        Assert.True(engine.Unsubscribe(A, first));
    }

    // ---- unsubscribe, connections ----

    [Fact]
    public void UnsubscribeOfAnotherConnectionsSubscriptionIsUnknown()
    {
        SubscriptionEngine<FakeReading> engine = Engine();
        SubscriptionId id = Subscribe(engine, A);

        Assert.False(engine.Unsubscribe(B, id));
        Assert.True(engine.Unsubscribe(A, id));
        Assert.False(engine.Unsubscribe(A, id));
    }

    [Fact]
    public void AnUpdateWaitingWhenTheSubscriptionEndsIsDropped()
    {
        SubscriptionEngine<FakeReading> engine = Engine();
        SubscriptionId id = Subscribe(engine, A);
        _reader.Value = 3;
        Poll(engine, 1.0, 1);

        engine.Unsubscribe(A, id);

        Assert.Empty(_events.Drain());
    }

    [Fact]
    public void AClosedConnectionsSubscriptionsEndSilently()
    {
        SubscriptionEngine<FakeReading> engine = Engine();
        Subscribe(engine, A);
        Subscribe(engine, B);

        engine.DropConnection(A);
        Poll(engine, 1.0, 1);

        Assert.Equal(0, engine.CountOf(A));
        Assert.Empty(_events.Ended);
        Assert.Equal(3, _reader.Reads);
    }

    [Fact]
    public void AReadThatThrowsWhileSamplingEndsOnlyThatSubscription()
    {
        SubscriptionEngine<FakeReading> engine = Engine();
        SubscriptionId id = Subscribe(engine, A);
        InvalidOperationException failure = new InvalidOperationException("game changed");
        _reader.Throw = failure;

        Poll(engine, 1.0, 1);

        (_, SubscriptionId subscription, SubscriptionEnd end) = Assert.Single(_events.Ended);
        Assert.Equal(id, subscription);
        Assert.Same(failure, Assert.IsType<SubscriptionEnd.ReadFailed>(end).Failure);
        Assert.Equal(0, engine.CountOf(A));
    }

    // ---- world identity ----

    [Fact]
    public void ANewWorldEndsEveryDevicesSubscriptionWithWorldChanged()
    {
        SubscriptionEngine<FakeReading> engine = Engine();
        Subscribe(engine, A);
        Subscribe(engine, B);
        WorldId world = new ScriptedWorldIds().Next();

        engine.WorldChanged(world, _events);
        _reader.Value = 9;
        Poll(engine, 1.0, 1);

        Assert.Equal(2, _events.Ended.Count);
        Assert.All(_events.Ended, ended => Assert.Equal("world_changed", ended.End.Reason));
        Assert.Empty(_events.Queue);
        Assert.Equal(0, engine.CountOf(A) + engine.CountOf(B));
    }

    [Fact]
    public void WorldTopicSubscriptionsHearOfTheNewWorldAndStay()
    {
        SubscriptionEngine<FakeReading> engine = Engine();
        SubscriptionId world = Subscribed(engine.SubscribeWorld(A, At(0, 0))).Subscription;
        WorldId next = new ScriptedWorldIds().Next();

        engine.WorldChanged(next, _events);

        Assert.Equal((A, world, next), Assert.Single(_events.Worlds));
        Assert.Empty(_events.Ended);
        Assert.Equal(1, engine.CountOf(A));
    }

    [Fact]
    public void WorldTopicSubscriptionsHearEachGameStateChangeOnce()
    {
        SubscriptionEngine<FakeReading> engine = Engine();
        Subscribed(engine.SubscribeWorld(A, At(0, 0)));

        engine.ObserveGameState("Running", _events);
        engine.ObserveGameState("Running", _events);
        engine.ObserveGameState("Paused", _events);

        Assert.Equal(new[] { "Running", "Paused" }, _events.States.ConvertAll(state => state.State).ToArray());
    }

    // ---- timing on the game clock ----

    [Fact]
    public void ASampleIsTakenInTheFirstFrameOnOrAfterItIsDue()
    {
        SubscriptionEngine<FakeReading> engine = Engine();
        Subscribe(engine, A, intervalS: 1.0, atS: 0.0);

        Assert.Equal(0, Poll(engine, 0.98, 1));
        Assert.Equal(1, Poll(engine, 1.02, 2));
        Assert.Equal(0, Poll(engine, 1.5, 3));
        Assert.Equal(1, Poll(engine, 2.0, 4));
    }

    [Fact]
    public void APausedGameSamplesNothing()
    {
        SubscriptionEngine<FakeReading> engine = Engine();
        Subscribe(engine, A, intervalS: 0.5, atS: 3.0);

        // Time.time stands still while paused; frames keep coming.
        for (long frame = 1; frame <= 100; frame++)
        {
            Poll(engine, 3.2, frame);
        }

        Assert.Equal(1, _reader.Reads);
    }

    [Fact]
    public void LateSamplesReportLatenessAndKeepTheGrid()
    {
        SubscriptionEngine<FakeReading> engine = Engine();
        Subscribe(engine, A, intervalS: 1.0);
        _reader.Value = 1;

        Poll(engine, 1.25, 1);
        Assert.Equal(250.0, Assert.Single(_events.Drain()).LateMs, 6);

        _reader.Value = 2;
        Assert.Equal(0, Poll(engine, 1.9, 2));
        Assert.Equal(1, Poll(engine, 2.0, 3));
        Assert.Equal(0.0, Assert.Single(_events.Drain()).LateMs, 6);
    }

    [Fact]
    public void AFrameThatMissedSeveralIntervalsTakesOneSample()
    {
        SubscriptionEngine<FakeReading> engine = Engine();
        Subscribe(engine, A, intervalS: 1.0);

        Assert.Equal(1, Poll(engine, 5.5, 1));
        Assert.Equal(0, Poll(engine, 5.9, 2));
        Assert.Equal(1, Poll(engine, 6.0, 3));
    }

    [Theory]
    [InlineData(1.0, 1.0, 1.5, 2.0)]
    [InlineData(1.0, 1.0, 1.0, 2.0)]
    [InlineData(1.0, 0.5, 2.6, 3.0)]
    [InlineData(1.0, 0.5, 3.0, 3.5)]
    public void NextDueIsTheNextGridPointAfterNow(double dueS, double intervalS, double nowS, double expected)
    {
        Assert.Equal(expected, SubscriptionEngine<FakeReading>.NextDue(dueS, intervalS, nowS), 9);
    }

    // ---- the lane budget and fairness ----

    [Fact]
    public void TheBudgetStopsSamplingAndTheRestRunsNextFrameOldestFirst()
    {
        SubscriptionEngine<FakeReading> engine = Engine();
        for (int index = 0; index < 5; index++)
        {
            Subscribe(engine, A);
        }

        Assert.Equal(2, Poll(engine, 1.0, 1, new CountBudget(2)));
        Assert.Equal(3, Poll(engine, 1.1, 2));
        Assert.Equal(0, Poll(engine, 1.2, 3));
        Assert.Equal(5, Poll(engine, 2.0, 4));
    }

    [Fact]
    public void ConnectionsTakeTurnsSoOneWithManySubscriptionsCannotCrowdOutAnother()
    {
        SubscriptionEngine<FakeReading> engine = Engine();
        for (int index = 0; index < 16; index++)
        {
            Subscribe(engine, A);
        }

        SubscriptionId lone = Subscribe(engine, B);
        _reader.Value = 1;

        Poll(engine, 1.0, 1, new CountBudget(2));

        Assert.Contains(_events.Queue, slot => slot.Subscription.Equals(lone));
    }

    [Fact]
    public void TheConnectionTheBudgetStoppedAtGoesFirstNextFrame()
    {
        SubscriptionEngine<FakeReading> engine = Engine();
        Subscribe(engine, A);
        Subscribe(engine, A);
        SubscriptionId b = Subscribe(engine, B);
        _reader.Value = 1;

        Poll(engine, 1.0, 1, new CountBudget(1));
        Poll(engine, 1.1, 2, new CountBudget(1));

        Assert.Equal(2, _events.Queue.Count);
        Assert.Equal(b, _events.Queue[1].Subscription);
    }

    [Fact]
    public void TheLaneBudgetAlwaysTakesTheFirstSampleThenStopsAtItsShare()
    {
        double spent = 5.0;
        LaneSamplingBudget budget = new LaneSamplingBudget(1.5, () => spent);

        Assert.True(budget.MayTakeAnother());
        Assert.False(budget.MayTakeAnother());

        spent = 0.2;
        Assert.True(new LaneSamplingBudget(1.5, () => spent).MayTakeAnother());
        Assert.True(new LaneSamplingBudget(0.0, () => spent).MayTakeAnother());
    }
}
