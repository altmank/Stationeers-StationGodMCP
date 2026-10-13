#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// server profiling without the game: each action (on, off, reset, report, any case), slow_frame_ms (kept to 1 to 1000,
/// and only with on), csv (only with on), and the what's label on a refusal.
/// </summary>
public sealed class ProfilingOrderTests
{
    private static ProfilingOrder? Order(string json, List<OptionProblem> problems)
    {
        OptionReader given = new OptionReader(ServerProfilingOptions.Spec, JObject.Parse(json));
        return ProfilingOrder.Of(ServerProfilingOptions.Bind(given), given, problems);
    }

    // ---- the action ----

    [Theory]
    [InlineData("""{"action": "on"}""", "On")]
    [InlineData("""{"action": "OFF"}""", "Off")]
    [InlineData("""{"action": " reset "}""", "Reset")]
    [InlineData("""{"action": "Report"}""", "Report")]
    public void TheActionIsOneOfTheFourWords(string json, string action)
    {
        List<OptionProblem> problems = new();

        ProfilingOrder order = Order(json, problems)!;

        Assert.Empty(problems);
        Assert.Equal(action, order.Action.ToString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"action": "   "}""")]
    public void AnActionIsRequired(string json)
    {
        List<OptionProblem> problems = new();

        Assert.Null(Order(json, problems));

        Assert.Equal("action", Assert.Single(problems).Path);
        Assert.Equal("is required: one of on, off, reset, report", problems[0].Problem);
    }

    [Fact]
    public void AnActionThatIsNoWordIsAProblemNamingTheFour()
    {
        List<OptionProblem> problems = new();

        Assert.Null(Order("""{"action": "start"}""", problems));

        Assert.Equal("action", Assert.Single(problems).Path);
        Assert.Equal("'start' is one of on, off, reset, report", problems[0].Problem);
        Assert.Equal("server profiling: action: 'start' is one of on, off, reset, report.",
            OptionProblem.Refusal(ServerProfilingOptions.Spec.Label, problems).Message);
    }

    [Fact]
    public void AnActionNearAWordNamesIt()
    {
        List<OptionProblem> problems = new();

        Assert.Null(Order("""{"action": "rest"}""", problems));

        Assert.Equal(new[] { "reset" }, problems[0].Nearest);
    }

    // ---- what on takes ----

    [Theory]
    [InlineData("""{"action": "on", "slow_frame_ms": 12.5, "csv": true}""", 12.5, true)]
    [InlineData("""{"action": "on", "slow_frame_ms": 1}""", 1.0, null)]
    [InlineData("""{"action": "on", "slow_frame_ms": 1000}""", 1000.0, null)]
    [InlineData("""{"action": "on", "csv": false}""", null, false)]
    // The catalogue's default fills the option in, but the call gave none: null leaves the session's threshold.
    [InlineData("""{"action": "on"}""", null, null)]
    public void OnTakesTheThresholdAndTheCsv(string json, double? slowFrameMs, bool? csv)
    {
        List<OptionProblem> problems = new();

        ProfilingOrder order = Order(json, problems)!;

        Assert.Empty(problems);
        Assert.Equal(ProfilingAction.On, order.Action);
        Assert.Equal(slowFrameMs, order.SlowFrameMs);
        Assert.Equal(csv, order.Csv);
    }

    [Theory]
    [InlineData("""{"action": "on", "slow_frame_ms": 0.5}""")]
    [InlineData("""{"action": "on", "slow_frame_ms": 1000.5}""")]
    public void TheThresholdIsKeptToItsLimits(string json)
    {
        List<OptionProblem> problems = new();

        Assert.Null(Order(json, problems));

        Assert.Equal("slow_frame_ms", Assert.Single(problems).Path);
        Assert.Equal("is 1 to 1000", problems[0].Problem);
    }

    [Theory]
    [InlineData("""{"action": "off", "slow_frame_ms": 8}""", "slow_frame_ms")]
    [InlineData("""{"action": "report", "csv": true}""", "csv")]
    [InlineData("""{"action": "reset", "csv": true}""", "csv")]
    public void TheThresholdAndTheCsvGoWithOnOnly(string json, string path)
    {
        List<OptionProblem> problems = new();

        Assert.Null(Order(json, problems));

        Assert.Equal(path, Assert.Single(problems).Path);
        Assert.Equal("goes with action: on only", problems[0].Problem);
    }

    [Fact]
    public void BothTheThresholdAndTheCsvAreNamedAtOnce()
    {
        List<OptionProblem> problems = new();

        Assert.Null(Order("""{"action": "off", "slow_frame_ms": 8, "csv": false}""", problems));

        Assert.Equal(new[] { "slow_frame_ms", "csv" },
            new List<string> { problems[0].Path, problems[1].Path });
        Assert.Equal("server profiling: slow_frame_ms: goes with action: on only; csv: goes with action: on only.",
            OptionProblem.Refusal(ServerProfilingOptions.Spec.Label, problems).Message);
    }

    [Fact]
    public void WhatOnlyOnTakesIsNotReadForTheOtherActions()
    {
        List<OptionProblem> problems = new();

        ProfilingOrder order = Order("""{"action": "report"}""", problems)!;

        Assert.Empty(problems);
        Assert.Null(order.SlowFrameMs);
        Assert.Null(order.Csv);
    }
}
