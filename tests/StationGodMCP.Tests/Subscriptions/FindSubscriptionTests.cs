#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Subscriptions;
using Xunit;

namespace StationGodMCP.Tests.Subscriptions;

/// <summary>
/// Step 6 (cost design A6): the find topic. A find query is evaluated once for every subscriber of it; each subscriber
/// is sent its first list whole, then what changed (keyed entries added, changed, removed; the rest whole when it
/// changed); evaluations come due on the interval or earlier on world news, cost what they measured, and end with
/// their subscriptions as every topic's do.
/// </summary>
public sealed class FindSubscriptionTests
{
    private static readonly ConnectionId A = new ConnectionId("c1");
    private static readonly ConnectionId B = new ConnectionId("c2");

    private readonly FakeReader _reader = new FakeReader();
    private readonly RecordingEvents _events = new RecordingEvents();
    private readonly FakeNews _news = new FakeNews();
    private readonly FakeFinds _finds;

    public FindSubscriptionTests()
    {
        _finds = new FakeFinds(_news);
    }

    // ---- the text form and the change ----

    [Fact]
    public void AWalksPagesJoinOneListWithoutPagingKeysAndTheFirstPagesCounts()
    {
        RenderedFind rendered = RenderedFind.Of(new[]
        {
            JObject.Parse("""
                {"frames": [{"reference_id": "1"}, {"reference_id": "2"}], "count": 2, "total": 3,
                "has_more": true, "cursor_id": "k1", "total_outer": 3}
                """),
            JObject.Parse("""{"frames": [{"reference_id": "3"}], "count": 1, "total_outer": 99}"""),
        });

        Assert.Equal(new[] { "1", "2", "3" }, rendered.Lists["frames"].Order);
        Assert.Equal("reference_id", rendered.Lists["frames"].Field);
        Assert.Equal("""{"frames":[{"reference_id":"1"},{"reference_id":"2"},{"reference_id":"3"}],"total_outer":3}""",
            rendered.Whole.ToString(Newtonsoft.Json.Formatting.None));
        Assert.Equal("""{"total_outer":3}""", rendered.RestText);
    }

    [Fact]
    public void AListWithoutKeysIsPartOfTheRestAndAnEmptyListIsKeyed()
    {
        RenderedFind rendered = RenderedFind.Of(new[]
        {
            JObject.Parse("""{"items": [{"prefab_name": "a", "quantity": 2}], "pads": [], "gateways": [{"gateway_id": "world"}]}"""),
        });

        Assert.False(rendered.Lists.ContainsKey("items"));
        Assert.True(rendered.Lists.ContainsKey("pads"));
        Assert.Equal("gateway_id", rendered.Lists["gateways"].Field);
        Assert.NotNull(rendered.Rest["items"]);
    }

    [Fact]
    public void TheChangeNamesAddedChangedAndRemovedEntriesAndTheRestOnlyWhenItDiffers()
    {
        RenderedFind before = Rendered("""{"things": [{"reference_id": "1", "d": 0.1}, {"reference_id": "2", "d": 0.2}], "broken": 0}""");
        RenderedFind after = Rendered("""{"things": [{"reference_id": "2", "d": 0.3}, {"reference_id": "4", "d": 0.5}], "broken": 0}""");

        FindChange change = FindChange.Between(before, after)!;

        FindListChange things = change.Lists["things"];
        Assert.Equal("4", (string?)things.Added[0]["reference_id"]);
        Assert.Equal("2", (string?)things.Changed[0]["reference_id"]);
        Assert.Equal(new[] { "1" }, things.Removed);
        Assert.Null(change.Rest);
        Assert.Null(FindChange.Between(after, Rendered("""{"things": [{"reference_id": "2", "d": 0.3}, {"reference_id": "4", "d": 0.5}], "broken": 0}""")));
        Assert.Equal(1, (int)FindChange.Between(after, Rendered("""{"things": [{"reference_id": "2", "d": 0.3}, {"reference_id": "4", "d": 0.5}], "broken": 1}"""))!.Rest!["broken"]!);
    }

    // ---- the slot ----

    [Fact]
    public void ASlotSendsWhatChangedSinceItsLastResultAndNothingForAnEvaluationThatChangedNothing()
    {
        FindSlot slot = new FindSlot(A, new SubscriptionId(1), 0, Result("""{"things": [{"reference_id": "1"}]}"""));

        Assert.True(slot.Offer(Result("""{"things": [{"reference_id": "1"}]}""")));
        Assert.False(slot.TryTake(slot.Epoch, out _));

        Assert.True(slot.Offer(Result("""{"things": [{"reference_id": "1"}, {"reference_id": "2"}]}""")));
        Assert.True(slot.TryTake(slot.Epoch, out FindUpdate update));
        Assert.Equal(1, update.Seq);
        Assert.Single(update.Change!.Lists["things"].Added);
    }

    [Fact]
    public void ASlowSubscriberGetsOneUpdateFromItsLastResultToTheNewest()
    {
        FindSlot slot = new FindSlot(A, new SubscriptionId(1), 0, Result("""{"things": [{"reference_id": "1"}]}"""));

        Assert.True(slot.Offer(Result("""{"things": [{"reference_id": "1"}, {"reference_id": "2"}]}""")));
        Assert.False(slot.Offer(Result("""{"things": [{"reference_id": "2"}, {"reference_id": "3"}]}""")));
        Assert.True(slot.TryTake(slot.Epoch, out FindUpdate update));

        FindListChange things = update.Change!.Lists["things"];
        Assert.Equal(new[] { "2", "3" }, Keys(things.Added));
        Assert.Equal(new[] { "1" }, things.Removed);
        Assert.False(slot.TryTake(slot.Epoch, out _));
    }

    [Fact]
    public void ARequeuedSlotSendsItsNextResultWholeAndItsOldEntryWritesNothing()
    {
        FindSlot slot = new FindSlot(A, new SubscriptionId(1), 0, Result("""{"things": []}"""));
        slot.Offer(Result("""{"things": [{"reference_id": "1"}]}"""));
        int old = slot.Epoch;

        slot.Requeue();
        Assert.False(slot.TryTake(old, out _));
        Assert.True(slot.Offer(Result("""{"things": [{"reference_id": "1"}]}""")));

        Assert.True(slot.TryTake(slot.Epoch, out FindUpdate update));
        Assert.True(update.IsWhole);
    }

    // ---- the engine ----

    [Fact]
    public void TheFirstSubscriberEvaluatesAndAnotherJoinsTheSameQueryWithoutEvaluating()
    {
        SubscriptionEngine<FakeReading> engine = Engine();
        FindQuery query = Query("frame");

        SubscriptionId first = Subscribe(engine, A, query, 10.0, 0.0);
        FindSubscribeOutcome.Subscribed joined = Assert.IsType<FindSubscribeOutcome.Subscribed>(
            engine.SubscribeFind(B, Query("frame"), Interval(10.0), At(0.5, 2)));

        Assert.Equal(1, _finds.Started);
        Assert.Equal(1, engine.FindQueryCount);
        Assert.NotEqual(first, joined.Subscription);
        Assert.Equal(2, engine.CountOf(A) + engine.CountOf(B));
    }

    [Fact]
    public void AQueryIsEvaluatedAgainWhenItsIntervalIsDueAndItsSubscribersHearWhatChanged()
    {
        SubscriptionEngine<FakeReading> engine = Engine();
        Subscribe(engine, A, Query("damaged"), 10.0, 0.0);
        Subscribe(engine, B, Query("damaged"), 10.0, 0.0);
        _finds.Next = """{"things": [{"reference_id": "7"}]}""";

        Assert.False(engine.StepFinds(At(9.9, 10), double.PositiveInfinity, _events));
        Assert.True(engine.StepFinds(At(10.0, 11), double.PositiveInfinity, _events));

        List<FindUpdate> updates = _events.DrainFinds();
        Assert.Equal(2, updates.Count);
        Assert.All(updates, update => Assert.Equal("7", (string?)update.Change!.Lists["things"].Added[0]["reference_id"]));
        Assert.Equal(2, _finds.Started);
    }

    [Fact]
    public void WorldNewsOnItsChannelsBringsAnEvaluationForwardButNotWithinASecondOfTheLast()
    {
        SubscriptionEngine<FakeReading> engine = Engine();
        Subscribe(engine, A, Query("frame"), 60.0, 0.0);

        _news.Moved(FindChannels.Items);
        Assert.False(engine.StepFinds(At(5.0, 2), double.PositiveInfinity, _events));
        _news.Moved(FindChannels.Cells);
        Assert.True(engine.StepFinds(At(5.0, 3), double.PositiveInfinity, _events));
        _news.Moved(FindChannels.Frames);
        Assert.False(engine.StepFinds(At(5.5, 4), double.PositiveInfinity, _events));
        Assert.True(engine.StepFinds(At(6.0, 5), double.PositiveInfinity, _events));
    }

    [Fact]
    public void AnEvaluationsMeasuredCostIsSplitOverItsSubscribersStreams()
    {
        // review D, R-D-25: stream_costs counts a shared find evaluation as a sample of each subscriber's stream
        SubscriptionEngine<FakeReading> engine = Engine();
        Subscribe(engine, A, Query("damaged"), 10.0, 0.0, stream: 4);
        Subscribe(engine, B, Query("damaged"), 10.0, 0.0, stream: 5);
        _finds.CostMs = 6.0;

        Assert.True(engine.StepFinds(At(10.0, 11), double.PositiveInfinity, _events));

        Assert.Equal(new[] { (A, 4L, 3.0), (B, 5L, 3.0) }, _events.Samples);
    }

    [Fact]
    public void AnEvaluationTakesAStepATurnUntilItEnds()
    {
        SubscriptionEngine<FakeReading> engine = Engine();
        Subscribe(engine, A, Query("thing"), 1.0, 0.0);
        _finds.Steps = 3;
        _finds.Next = """{"things": [{"reference_id": "1"}]}""";

        Assert.True(engine.StepFinds(At(1.0, 2), double.PositiveInfinity, _events));
        Assert.True(engine.StepFinds(At(1.0, 3), double.PositiveInfinity, _events));
        Assert.Empty(_events.FindQueue);
        Assert.True(engine.StepFinds(At(1.1, 4), double.PositiveInfinity, _events));

        Assert.Single(_events.FindQueue);
    }

    [Fact]
    public void TheLastSubscriberLeavingDropsTheQueryAndLetsGoOfItsRunningEvaluation()
    {
        SubscriptionEngine<FakeReading> engine = Engine();
        SubscriptionId id = Subscribe(engine, A, Query("thing"), 1.0, 0.0, stream: 4);
        _finds.Steps = 5;
        engine.StepFinds(At(1.0, 2), double.PositiveInfinity, _events);

        engine.DropStream(A, 4);

        Assert.Equal(0, engine.FindQueryCount);
        Assert.True(_finds.LastEvaluation!.Cancelled);
        Assert.False(engine.Unsubscribe(A, id));
    }

    [Fact]
    public void AdmissionProjectsTheFirstEvaluationsMeasuredCostAtTheInterval()
    {
        SubscriptionEngine<FakeReading> engine = Engine();
        _finds.CostMs = 150.0;

        FindSubscribeOutcome.Evaluate first = Assert.IsType<FindSubscribeOutcome.Evaluate>(
            engine.SubscribeFind(A, Query("thing"), Interval(5.0), At(0.0, 1)));
        Run(first.Evaluation);
        SubscriptionRefusal refusal = Assert.IsType<FindSubscribeOutcome.Refused>(engine.CompleteFind(A,
            Query("thing"), Interval(5.0), At(0.0, 1), 0, first.Evaluation.Result!, _events)).Refusal;

        Assert.Equal(SubscriptionLimitKind.ProjectedLoad, refusal.Kind);
        Assert.Equal(30.0, refusal.Requested, 9);
        Assert.Equal(0, engine.FindQueryCount);
        Assert.Equal(0, engine.CountOf(A));
    }

    [Fact]
    public void AFindSubscriptionCountsAgainstTheConnectionsSubscriptions()
    {
        SubscriptionEngine<FakeReading> engine = Engine(new SubscriptionLimits(1, 8192, 100.0));
        Subscribe(engine, A, Query("thing"), 10.0, 0.0);

        Assert.IsType<FindSubscribeOutcome.Refused>(engine.SubscribeFind(A, Query("frame"), Interval(10.0), At(0, 1)));
    }

    [Fact]
    public void AWorldChangeEndsFindSubscriptionsAndDropsTheirQueries()
    {
        SubscriptionEngine<FakeReading> engine = Engine();
        SubscriptionId id = Subscribe(engine, A, Query("thing"), 10.0, 0.0);

        engine.WorldChanged(WorldId.FromBytes(new byte[] { 0, 0, 0, 0, 0, 0, 0, 9 }), _events);

        Assert.Equal((A, id, "world_changed"), (_events.Ended[0].Connection, _events.Ended[0].Subscription,
            _events.Ended[0].End.Reason));
        Assert.Equal(0, engine.FindQueryCount);
    }

    [Fact]
    public void AnEvaluationTheFindRefusesEndsItsSubscriptionsWithReadFailed()
    {
        SubscriptionEngine<FakeReading> engine = Engine();
        Subscribe(engine, A, Query("device"), 1.0, 0.0);
        _finds.Refuse = new FindFailure("gateway_not_found", "gone");

        engine.StepFinds(At(1.0, 2), double.PositiveInfinity, _events);

        Assert.Equal("read_failed", Assert.Single(_events.Ended).End.Reason);
        Assert.Equal(0, engine.CountOf(A));
        Assert.Equal(0, engine.FindQueryCount);
    }

    [Fact]
    public void AResumedConnectionIsSentItsQueriesLatestResultWhole()
    {
        SubscriptionEngine<FakeReading> engine = Engine();
        Subscribe(engine, A, Query("thing"), 10.0, 0.0);
        engine.Pause(A);

        engine.Resume(A, _events);

        FindUpdate update = Assert.Single(_events.DrainFinds());
        Assert.True(update.IsWhole);
    }

    [Fact]
    public void ASubscribersShorterIntervalIsTheQuerysAndLeavingRestoresTheOther()
    {
        SubscriptionEngine<FakeReading> engine = Engine();
        _finds.CostMs = 2.0;
        Subscribe(engine, A, Query("thing"), 10.0, 0.0);
        Assert.Equal(0.2, engine.ProjectedMsPerSecond(), 9);
        SubscriptionId fast = Assert.IsType<FindSubscribeOutcome.Subscribed>(
            engine.SubscribeFind(B, Query("thing"), Interval(2.0), At(0, 1))).Subscription;
        Assert.Equal(1.0, engine.ProjectedMsPerSecond(), 9);

        engine.Unsubscribe(B, fast);

        Assert.Equal(0.2, engine.ProjectedMsPerSecond(), 9);
    }

    // ---- subscribe's params ----

    [Theory]
    [InlineData("""{"topic": "find"}""", "takes what")]
    [InlineData("""{"topic": "find", "what": "ore"}""", "takes what")]
    [InlineData("""{"topic": "find", "what": "frame", "limit": 10}""", "no 'limit'")]
    [InlineData("""{"topic": "find", "what": "frame", "cursor": "open"}""", "no 'cursor'")]
    [InlineData("""{"topic": "find", "what": "device", "fields": ["devices.reference_id"]}""", "no 'fields'")]
    [InlineData("""{"topic": "find", "what": "frame", "interval_s": 0.5}""", "interval_s")]
    [InlineData("""{"topic": "find", "what": "frame", "colour": 3}""", "colour")]
    public void AFindSubscribeIsCheckedAsAFindCallIs(string parameters, string says)
    {
        SubscribeRequest.Invalid invalid = Assert.IsType<SubscribeRequest.Invalid>(
            SubscribeRequest.Of(JObject.Parse(parameters)));

        Assert.Contains(says, invalid.Message);
    }

    [Fact]
    public void AFindSubscribeTakesTheFindsOptionsAndTenSecondsByDefault()
    {
        SubscribeRequest.Find find = Assert.IsType<SubscribeRequest.Find>(SubscribeRequest.Of(JObject.Parse(
            """{"topic": "find", "what": "damaged", "structures_only": true}""")));

        Assert.Equal("damaged", find.Query.Topic.What);
        Assert.Equal(10.0, find.Interval.Seconds);
        Assert.True((bool)find.Query.Options["structures_only"]!);
        Assert.Null(find.Query.Options["what"]);
    }

    [Fact]
    public void TwoQueriesWithTheSameOptionsInAnotherOrderShareOneKey()
    {
        FindQuery one = new FindQuery(FindTopic.Of("item")!, JObject.Parse("""{"prefab": "x", "location": "player"}"""));
        FindQuery two = new FindQuery(FindTopic.Of("item")!, JObject.Parse("""{"location": "player", "prefab": "x"}"""));

        Assert.Equal(one.Key, two.Key);
    }

    // ---- helpers ----

    private SubscriptionEngine<FakeReading> Engine(SubscriptionLimits? limits = null) =>
        new SubscriptionEngine<FakeReading>(_reader, FakeComparer.Instance, limits ?? SubscriptionLimits.Default,
            () => 0.0, _finds, _news);

    private SubscriptionId Subscribe(SubscriptionEngine<FakeReading> engine, ConnectionId connection, FindQuery query,
        double intervalS, double atS, long stream = 0)
    {
        FindSubscribeOutcome outcome = engine.SubscribeFind(connection, query, Interval(intervalS), At(atS, 1), stream);
        if (outcome is FindSubscribeOutcome.Evaluate first)
        {
            Run(first.Evaluation);
            outcome = engine.CompleteFind(connection, query, Interval(intervalS), At(atS, 1), stream,
                first.Evaluation.Result!, _events);
        }

        return Assert.IsType<FindSubscribeOutcome.Subscribed>(outcome).Subscription;
    }

    private static void Run(IFindEvaluation evaluation)
    {
        while (!evaluation.Step(double.PositiveInfinity))
        {
        }
    }

    private static FindQuery Query(string what) => new FindQuery(FindTopic.Of(what)!, new JObject());

    private static SamplingInterval Interval(double seconds)
    {
        Assert.True(SamplingInterval.TryOf(seconds, out SamplingInterval interval));
        return interval;
    }

    private static SamplingTick At(double gameTimeS, long frame) => new SamplingTick(frame, gameTimeS);

    private static RenderedFind Rendered(string json) => RenderedFind.Of(new[] { JObject.Parse(json) });

    private static FindResult Result(string json, double costMs = 0.0, long news = 0) =>
        new FindResult(new object[] { JObject.Parse(json) }, page => (JObject)page, 0.0, 1, costMs, news);

    private static string[] Keys(List<JToken> entries)
    {
        List<string> keys = new List<string>();
        foreach (JToken entry in entries)
        {
            keys.Add((string)entry["reference_id"]!);
        }

        return keys.ToArray();
    }

    /// <summary>Evaluates every query to Next (a find reply) in Steps steps, or refuses it; counts starts.</summary>
    private sealed class FakeFinds : IFindEvaluator
    {
        private readonly FakeNews _news;

        internal FakeFinds(FakeNews news)
        {
            _news = news;
        }

        internal string Next { get; set; } = """{"things": []}""";

        internal int Steps { get; set; } = 1;

        internal double CostMs { get; set; }

        internal FindFailure? Refuse { get; set; }

        internal int Started { get; private set; }

        internal FakeEvaluation? LastEvaluation { get; private set; }

        public IFindEvaluation Start(FindQuery query)
        {
            Started++;
            LastEvaluation = new FakeEvaluation(Next, Steps, CostMs, Refuse, _news.Version(query.Topic.Channels));
            return LastEvaluation;
        }
    }

    private sealed class FakeEvaluation : IFindEvaluation
    {
        private readonly string _reply;
        private readonly double _costMs;
        private readonly FindFailure? _refuse;
        private readonly long _news;
        private int _left;

        internal FakeEvaluation(string reply, int steps, double costMs, FindFailure? refuse, long news)
        {
            _reply = reply;
            _left = steps;
            _costMs = costMs;
            _refuse = refuse;
            _news = news;
        }

        public FindResult? Result { get; private set; }

        public FindFailure? Failure { get; private set; }

        internal bool Cancelled { get; private set; }

        public bool Step(double untilMs)
        {
            if (--_left > 0)
            {
                return false;
            }

            if (_refuse != null)
            {
                Failure = _refuse;
            }
            else
            {
                Result = FindSubscriptionTests.Result(_reply, _costMs, _news);
            }

            return true;
        }

        public void Cancel() => Cancelled = true;
    }

    /// <summary>A counter per channel; Version sums the asked ones.</summary>
    private sealed class FakeNews : IWorldNews
    {
        private readonly Dictionary<FindChannels, long> _counts = new Dictionary<FindChannels, long>();

        internal void Moved(FindChannels channel) =>
            _counts[channel] = (_counts.TryGetValue(channel, out long count) ? count : 0) + 1;

        public long Version(FindChannels channels)
        {
            long version = 0;
            foreach (KeyValuePair<FindChannels, long> count in _counts)
            {
                if ((channels & count.Key) != 0)
                {
                    version += count.Value;
                }
            }

            return version;
        }
    }
}
