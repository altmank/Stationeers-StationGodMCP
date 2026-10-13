#nullable enable

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// write label: its renames (LabelOrders: one id, a list numbered in order, or labels; every problem at once) and its
/// replies (LabelReplies: one rename's shape, a row each with previous and current names for the hub's undo, a refused
/// one rename as the call's refusal, and the report's targets).
/// </summary>
public sealed class WriteLabelTests
{
    private static LabelOrders? Parse(string options, out List<OptionProblem> problems)
    {
        problems = new List<OptionProblem>();
        WriteLabelOptions bound = WriteLabelOptions.Bind(new OptionReader(WriteLabelOptions.Spec, JObject.Parse(options)));
        return LabelOrders.Of(bound.Selector.Ids, bound.Label, bound.Labels, problems);
    }

    private static LabelResult Renamed(long id, string written, string? before) =>
        new LabelResult.Renamed(new Relabel(new ThingId(id), "DynamicGasCanisterEmpty", written, false,
            new LabelStateView(before ?? "Portable Gas Tank", before), new LabelStateView(written, written)));

    // ---- the renames ----

    [Fact]
    public void OneIdTakesTheLabelAsGiven()
    {
        LabelOrders orders = Parse("""{"id": "5", "label": "O2 Tank"}""", out _)!;

        Assert.False(orders.AnswersRows);
        Assert.Equal("O2 Tank", Assert.Single(orders.Orders).Label);
    }

    [Fact]
    public void AListIsNumberedInTheOrderGiven()
    {
        LabelOrders orders = Parse("""{"id": ["9", "5", "7"], "label": "O2 Tank"}""", out _)!;

        Assert.True(orders.AnswersRows);
        Assert.Equal(new[] { ("9", "O2 Tank 1"), ("5", "O2 Tank 2"), ("7", "O2 Tank 3") },
            orders.Orders.Select(order => (order.Id, order.Label)));
    }

    [Fact]
    public void AnEmptyLabelGivesEachItsGameNameUnnumbered()
    {
        LabelOrders orders = Parse("""{"id": ["9", "5"], "label": ""}""", out _)!;

        Assert.All(orders.Orders, order => Assert.Equal(string.Empty, order.Label));
    }

    [Fact]
    public void LabelsAreTheirOwnEntries()
    {
        LabelOrders orders = Parse("""{"labels": [{"id": "5", "label": "A"}, {"id": "6", "label": "B"}]}""", out _)!;

        Assert.Equal(new[] { ("5", "A"), ("6", "B") }, orders.Orders.Select(order => (order.Id, order.Label)));
    }

    [Fact]
    public void EveryProblemIsListedAtOnce()
    {
        Assert.Null(Parse("""{"id": "5", "label": "A", "labels": [{"id": "6"}, {"name": "B"}]}""",
            out List<OptionProblem> problems));

        Assert.Equal(new[] { "labels", "label", "labels[0].label", "labels[1].id", "labels[1].label", "labels[1].name" },
            problems.Select(problem => problem.Path));
        Assert.Null(Parse("""{"id": "5"}""", out List<OptionProblem> missing));
        Assert.Equal("label", Assert.Single(missing).Path);
        Assert.Null(Parse("""{}""", out List<OptionProblem> none));
        Assert.Equal("id", Assert.Single(none).Path);
    }

    // ---- the replies ----

    [Fact]
    public void OneRenameAnswersItsShape()
    {
        JObject reply = ((VerbOutcome.Answered)LabelReplies.Acted(new[] { Renamed(91234, "T2", "T1") }, false)).Reply;

        Assert.Equal(
            """{"reference_id":"91234","prefab_name":"DynamicGasCanisterEmpty","written":"T2","sent_to_host":false,"previous":{"display_name":"T1","custom_name":"T1"},"current":{"display_name":"T2","custom_name":"T2"}}""",
            reply.ToString(Newtonsoft.Json.Formatting.None));
    }

    [Fact]
    public void SeveralRenamesAnswerARowEachForTheHubsUndo()
    {
        JObject reply = ((VerbOutcome.Answered)LabelReplies.Acted(new LabelResult[]
        {
            Renamed(91234, "Tank 1", null),
            new LabelResult.Failed(1, "555", new VerbError("not_labelable", "no"))
        }, true)).Reply;

        JObject row = (JObject)reply["results"]![0]!;
        Assert.Equal(JTokenType.Null, row["previous"]!["custom_name"]!.Type);
        Assert.Equal("Tank 1", (string?)row["current"]!["custom_name"]);
        Assert.Equal("not_labelable", (string?)reply["results"]![1]!["error"]!["code"]);
        Assert.Equal("555", (string?)reply["results"]![1]!["reference_id"]);
        Assert.Equal(1, (int)reply["error_count"]!);
    }

    [Fact]
    public void OneRefusedRenameIsTheCallsRefusal()
    {
        VerbOutcome outcome = LabelReplies.Acted(
            new LabelResult[] { new LabelResult.Failed(0, "5", new VerbError("not_labelable", "no")) }, false);

        Assert.Equal("not_labelable", ((VerbOutcome.Refused)outcome).Error.Code);
    }

    [Fact]
    public void AReportListsEachRenameAndEachRefusal()
    {
        JObject report = LabelReplies.Reported(new LabelResult[]
        {
            new LabelResult.Planned(new ThingId(5), "Portable Gas Tank", "Portable Gas Tank", "O2 Tank 1"),
            new LabelResult.Failed(1, "6", new VerbError("not_labelable", "Pipe (6) is a Pipe."))
        });

        Assert.True((bool)report["report_only"]!);
        Assert.Equal("[5, Portable Gas Tank, Portable Gas Tank -> O2 Tank 1] [undo]",
            (string?)Assert.Single(report["targets"]!)["text"]);
        Assert.Equal(1, (int)Assert.Single(report["problems"]!)["index"]!);
    }
}
