#nullable enable

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Lint;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// write logic (catalogue write.json): its writes (LogicOrders: id with logic and value, or writes, each problem in
/// verb names and all at once, missing_target with neither) and its replies (LogicWriteReplies): one write's shape, one
/// row per write with previous_value and current_value for the hub's write-back and undo, a refused one write as the
/// call's refusal, and the report's targets with their undo marks.
/// </summary>
public sealed class WriteLogicTests
{
    private static readonly LogicTypeView On = new LogicTypeView(28, "On");

    private static LogicOrders? Parse(string options, out List<OptionProblem> problems)
    {
        problems = new List<OptionProblem>();
        WriteLogicOptions bound = WriteLogicOptions.Bind(new OptionReader(WriteLogicOptions.Spec, JObject.Parse(options)));
        return LogicOrders.Of(bound.Selector.Ids, bound.Logic, bound.Value, bound.Writes, problems);
    }

    private static LogicWriteChecked Checked(long id, LogicTypeView type, double? previous, double requested) =>
        WriteLint.Checked(new LogicRead(new ThingId(id), type, requested), previous, OpeningVerdict.Closed, false, null,
            null);

    private static LogicWriteResult Written(int index, long id, double? previous, double requested, double? current) =>
        new LogicWriteResult.Written(index, "Door", new LogicWrite(Checked(id, On, previous, requested), current));

    // ---- the writes ----

    [Fact]
    public void OneIdIsOneWriteAnsweringItsShape()
    {
        LogicOrders orders = Parse("""{"id": "5", "logic": "On", "value": 1}""", out List<OptionProblem> problems)!;

        Assert.Empty(problems);
        Assert.False(orders.AnswersRows);
        LogicOrder order = Assert.Single(orders.Orders);
        Assert.Equal(("5", 1.0), (order.Id, order.Value));
        Assert.Equal("On", (string?)order.Logic);
    }

    [Fact]
    public void AnIdListIsOneWriteEachAnsweringRows()
    {
        LogicOrders orders = Parse("""{"id": ["5", "6"], "logic": 28, "value": 0}""", out _)!;

        Assert.True(orders.AnswersRows);
        Assert.Equal(new[] { "5", "6" }, orders.Orders.Select(order => order.Id));
        Assert.Equal(new[] { 0, 1 }, orders.Orders.Select(order => order.Index));
    }

    [Fact]
    public void WritesAreTheirOwnEntriesInOrder()
    {
        LogicOrders orders = Parse("""{"writes": [{"id": "5", "logic": "On", "value": 1}, {"id": "6", "logic": "Setting", "value": 20.5}]}""",
            out _)!;

        Assert.True(orders.AnswersRows);
        Assert.Equal(new[] { ("5", 1.0), ("6", 20.5) }, orders.Orders.Select(order => (order.Id, order.Value)));
    }

    [Fact]
    public void NeitherIdNorWritesIsMissingTarget()
    {
        Assert.Null(Parse("""{"logic": "On", "value": 1}""", out List<OptionProblem> problems));
        Assert.Empty(problems);

        VerbError missing = LogicOrders.Missing("write logic");
        Assert.Equal("missing_target", missing.Code);
        Assert.Equal(new JArray("id", "writes"), missing.Data!["options"]);
    }

    [Fact]
    public void EveryProblemIsListedAtOnceInVerbNames()
    {
        Assert.Null(Parse("""{"writes": [{"id": "5"}, 3, {"id": "6", "logic": "On", "value": 1, "reference_id": "6"}], "logic": "On"}""",
            out List<OptionProblem> problems));

        Assert.Equal(new[] { "logic", "writes[0].logic", "writes[0].value", "writes[1]", "writes[2].reference_id" },
            problems.Select(problem => problem.Path));
    }

    [Fact]
    public void AnIdNeedsLogicAndValueAndGoesWithoutWrites()
    {
        Assert.Null(Parse("""{"id": "5"}""", out List<OptionProblem> missing));
        Assert.Equal(new[] { "logic", "value" }, missing.Select(problem => problem.Path));

        Assert.Null(Parse("""{"id": "5", "writes": []}""", out List<OptionProblem> both));
        Assert.Equal("writes", Assert.Single(both).Path);
    }

    // ---- the replies ----

    [Fact]
    public void OneWriteAnswersItsShapeWithTheValuesBeforeAndAfter()
    {
        VerbOutcome outcome = LogicWriteReplies.Acted("world", new[] { Written(0, 5, 0, 1, 1) }, false);

        Assert.Equal(
            """{"gateway_id":"world","reference_id":"5","logic_type":{"id":28,"name":"On"},"requested_value":1.0,"previous_value":0.0,"current_value":1.0}""",
            ((VerbOutcome.Answered)outcome).Reply.ToString(Newtonsoft.Json.Formatting.None));
    }

    [Fact]
    public void OneRefusedWriteIsTheCallsRefusal()
    {
        VerbOutcome outcome = LogicWriteReplies.Acted("world",
            new LogicWriteResult[]
            {
                new LogicWriteResult.Failed(0, "5", new VerbError("logic_not_writable", "Door 5 does not write On."))
            }, false);

        Assert.Equal("logic_not_writable", ((VerbOutcome.Refused)outcome).Error.Code);
    }

    [Fact]
    public void SeveralWritesAnswerARowEachForTheHubsWriteBack()
    {
        JObject reply = ((VerbOutcome.Answered)LogicWriteReplies.Acted("world", new LogicWriteResult[]
        {
            Written(0, 5, 0, 1, 1),
            new LogicWriteResult.Failed(1, "6", new VerbError("device_not_found", "Gone."))
        }, true)).Reply;

        Assert.Equal("world", (string?)reply["gateway_id"]);
        JObject row = (JObject)reply["results"]![0]!;
        Assert.Equal(new[] { "index", "ok", "reference_id", "logic_type", "requested_value", "previous_value", "current_value" },
            row.Properties().Select(key => key.Name));
        Assert.Equal(0.0, (double)row["previous_value"]!);
        Assert.Equal(1.0, (double)row["current_value"]!);
        JObject failed = (JObject)reply["results"]![1]!;
        Assert.False((bool)failed["ok"]!);
        Assert.Equal("6", (string?)failed["reference_id"]);
        Assert.Equal("device_not_found", (string?)failed["error"]!["code"]);
        Assert.Equal((2, 1, 1), ((int)reply["count"]!, (int)reply["success_count"]!, (int)reply["error_count"]!));
    }

    [Fact]
    public void AWriteOnlyTypeHasNoValueBeforeOrAfter()
    {
        JObject reply = ((VerbOutcome.Answered)LogicWriteReplies.Acted("world",
            new[] { Written(0, 5, null, 1, null) }, false)).Reply;

        Assert.Equal(JTokenType.Null, reply["previous_value"]!.Type);
        Assert.Equal(JTokenType.Null, reply["current_value"]!.Type);
    }

    [Fact]
    public void AReportListsEveryWriteAsATargetWithItsMarkAndNothingActs()
    {
        LogicTypeView pulse = new LogicTypeView(250, "Activate");
        JObject report = LogicWriteReplies.Reported("world", new LogicWriteResult[]
        {
            new LogicWriteResult.Planned(0, "Door", Checked(5, On, 0, 1)),
            new LogicWriteResult.Planned(1, "Sorter", Checked(6, pulse, 0, 1))
        });

        Assert.True((bool)report["report_only"]!);
        Assert.Empty((JArray)report["problems"]!);
        JArray targets = (JArray)report["targets"]!;
        Assert.Equal(new[] { "5", "6" }, targets.Select(target => (string?)target["id"]));
        Assert.Equal("Door (On)", (string?)targets[0]["name"]);
        Assert.Equal(new[] { "[undo]", "[cannot undo: action]" }, targets.Select(target => (string?)target["undo"]));
        Assert.Equal(0.0, (double)targets[0]["current"]!);
        Assert.Equal(1.0, (double)targets[0]["new"]!);
    }
}
