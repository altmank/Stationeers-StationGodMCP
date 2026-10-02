#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Sampling;
using StationGodMCP.Pure.Subscriptions;
using StationGodMCP.Tests.Subscriptions;
using Xunit;

namespace StationGodMCP.Tests.Sampling;

/// <summary>
/// Stage 12's sampler: the sidecar loop's arguments, schedule, change list, counts and rounding, on the real clock,
/// sampled in the first frame at or after each due time.
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

        public LogicSampleRead Read(SampleLogicArguments arguments, List<BatchItemView> into)
        {
            int sample = Samples++;
            if (sample == FailAt)
            {
                return LogicSampleRead.Failed("gateway_not_found", "No gateway gw9.");
            }

            for (int target = 0; target < arguments.Targets.Count; target++)
            {
                ThingId id = new ThingId(100 + target);
                LogicTypeView type = new LogicTypeView(28, "On");
                into.Add(Failing(target, sample)
                    ? new LogicFailedItemView(target, id, type, new ApiException("logic_not_readable", "No."))
                    : new LogicReadItemView(target, new LogicRead(id, type, _values(target, sample))));
            }

            return LogicSampleRead.Read(arguments.GatewayId ?? "world");
        }
    }

    private sealed class Outcomes : ISampleLogicEvents<BatchItemView>
    {
        internal List<SampleLogicOutcome<BatchItemView>> Finished { get; } = new List<SampleLogicOutcome<BatchItemView>>();

        void ISampleLogicEvents<BatchItemView>.Finished(SampleLogicRun<BatchItemView> run,
            SampleLogicOutcome<BatchItemView> outcome) => Finished.Add(outcome);
    }

    private static SampleLogicArguments Arguments(int targets = 1, double duration = 1.0, double interval = 0.05,
        string? gateway = null)
    {
        JObject parameters = new JObject
        {
            ["targets"] = Targets(targets),
            ["duration_seconds"] = duration,
            ["interval_seconds"] = interval,
        };
        if (gateway != null)
        {
            parameters["gateway_id"] = gateway;
        }

        return Assert.IsType<SampleLogicParse.Parsed>(SampleLogicArguments.Of(parameters)).Arguments;
    }

    private static JArray Targets(int count)
    {
        JArray targets = new JArray();
        for (int index = 0; index < count; index++)
        {
            targets.Add(new JObject { ["reference_id"] = (100 + index).ToString(), ["logic_type"] = "On" });
        }

        return targets;
    }

    /// <summary>Runs one call to its end with a frame every frameS real seconds from startS; returns its outcome.</summary>
    private static SampleLogicOutcome<BatchItemView> Run(ScriptedReader reader, SampleLogicArguments arguments,
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

    private static SampleLogicResult<BatchItemView> Completed(SampleLogicOutcome<BatchItemView> outcome) =>
        Assert.IsType<SampleLogicOutcome<BatchItemView>.Completed>(outcome).Result;

    // ---- the sidecar loop, as a model ----

    /// <summary>
    /// The sidecar's loop with reads that take no time and exact delays (Program.cs SampleLogicAsync): a read, then
    /// remaining = duration - elapsed, stop when it is not positive, otherwise wait min(interval, remaining). Changes
    /// compare each result's JSON text with the last one of its index.
    /// </summary>
    private static (int SampleCount, List<(double Elapsed, List<string> Readings)> Changes, double Duration)
        SidecarLoop(ScriptedReader reader, SampleLogicArguments arguments)
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
                string serialized = ApiJson.WriteFresh(reading);
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
            double remaining = arguments.DurationSeconds - elapsed;
            if (remaining <= 1e-9)
            {
                break;
            }

            elapsed += Math.Min(arguments.IntervalSeconds, remaining);
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
        SampleLogicArguments arguments = Arguments(targets, duration, interval);
        (int sampleCount, List<(double Elapsed, List<string> Readings)> changes, double loopDuration) =
            SidecarLoop(new ScriptedReader(Value) { Failing = Failing }, arguments);

        // Frames of a millisecond from an odd start, so every due time lands on a frame.
        SampleLogicResult<BatchItemView> result = Completed(
            Run(new ScriptedReader(Value) { Failing = Failing }, arguments, frameS: 0.001, startS: 1234.5));

        Assert.Equal(sampleCount, result.SampleCount);
        Assert.Equal(changes.Count, result.ChangeCount);
        Assert.Equal(loopDuration, result.DurationSeconds);
        for (int index = 0; index < changes.Count; index++)
        {
            Assert.Equal(changes[index].Elapsed, result.Changes[index].ElapsedSeconds);
            List<string> mine = result.Changes[index].Readings.ConvertAll(reading => ApiJson.WriteFresh(reading));
            Assert.Equal(changes[index].Readings, mine);
        }
    }

    [Fact]
    public void AnIntervalOfFiftyMillisecondsIsKeptAtSixtyFramesASecond()
    {
        SampleLogicResult<BatchItemView> result = Completed(Run(new ScriptedReader((_, sample) => sample),
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
        SampleLogicResult<BatchItemView> result = Completed(Run(new ScriptedReader((_, sample) => sample),
            Arguments(duration: 1.0, interval: 0.05), frameS: 0.2));

        Assert.Equal(6, result.SampleCount);
        Assert.Equal(new[] { 0.0, 0.2, 0.4, 0.6, 0.8, 1.0 },
            result.Changes.ConvertAll(change => change.ElapsedSeconds).ToArray());
    }

    [Fact]
    public void AStallLongerThanTheDurationEndsTheRunWithItsNextSample()
    {
        // Frames 1 to 99 never come (the game hung for two seconds): the sample after the stall is the last.
        SampleLogicResult<BatchItemView> result = Completed(Run(new ScriptedReader((_, sample) => sample),
            Arguments(duration: 1.0, interval: 0.05), frameS: 0.02, frameSkipped: frame => frame > 0 && frame < 100));

        Assert.Equal(2, result.SampleCount);
        Assert.Equal(2.0, result.DurationSeconds);
    }

    [Fact]
    public void TheFirstSampleIsAtZeroAndCarriesEveryReading()
    {
        SampleLogicResult<BatchItemView> result = Completed(Run(new ScriptedReader((_, _) => 1.0),
            Arguments(targets: 3, duration: 0.5, interval: 0.1), frameS: 0.01, startS: 50.0));

        SampleLogicChange<BatchItemView> first = Assert.Single(result.Changes);
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
        SampleLogicOutcome<BatchItemView> outcome = Run(new ScriptedReader((_, _) => 1.0) { FailAt = 3 },
            Arguments(), frameS: 0.01);

        SampleLogicOutcome<BatchItemView>.Failed failed = Assert.IsType<SampleLogicOutcome<BatchItemView>.Failed>(outcome);
        Assert.Equal("gateway_not_found", failed.Code);
        Assert.Equal("No gateway gw9.", failed.Message);
    }

    [Fact]
    public void TheMostOverdueRunGoesFirstWhenTheBudgetIsShort()
    {
        ScriptedReader reader = new ScriptedReader((_, _) => 1.0);
        LogicSampler<BatchItemView> sampler = new LogicSampler<BatchItemView>(reader, LogicResultComparer.Instance);
        Outcomes outcomes = new Outcomes();
        SampleLogicRun<BatchItemView> early = sampler.Start(A, Arguments(interval: 0.1), 0.0);
        sampler.Poll(new RealTimeTick(0, 0.0, Utc), new CountBudget(1), outcomes);
        SampleLogicRun<BatchItemView> late = sampler.Start(A, Arguments(interval: 0.1), 0.12);

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
        SampleLogicRun<BatchItemView> other = sampler.Start(new ConnectionId("c2"), Arguments(), 0.0);

        sampler.CancelConnection(A);

        Assert.Equal(1, sampler.Active);
        Assert.True(sampler.Cancel(other));
        Assert.Equal(0, sampler.Active);
    }

    // ---- the reply on the wire ----

    [Fact]
    public void TheReplyHasTheSidecarsKeys()
    {
        SampleLogicResult<BatchItemView> result = Completed(Run(new ScriptedReader((_, sample) => sample),
            Arguments(duration: 0.1, interval: 0.05, gateway: "gw1"), frameS: 0.05));

        JObject json = JObject.Parse(ApiJson.WriteFresh(result));

        List<string> keys = new List<string>();
        foreach (JProperty property in json.Properties())
        {
            keys.Add(property.Name);
        }

        Assert.Equal(new[]
        {
            "gateway_id", "started_at_utc", "duration_seconds", "interval_seconds", "sample_count", "change_count",
            "changes",
        }, keys.ToArray());
        Assert.Equal("gw1", (string?)json["gateway_id"]);
        Assert.Equal(3, (int)json["sample_count"]!);
        Assert.Equal(0.05, (double)json["changes"]![1]!["elapsed_seconds"]!);
        Assert.Equal("100", (string?)json["changes"]![0]!["readings"]![0]!["reference_id"]);
    }

    // ---- arguments ----

    private static string Invalid(string json) =>
        Assert.IsType<SampleLogicParse.Invalid>(SampleLogicArguments.Of(JObject.Parse(json))).Message;

    private const string OneTarget = "\"targets\": [{\"reference_id\": \"5\", \"logic_type\": \"On\"}]";

    [Fact]
    public void DefaultsAreFiveSecondsAtHalfASecond()
    {
        SampleLogicArguments arguments = Assert.IsType<SampleLogicParse.Parsed>(
            SampleLogicArguments.Of(JObject.Parse("{" + OneTarget + "}"))).Arguments;

        Assert.Equal(5.0, arguments.DurationSeconds);
        Assert.Equal(0.5, arguments.IntervalSeconds);
        Assert.Equal(11, arguments.PlannedSamples);
        Assert.Null(arguments.GatewayId);
        Assert.Equal("5", (string?)arguments.ReadArguments()["reads"]![0]!["reference_id"]);
    }

    [Theory]
    [InlineData("{}", "Argument 'targets' must be an array of 1 to 32 entries.")]
    [InlineData("{\"targets\": []}", "Argument 'targets' must be an array of 1 to 32 entries.")]
    [InlineData("{" + OneTarget + ", \"gateway_id\": 3}", "Argument 'gateway_id' must be a string.")]
    [InlineData("{" + OneTarget + ", \"duration_seconds\": 0.05}", "Argument 'duration_seconds' must be from 0.1 to 30.")]
    [InlineData("{" + OneTarget + ", \"duration_seconds\": 31}", "Argument 'duration_seconds' must be from 0.1 to 30.")]
    [InlineData("{" + OneTarget + ", \"interval_seconds\": 0.04}", "Argument 'interval_seconds' must be from 0.05 to 5.")]
    [InlineData("{" + OneTarget + ", \"interval_seconds\": 6}", "Argument 'interval_seconds' must be from 0.05 to 5.")]
    [InlineData("{" + OneTarget + ", \"interval_seconds\": null}", "Argument 'interval_seconds' must be a finite number.")]
    [InlineData("{" + OneTarget + ", \"duration_seconds\": \"5\"}", "Argument 'duration_seconds' must be a finite number.")]
    [InlineData("{" + OneTarget + ", \"duration_seconds\": 30, \"interval_seconds\": 0.25}",
        "duration_seconds 30 at interval_seconds 0.25 asks for 121 samples (the first at 0 s included); at most 120.")]
    public void BoundsAndMessagesAreTheSidecars(string json, string message)
    {
        Assert.Equal(message, Invalid(json));
    }

    [Fact]
    public void ThirtyTwoTargetsAndOneHundredTwentySamplesAreAllowed()
    {
        JObject parameters = new JObject
        {
            ["targets"] = Targets(32), ["duration_seconds"] = 29.75, ["interval_seconds"] = 0.25,
        };

        SampleLogicArguments arguments = Assert.IsType<SampleLogicParse.Parsed>(SampleLogicArguments.Of(parameters))
            .Arguments;

        Assert.Equal(120, arguments.PlannedSamples);
        parameters["targets"] = Targets(33);
        Assert.IsType<SampleLogicParse.Invalid>(SampleLogicArguments.Of(parameters));
    }

    [Fact]
    public void ABlankGatewayIsTheWholeWorld()
    {
        SampleLogicArguments arguments = Assert.IsType<SampleLogicParse.Parsed>(
            SampleLogicArguments.Of(JObject.Parse("{" + OneTarget + ", \"gateway_id\": \"  \"}"))).Arguments;

        Assert.Null(arguments.GatewayId);
        Assert.Null(arguments.ReadArguments()["gateway_id"]);
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
        Assert.NotEqual(ApiJson.WriteFresh(Read(0.0)), ApiJson.WriteFresh(Read(-0.0)));
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
