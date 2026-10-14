#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// The write path's WriteCheck: a verb write's report describes the plan and never acts, its confirm acts once, and a
/// check that refuses the acting call is the call's refusal or the report's one problem. Its checks read the world
/// (write memory's range, the genes' values).
/// </summary>
public sealed class WriteCheckTests
{
    private sealed class Plan
    {
    }

    private sealed class Counter
    {
        internal int Acts;

        internal int Descriptions;

        internal object Act(Plan plan)
        {
            Acts++;
            return "acted";
        }

        internal object Describe(Plan plan)
        {
            Descriptions++;
            return "described";
        }
    }

    // ---- a verb write: report until confirm ----

    private static readonly ReportConfirm Reporting = new ReportConfirm(true, false, null);

    private static readonly ReportConfirm Confirmed = new ReportConfirm(true, true, null);

    [Fact]
    public void AVerbReportDescribesThePlanAndNeverActs()
    {
        Counter counter = new Counter();
        VerbOutcome outcome = WriteCheck<Plan>.Run(() => new Plan()).Outcome(Reporting,
            plan => new VerbOutcome.Answered(new JObject { ["acted"] = (string)counter.Act(plan) }),
            plan => Report.Of(new[] { new ReportTarget("5", null, (string)counter.Describe(plan), 0, 1, "[undo]") }));

        Assert.Equal(0, counter.Acts);
        JObject report = ((VerbOutcome.Answered)outcome).Reply;
        Assert.True((bool)report["report_only"]!);
        Assert.Equal("[5, described, 0 -> 1] [undo]", (string?)report["targets"]![0]!["text"]);
    }

    [Fact]
    public void AConfirmedVerbWriteActsOnce()
    {
        Counter counter = new Counter();
        VerbOutcome outcome = WriteCheck<Plan>.Run(() => new Plan()).Outcome(Confirmed,
            plan => new VerbOutcome.Answered(new JObject { ["acted"] = (string)counter.Act(plan) }),
            plan => throw new InvalidOperationException("an acting call does not report"));

        Assert.Equal((1, 0), (counter.Acts, counter.Descriptions));
        Assert.Equal("acted", (string?)((VerbOutcome.Answered)outcome).Reply["acted"]);
    }

    [Fact]
    public void AVerbReportListsTheRefusalTheActingCallAnswers()
    {
        Func<Plan> checks = () => throw ApiErrors.Refused("memory_not_writable", "Device 5 has no writable memory.");
        Counter counter = new Counter();

        VerbError refused = ((VerbOutcome.Refused)WriteCheck<Plan>.Run(checks)
            .Outcome(Confirmed, _ => throw new InvalidOperationException(), _ => new JObject())).Error;
        JObject report = ((VerbOutcome.Answered)WriteCheck<Plan>.Run(checks)
            .Outcome(Reporting, _ => throw new InvalidOperationException(), _ => new JObject())).Reply;

        Assert.Equal(0, counter.Acts);
        Assert.Equal("memory_not_writable", refused.Code);
        Assert.Equal(
            "{\"problems\":[{\"code\":\"memory_not_writable\",\"message\":\"Device 5 has no writable memory.\"}]," +
            "\"report_only\":true,\"targets\":[]}",
            report.ToString(Newtonsoft.Json.Formatting.None));
    }

    [Fact]
    public void AFaultThatIsNotARefusalIsRaisedReportOrNot()
    {
        Counter counter = new Counter();
        Assert.Throws<InvalidOperationException>(() =>
            WriteCheck<Plan>.Run(() => throw new InvalidOperationException("broken")));
        Assert.Equal(0, counter.Acts);
    }

    // ---- write memory ----

    [Fact]
    public void AMemoryReportGivesTheTargetTheValuesAndWhatIsThereNowAndNoCurrentValues()
    {
        JObject report = MemoryWrites.Reported(new MemoryPlace("local", new ThingId(5), 2, 512), "IC Housing",
            new List<double> { 1, 2 }, new List<double> { 0, 7 });

        Assert.Equal(
            "{\"gateway_id\":\"local\",\"reference_id\":\"5\",\"start_address\":2,\"requested_values\":[1.0,2.0]," +
            "\"previous_values\":[0.0,7.0],\"count\":2,\"stack_size\":512,\"problems\":[],\"report_only\":true," +
            "\"targets\":[{\"id\":\"5\",\"name\":\"IC Housing\",\"current\":[0.0,7.0],\"new\":[1.0,2.0]," +
            "\"undo\":\"[undo]\",\"text\":\"[5, IC Housing, [0.0,7.0] -> [1.0,2.0]] [undo]\"}]}",
            report.ToString(Newtonsoft.Json.Formatting.None));
    }

    [Fact]
    public void AMemoryWriteGivesTheMemoryBeforeAndAfter()
    {
        JObject written = MemoryWrites.Written(new MemoryPlace("world", new ThingId(5), 0, null),
            new List<double> { 3 }, new List<double> { 0 }, new List<double> { 3 });

        Assert.Equal(new[] { "gateway_id", "reference_id", "start_address", "requested_values", "previous_values",
            "current_values", "count", "stack_size" }, written.Properties().Select(key => key.Name));
        Assert.Equal(3.0, (double)written["current_values"]![0]!);
    }

    [Fact]
    public void MemoryValuesAreFiniteNumbersAndStartAnAddress()
    {
        List<OptionProblem> problems = new List<OptionProblem>();

        Assert.Null(MemoryWrites.Values(JArray.Parse("[1, \"x\", 2]"), problems));
        Assert.Null(MemoryWrites.Start(1L + int.MaxValue, problems));
        Assert.Equal(new[] { "values[1]", "start" }, problems.Select(problem => problem.Path));
    }

    [Fact]
    public void AMemoryRangePastTheLastAddressIsAProblemInTheReport()
    {
        Func<Plan> checks = () =>
        {
            DeviceMemoryRange.Require(510, 4, 512);
            return new Plan();
        };

        Assert.Null(Record.Exception(() => DeviceMemoryRange.Require(0, 512, 512)));
        Assert.Equal("invalid_argument", ProblemCode(checks));
        Assert.Equal("invalid_argument", ActingCode(checks));
    }

    // ---- write genes ----

    [Fact]
    public void AGeneValueOutsideTheRangeIsRefusedUnlessAllowedAndANonFiniteOneAlways()
    {
        Assert.Equal(GeneValueRefusal.None, GeneValues.Refusal(1.0, allowOutOfRange: false));
        Assert.Equal(GeneValueRefusal.None, GeneValues.Refusal(-1.0, allowOutOfRange: false));
        Assert.Equal(GeneValueRefusal.OutOfRange, GeneValues.Refusal(1.5, allowOutOfRange: false));
        Assert.Equal(GeneValueRefusal.None, GeneValues.Refusal(1.5, allowOutOfRange: true));
        Assert.Equal(GeneValueRefusal.NotFinite, GeneValues.Refusal(double.NaN, allowOutOfRange: true));
    }

    [Fact]
    public void AGenesReportListsEachRefusedGeneAsAProblemWithItsIndexAndTheOthersAsTargets()
    {
        BatchBuilder genes = new BatchBuilder(2);
        genes.Succeeded(new GeneWrittenView(0, "WaterUsage", 0.5f, 1f));
        genes.Failed(new GeneNotWrittenView(1, "LightPerDay", -0.25f,
            ApiErrors.Refused("out_of_range", "Gene LightPerDay: 2 is outside the game's range -1 to 1.")));
        JObject report = GeneReplies.Reported(
            new GeneSetHeader(new ThingView(new ThingId(9), "SeedBag_Corn", "Corn Seeds"), "seed", 3, true),
            genes.Build(), "[undo]");

        Assert.Equal(new[]
        {
            "thing", "holder", "unit", "is_top", "count", "success_count", "error_count", "problems", "report_only",
            "targets"
        }, report.Properties().Select(key => key.Name));
        Assert.Equal(1, (int)report["problems"]![0]!["index"]!);
        Assert.Equal("9", (string?)report["problems"]![0]!["reference_id"]);
        Assert.Equal("out_of_range", (string?)report["problems"]![0]!["code"]);
        Assert.Equal("[9, Corn Seeds (WaterUsage), 0.5 -> 1] [undo]", (string?)Assert.Single((JArray)report["targets"]!)["text"]);
    }

    [Fact]
    public void GenesAreANonEmptyObjectOfNumbersWithEveryOtherValueAProblem()
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        Assert.Null(GeneOrders.Of(JObject.Parse("""{"WaterUsage": "high", "LightPerDay": null, "DarkPerDay": 1}"""),
            problems));
        Assert.Equal(new[] { "genes.WaterUsage", "genes.LightPerDay" }, problems.Select(problem => problem.Path));

        List<OptionProblem> none = new List<OptionProblem>();
        Assert.Null(GeneOrders.Of(new JObject(), none));
        Assert.Equal("genes", Assert.Single(none).Path);
        Assert.NotNull(GeneOrders.Of(JObject.Parse("""{"DarkPerDay": -0.5}"""), new List<OptionProblem>()));
    }

    private static string? ProblemCode(Func<Plan> checks)
    {
        JObject report = ((VerbOutcome.Answered)WriteCheck<Plan>.Run(checks)
            .Outcome(Reporting, _ => throw new InvalidOperationException("a report does not act"),
                _ => new JObject())).Reply;

        Assert.True((bool)report["report_only"]!);
        return (string?)report["problems"]![0]!["code"];
    }

    private static string ActingCode(Func<Plan> checks)
    {
        VerbError refused = ((VerbOutcome.Refused)WriteCheck<Plan>.Run(checks)
            .Outcome(Confirmed, _ => throw new InvalidOperationException("a refusal does not act"),
                _ => new JObject())).Error;

        return refused.Code;
    }
}
