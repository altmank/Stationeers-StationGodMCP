#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Sampling;
using StationGodMCP.Pure.Subscriptions;
using StationGodMCP.Pure.Verbs;
using StationGodMCP.Tests.Subscriptions;
using Xunit;

namespace StationGodMCP.Tests.Sampling;

/// <summary>
/// read sample's sampler: its request from the verb's options, and the sidecar loop's schedule, change list, counts and
/// rounding, on the real clock, sampled in the first frame at or after each due time; the reply in the catalogue's shape.
/// </summary>
public sealed class LogicSamplerTests
{
    private static readonly ConnectionId A = new ConnectionId("c1");
    private static readonly DateTimeOffset Utc = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Sample k of target t reads Values(t, k); counts samples; fails sample FailAt with gateway_not_found.</summary>
    private sealed class ScriptedReader : ILogicSampleReader<BatchItemView>
    {
        private readonly Func<int, int, double> _values;

        internal ScriptedReader(Func<int, int, double> values)
        {
            _values = values;
        }

        internal int Samples { get; private set; }

        internal int FailAt { get; set; } = -1;

        internal Func<int, int, bool> Failing { get; set; } = (_, _) => false;

        /// <summary>Target t's thing is gone at sample k: its reading a failure and a miss in the read.</summary>
        internal Func<int, int, bool> Gone { get; set; } = (_, _) => false;

        public LogicSampleRead Read(SampleRequest arguments, List<BatchItemView> into)
        {
            int sample = Samples++;
            if (sample == FailAt)
            {
                return LogicSampleRead.Failed("gateway_not_found", "No gateway gw9.");
            }

            List<SampleMiss> misses = new List<SampleMiss>();
            for (int target = 0; target < arguments.Targets.Count; target++)
            {
                ThingId id = new ThingId(100 + target);
                LogicTypeView type = new LogicTypeView(28, "On");
                if (Gone(target, sample))
                {
                    into.Add(new LogicFailedItemView(target, id, type, new ApiException("thing_not_found", "Gone.")));
                    misses.Add(new SampleMiss(target, "thing_not_found", "Gone."));
                    continue;
                }

                into.Add(Failing(target, sample)
                    ? new LogicFailedItemView(target, id, type, new ApiException("logic_not_readable", "No."))
                    : new LogicReadItemView(target, new LogicRead(id, type, _values(target, sample))));
            }

            return LogicSampleRead.Read(arguments.Gateway ?? "world", misses);
        }
    }

    private sealed class Outcomes : ISampleEvents<BatchItemView>
    {
        internal List<SampleOutcome<BatchItemView>> Finished { get; } = new List<SampleOutcome<BatchItemView>>();

        void ISampleEvents<BatchItemView>.Finished(SampleRun<BatchItemView> run,
            SampleOutcome<BatchItemView> outcome) => Finished.Add(outcome);
    }

    private static SampleRequest Arguments(int targets = 1, double duration = 1.0, double interval = 0.05,
        string? gateway = null) => SampleRequests.Of(targets, duration, interval, gateway);

    /// <summary>Runs one call to its end with a frame every frameS real seconds from startS; returns its outcome.</summary>
    private static SampleOutcome<BatchItemView> Run(ScriptedReader reader, SampleRequest arguments,
        double frameS, double startS = 0.0, Func<long, bool>? frameSkipped = null)
    {
        LogicSampler<BatchItemView> sampler = new LogicSampler<BatchItemView>(reader, LogicResultComparer.Instance);
        Outcomes outcomes = new Outcomes();
        sampler.Start(A, arguments, startS);
        for (long frame = 0; frame < 1_000_000 && outcomes.Finished.Count == 0; frame++)
        {
            if (frameSkipped != null && frameSkipped(frame))
            {
                continue;
            }

            sampler.Poll(new RealTimeTick(frame, startS + frame * frameS, Utc.AddSeconds(frame * frameS)),
                CountBudget.Unlimited, outcomes);
        }

        Assert.Equal(0, sampler.Active);
        return Assert.Single(outcomes.Finished);
    }

    private static SampleResult<BatchItemView> Completed(SampleOutcome<BatchItemView> outcome) =>
        Assert.IsType<SampleOutcome<BatchItemView>.Completed>(outcome).Result;

    // ---- the sidecar loop, as a model ----

    /// <summary>
    /// The sidecar's loop with reads that take no time and exact delays (Program.cs SampleLogicAsync): a read, then
    /// remaining = duration - elapsed, stop when it is not positive, otherwise wait min(interval, remaining). Changes
    /// compare each result's JSON text with the last one of its index.
    /// </summary>
    private static (int SampleCount, List<(double Elapsed, List<string> Readings)> Changes, double Duration)
        SidecarLoop(ScriptedReader reader, SampleRequest arguments)
    {
        Dictionary<int, string> previous = new Dictionary<int, string>();
        List<(double, List<string>)> changes = new List<(double, List<string>)>();
        int sampleCount = 0;
        double elapsed = 0.0;
        while (true)
        {
            List<BatchItemView> results = new List<BatchItemView>();
            reader.Read(arguments, results);
            List<string> changed = new List<string>();
            foreach (BatchItemView reading in results)
            {
                string serialized = ApiJson.WriteShared(reading);
                if (!previous.TryGetValue(reading.Index, out string? prior) || prior != serialized)
                {
                    previous[reading.Index] = serialized;
                    changed.Add(serialized);
                }
            }

            if (changed.Count > 0)
            {
                changes.Add((sampleCount == 0 ? 0d : Math.Round(elapsed, 3), changed));
            }

            sampleCount++;
            double remaining = arguments.Seconds - elapsed;
            if (remaining <= 1e-9)
            {
                break;
            }

            elapsed += Math.Min(arguments.Interval, remaining);
        }

        return (sampleCount, changes, Math.Round(elapsed, 3));
    }

    public static IEnumerable<object[]> Sequences()
    {
        // Toggles every 0.25 s; a steady target; NaN; 0 and -0; a target that fails for a while.
        yield return new object[] { 1.0, 0.05, 3 };
        yield return new object[] { 5.0, 0.5, 3 };
        yield return new object[] { 0.1, 0.05, 2 };
        yield return new object[] { 1.0, 0.3, 4 };
        yield return new object[] { 5.9, 0.05, 5 };
    }

    private static double Value(int target, int sample) => target switch
    {
        0 => sample / 5 % 2,
        1 => 7.0,
        2 => sample % 3 == 0 ? double.NaN : 1.0,
        3 => sample % 2 == 0 ? 0.0 : -0.0,
        _ => sample,
    };

    private static bool Failing(int target, int sample) => target == 1 && sample >= 4 && sample < 9;

    [Theory]
    [MemberData(nameof(Sequences))]
    public void ChangesCountsAndRoundingEqualTheSidecarLoops(double duration, double interval, int targets)
    {
        SampleRequest arguments = Arguments(targets, duration, interval);
        (int sampleCount, List<(double Elapsed, List<string> Readings)> changes, double loopDuration) =
            SidecarLoop(new ScriptedReader(Value) { Failing = Failing }, arguments);

        // Frames of a millisecond from an odd start, so every due time lands on a frame.
        SampleResult<BatchItemView> result = Completed(
            Run(new ScriptedReader(Value) { Failing = Failing }, arguments, frameS: 0.001, startS: 1234.5));

        Assert.Equal(sampleCount, result.SampleCount);
        Assert.Equal(changes.Count, result.ChangeCount);
        Assert.Equal(loopDuration, result.DurationSeconds);
        for (int index = 0; index < changes.Count; index++)
        {
            Assert.Equal(changes[index].Elapsed, result.Changes[index].ElapsedSeconds);
            List<string> mine = result.Changes[index].Readings.ConvertAll(reading => ApiJson.WriteShared(reading));
            Assert.Equal(changes[index].Readings, mine);
        }
    }

    [Fact]
    public void AnIntervalOfFiftyMillisecondsIsKeptAtSixtyFramesASecond()
    {
        SampleResult<BatchItemView> result = Completed(Run(new ScriptedReader((_, sample) => sample),
            Arguments(duration: 1.0, interval: 0.05), frameS: 1.0 / 60.0));

        Assert.Equal(21, result.SampleCount);
        Assert.Equal(21, result.ChangeCount);
        for (int index = 1; index < result.Changes.Count; index++)
        {
            double step = result.Changes[index].ElapsedSeconds - result.Changes[index - 1].ElapsedSeconds;
            Assert.InRange(step, 0.05 - 1.0 / 60.0, 0.05 + 1.0 / 60.0);
        }

        Assert.InRange(result.Changes[10].ElapsedSeconds, 0.5, 0.5 + 1.0 / 60.0);
    }

    [Fact]
    public void SlowFramesTakeOneSampleEachAndNeverABurst()
    {
        // A frame every 0.2 s against a 0.05 s interval: one sample per frame, the last at the duration.
        SampleResult<BatchItemView> result = Completed(Run(new ScriptedReader((_, sample) => sample),
            Arguments(duration: 1.0, interval: 0.05), frameS: 0.2));

        Assert.Equal(6, result.SampleCount);
        Assert.Equal(new[] { 0.0, 0.2, 0.4, 0.6, 0.8, 1.0 },
            result.Changes.ConvertAll(change => change.ElapsedSeconds).ToArray());
    }

    [Fact]
    public void AStallLongerThanTheDurationEndsTheRunWithItsNextSample()
    {
        // Frames 1 to 99 never come (the game hung for two seconds): the sample after the stall is the last.
        SampleResult<BatchItemView> result = Completed(Run(new ScriptedReader((_, sample) => sample),
            Arguments(duration: 1.0, interval: 0.05), frameS: 0.02, frameSkipped: frame => frame > 0 && frame < 100));

        Assert.Equal(2, result.SampleCount);
        Assert.Equal(2.0, result.DurationSeconds);
    }

    [Fact]
    public void TheFirstSampleIsAtZeroAndCarriesEveryReading()
    {
        SampleResult<BatchItemView> result = Completed(Run(new ScriptedReader((_, _) => 1.0),
            Arguments(targets: 3, duration: 0.5, interval: 0.1), frameS: 0.01, startS: 50.0));

        SampleChange<BatchItemView> first = Assert.Single(result.Changes);
        Assert.Equal(0.0, first.ElapsedSeconds);
        Assert.Equal(3, first.Readings.Count);
        Assert.Equal(6, result.SampleCount);
        Assert.Equal(Utc, result.StartedAtUtc);
        Assert.Equal("world", result.GatewayId);
        Assert.Equal(0.1, result.IntervalSeconds);
    }

    [Fact]
    public void AFailedReadFailsTheCallWithItsError()
    {
        SampleOutcome<BatchItemView> outcome = Run(new ScriptedReader((_, _) => 1.0) { FailAt = 3 },
            Arguments(), frameS: 0.01);

        SampleOutcome<BatchItemView>.Failed failed = Assert.IsType<SampleOutcome<BatchItemView>.Failed>(outcome);
        Assert.Equal("gateway_not_found", failed.Code);
        Assert.Equal("No gateway gw9.", failed.Message);
    }

    [Fact]
    public void ATargetGoneMidRunEndsItsOwnSeriesAndTheRestGoOn()
    {
        ScriptedReader reader = new ScriptedReader((_, sample) => sample)
        {
            Gone = (target, sample) => target == 1 && sample >= 3
        };

        SampleResult<BatchItemView> result = Completed(Run(reader,
            Arguments(targets: 2, duration: 0.5, interval: 0.1), frameS: 0.01));

        Assert.Equal(6, result.SampleCount);
        Assert.Equal(6, result.ChangeCount);
        List<BatchItemView> readings = result.Changes.SelectMany(change => change.Readings).ToList();
        Assert.All(readings, reading => Assert.IsType<LogicReadItemView>(reading));
        Assert.Equal(6, readings.Count(reading => reading.Index == 0));
        Assert.Equal(3, readings.Count(reading => reading.Index == 1));
    }

    [Fact]
    public void TheCheckReadsEveryTargetOnceAndNamesEveryMiss()
    {
        ScriptedReader reader = new ScriptedReader((_, _) => 1.0) { Gone = (target, _) => target != 1 };
        LogicSampler<BatchItemView> sampler = new LogicSampler<BatchItemView>(reader, LogicResultComparer.Instance);

        LogicSampleRead check = sampler.Check(Arguments(targets: 3));

        Assert.True(check.Ok);
        Assert.Equal(new[] { 0, 2 }, check.Misses.Select(miss => miss.Index));
        Assert.Equal(1, reader.Samples);
        Assert.Equal(0, sampler.Active);
    }

    [Fact]
    public void TheMostOverdueRunGoesFirstWhenTheBudgetIsShort()
    {
        ScriptedReader reader = new ScriptedReader((_, _) => 1.0);
        LogicSampler<BatchItemView> sampler = new LogicSampler<BatchItemView>(reader, LogicResultComparer.Instance);
        Outcomes outcomes = new Outcomes();
        SampleRun<BatchItemView> early = sampler.Start(A, Arguments(interval: 0.1), 0.0);
        sampler.Poll(new RealTimeTick(0, 0.0, Utc), new CountBudget(1), outcomes);
        SampleRun<BatchItemView> late = sampler.Start(A, Arguments(interval: 0.1), 0.12);

        sampler.Poll(new RealTimeTick(1, 0.15, Utc), new CountBudget(1), outcomes);

        Assert.Equal(2, early.SampleCount);
        Assert.Equal(0, late.SampleCount);
        sampler.Poll(new RealTimeTick(2, 0.16, Utc), new CountBudget(1), outcomes);
        Assert.Equal(1, late.SampleCount);
    }

    [Fact]
    public void ARunSamplesAtMostOncePerFrame()
    {
        ScriptedReader reader = new ScriptedReader((_, _) => 1.0);
        LogicSampler<BatchItemView> sampler = new LogicSampler<BatchItemView>(reader, LogicResultComparer.Instance);
        sampler.Start(A, Arguments(), 0.0);

        Assert.Equal(1, sampler.Poll(new RealTimeTick(0, 0.0, Utc), CountBudget.Unlimited, new Outcomes()));
        Assert.Equal(0, sampler.Poll(new RealTimeTick(0, 0.0, Utc), CountBudget.Unlimited, new Outcomes()));
    }

    [Fact]
    public void ClosingTheConnectionDropsItsRuns()
    {
        LogicSampler<BatchItemView> sampler = new LogicSampler<BatchItemView>(new ScriptedReader((_, _) => 1.0),
            LogicResultComparer.Instance);
        sampler.Start(A, Arguments(), 0.0);
        SampleRun<BatchItemView> other = sampler.Start(new ConnectionId("c2"), Arguments(), 0.0);

        sampler.CancelConnection(A);

        Assert.Equal(1, sampler.Active);
        Assert.True(sampler.Cancel(other));
        Assert.Equal(0, sampler.Active);
    }

    // ---- the reply on the wire ----

    [Fact]
    public void TheReplyIsTheCataloguesShapeWithARowPerChangedValue()
    {
        SampleResult<BatchItemView> result = Completed(Run(new ScriptedReader((_, sample) => sample),
            Arguments(duration: 0.1, interval: 0.05, gateway: "gw1"), frameS: 0.05));

        JObject json = Replies.Tree(StationGodMCP.Protocol.SampleReply.Of(result));

        List<string> keys = new List<string>();
        foreach (JProperty property in json.Properties())
        {
            keys.Add(property.Name);
        }

        List<string> declared = new List<string>();
        foreach (KeyValuePair<string, System.Text.Json.Nodes.JsonNode?> key in Verbs.VerbCatalogue.Verb("read")["whats"]!
                     ["sample"]!["reply"]!["variants"]![0]!["keys"]!.AsObject())
        {
            declared.Add(key.Key);
        }

        Assert.Equal(declared, keys);
        Assert.Equal("gw1", (string?)json["gateway_id"]);
        Assert.Equal(3, (int)json["sample_count"]!);
        Assert.Equal(3, (int)json["change_count"]!);
        Assert.Equal("""{"reference_id":"100","logic_type":"On","value":1.0,"at_s":0.05}""",
            json["changes"]![1]!.ToString(Newtonsoft.Json.Formatting.None));
    }

    [Fact]
    public void AWholeWorldRunAnswersTheWorld()
    {
        SampleResult<BatchItemView> result = Completed(Run(new ScriptedReader((_, sample) => sample),
            Arguments(duration: 0.05, interval: 0.05), frameS: 0.05));

        Assert.Equal("world", (string?)StationGodMCP.Protocol.SampleReply.Of(result)["gateway_id"]);
    }

    // ---- the request ----

    [Fact]
    public void DefaultsAreTheCataloguesFiveSecondsAtHalfASecond()
    {
        SampleRequest request = SampleRequests.Of(JObject.Parse("""{"id": "5", "logic": "On"}"""));

        Assert.Equal(5.0, request.Seconds);
        Assert.Equal(0.5, request.Interval);
        Assert.Equal(11, request.PlannedSamples);
        Assert.Null(request.Gateway);
        Assert.Equal(new SampleTarget("5", "On"), request.Targets[0], new TargetComparer());
    }

    [Fact]
    public void EveryIdIsWatchedForEveryLogicTypeOrEachReadsPair()
    {
        SampleRequest crossed = SampleRequests.Of(JObject.Parse("""{"id": ["1", "2"], "logic": ["On", 12]}"""));
        SampleRequest pairs = SampleRequests.Of(JObject.Parse("""{"reads": [{"id": "1", "logic": "On"}, {"id": "3", "logic": "Mode"}]}"""));

        Assert.Equal(new[] { "1 On", "1 12", "2 On", "2 12" }, Texts(crossed));
        Assert.Equal(new[] { "1 On", "3 Mode" }, Texts(pairs));
    }

    [Theory]
    [InlineData("""{}""", "id")]
    [InlineData("""{"id": "5"}""", "logic")]
    [InlineData("""{"id": "5", "logic": "On", "reads": [{"id": "6", "logic": "On"}]}""", "reads")]
    [InlineData("""{"reads": [{"logic": "On"}]}""", "reads[0]")]
    [InlineData("""{"id": "5", "logic": "On", "seconds": 30, "interval": 0.25}""", "interval")]
    public void AnUnusableRequestIsAProblemAtItsOption(string options, string path)
    {
        OptionProblem problem = Assert.Single(SampleRequests.Problems(JObject.Parse(options)));

        Assert.Equal(path, problem.Path);
    }

    [Fact]
    public void ThirtyTwoTargetsAndOneHundredTwentySamplesAreAllowed()
    {
        Assert.Equal(120, SampleRequests.Of(32, 29.75, 0.25).PlannedSamples);
        Assert.Equal("id", Assert.Single(SampleRequests.Problems(SampleRequests.Watching(33, 1.0, 0.25))).Path);
        Assert.Contains("121 samples", Assert.Single(SampleRequests.Problems(SampleRequests.Watching(1, 30, 0.25))).Problem);
    }

    [Fact]
    public void TheWorldGatewayIsTheWholeWorld()
    {
        Assert.Null(SampleRequests.Of(SampleRequests.Watching(gateway: "world")).Gateway);
        Assert.Equal("77", SampleRequests.Of(SampleRequests.Watching(gateway: " 77 ")).Gateway);
    }

    private static string[] Texts(SampleRequest request) =>
        request.Targets.Select(target => target.Id + " " + target.Logic).ToArray();

    private sealed class TargetComparer : IEqualityComparer<SampleTarget>
    {
        public bool Equals(SampleTarget? a, SampleTarget? b) =>
            a?.Id == b?.Id && JToken.DeepEquals(a?.Logic, b?.Logic);

        public int GetHashCode(SampleTarget target) => target.Id.GetHashCode();
    }

    // ---- the comparison ----

    [Fact]
    public void ResultsCompareAsTheirJsonTextWould()
    {
        LogicTypeView on = new LogicTypeView(28, "On");
        BatchItemView Read(double value) => new LogicReadItemView(0, new LogicRead(new ThingId(5), on, value));
        BatchItemView Fail(string message) =>
            new LogicFailedItemView(0, new ThingId(5), on, new ApiException("logic_not_readable", message));

        Assert.True(LogicResultComparer.Instance.SameValues(Read(double.NaN), Read(double.NaN)));
        Assert.False(LogicResultComparer.Instance.SameValues(Read(0.0), Read(-0.0)));
        Assert.False(LogicResultComparer.Instance.SameValues(Read(1.0), Fail("No.")));
        Assert.True(LogicResultComparer.Instance.SameValues(Fail("No."), Fail("No.")));
        Assert.False(LogicResultComparer.Instance.SameValues(Fail("No."), Fail("Not now.")));
        Assert.NotEqual(ApiJson.WriteShared(Read(0.0)), ApiJson.WriteShared(Read(-0.0)));
    }

    [Theory]
    [InlineData(0.0, 0.05, 1.0, 0.05)]
    [InlineData(0.1499, 0.05, 1.0, 0.15)]
    [InlineData(0.15, 0.05, 1.0, 0.2)]
    [InlineData(0.97, 0.05, 1.0, 1.0)]
    [InlineData(0.62, 0.3, 1.0, 0.9)]
    [InlineData(0.9, 0.3, 1.0, 1.0)]
    public void TheNextDueIsTheNextGridPointCappedAtTheDuration(double elapsed, double interval, double duration,
        double expected)
    {
        Assert.Equal(expected, SampleSchedule.NextDue(elapsed, interval, duration), 9);
    }
}
