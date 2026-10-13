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
/// write paint: its request (PaintRequest: the colours with no target, ids in one color, items a color each, a
/// network's members; every problem at once) and its replies (PaintReplies: the colour list, a row per thing with
/// previous_color and color for the hub's undo, and the report's targets).
/// </summary>
public sealed class WritePaintTests
{
    private static PaintRequest? Parse(string options, out List<OptionProblem> problems)
    {
        problems = new List<OptionProblem>();
        WritePaintOptions bound = WritePaintOptions.Bind(new OptionReader(WritePaintOptions.Spec, JObject.Parse(options)));
        return PaintRequest.Of(bound.Selector.Ids, bound.Selector.Network, bound.Kind, bound.Fields.Includes("devices"),
            bound.Color, bound.Items, problems);
    }

    // ---- the request ----

    [Fact]
    public void NoTargetListsTheColours()
    {
        Assert.IsType<PaintRequest.Colors>(Parse("{}", out _));
    }

    [Fact]
    public void IdsTakeOneColorAndItemsTheirOwn()
    {
        PaintRequest.Things ids = (PaintRequest.Things)Parse("""{"id": ["5", "6"], "color": "Red"}""", out _)!;
        PaintRequest.Things items = (PaintRequest.Things)Parse(
            """{"items": [{"id": "5", "color": "Red"}, {"id": "6", "color": "3"}]}""", out _)!;

        Assert.Equal(new[] { ("5", "Red"), ("6", "Red") }, ids.Orders.Select(order => (order.Id, order.Color)));
        Assert.Equal(new[] { ("5", "Red"), ("6", "3") }, items.Orders.Select(order => (order.Id, order.Color)));
    }

    [Fact]
    public void ANetworkWithKindAndDevicesIsOnePaintOfItsMembers()
    {
        PaintRequest.Network network = (PaintRequest.Network)Parse(
            """{"network": "4639870", "kind": "pipe", "include": ["devices"], "color": "Red"}""", out _)!;

        Assert.Equal(("4639870", "pipe", true, "Red"),
            (network.Target.Id, network.Kind, network.Devices, network.Color));
    }

    [Fact]
    public void EveryProblemIsListedAtOnce()
    {
        Assert.Null(Parse("""{"id": "5", "network": "6", "kind": "pipe"}""", out List<OptionProblem> both));
        Assert.Equal(new[] { "id", "color" }, both.Select(problem => problem.Path));

        Assert.Null(Parse("""{"color": "Red", "kind": "cable", "include": ["devices"]}""", out List<OptionProblem> lone));
        Assert.Equal(new[] { "kind", "include", "color" }, lone.Select(problem => problem.Path));

        Assert.Null(Parse("""{"items": [{"id": "5"}, 4], "color": "Red"}""", out List<OptionProblem> items));
        Assert.Equal(new[] { "color", "items[0].color", "items[1]" }, items.Select(problem => problem.Path));
    }

    // ---- the replies ----

    [Fact]
    public void TheColourListIsEachColourAndTheCount()
    {
        JObject reply = PaintReplies.Colors(new PaintColorsView(new List<PaintColorView>
        {
            new PaintColorView(0, "Blue", false),
            new PaintColorView(1, null, true)
        }));

        Assert.Equal("""{"colors":[{"index":0,"name":"Blue","paint_only":false},{"index":1,"name":null,"paint_only":true}],"count":2}""",
            reply.ToString(Newtonsoft.Json.Formatting.None));
    }

    [Fact]
    public void APaintIsARowWithTheColourBeforeAndAfterForTheHubsUndo()
    {
        ThingColorView was = new ThingColorView(3, "Orange", true);
        JObject reply = PaintReplies.Acted(new PaintResult[]
        {
            new PaintResult.Painted(new ThingId(11), was, new ThingColorView(0, "Blue", false)),
            new PaintResult.Failed(1, "12", new VerbError("not_paintable", "no"))
        });

        Assert.Equal(
            """{"results":[{"index":0,"ok":true,"reference_id":"11","previous_color":{"index":3,"name":"Orange","is_default":true},"color":{"index":0,"name":"Blue","is_default":false}},{"index":1,"ok":false,"reference_id":"12","error":{"code":"not_paintable","message":"no"}}],"count":2,"success_count":1,"error_count":1}""",
            reply.ToString(Newtonsoft.Json.Formatting.None));
    }

    [Fact]
    public void AReportListsEachThingWithItsColourNowAndTheColourAsked()
    {
        JObject report = PaintReplies.Reported(new PaintResult[]
        {
            new PaintResult.Planned(new ThingId(11), "Pipe", new ThingColorView(3, "Orange", true), "Red"),
            new PaintResult.Failed(1, "12", new VerbError("has_color_state", "no"))
        });

        Assert.Equal("[11, Pipe, Orange -> Red] [undo]", (string?)Assert.Single(report["targets"]!)["text"]);
        Assert.Equal("has_color_state", (string?)Assert.Single(report["problems"]!)["code"]);
    }
}
