#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// undo's call without the game: the ids (one id or a list; none refuses missing_target), materials given as one
/// holder, as "player", or left out (which the bound MaterialsSource cannot tell apart from the player, so the order
/// carries the caller's decision itself), refund_to left out or named, the raw allow_bridge every piece run takes, a
/// blank acknowledge_gas_lost, the bulk-write words allow and from_step, and the order the ids run in: newest first by
/// their numeric suffix, an id with no number keeping its place last.
/// </summary>
public sealed class UndoOrderTests
{
    [Fact]
    public void OneIdAndAListBothRead()
    {
        UndoOrders one = Order("""{"id": "run-4"}""");

        Assert.Equal(new[] { "run-4" }, one.Ids!.Ids);
        Assert.False(one.Ids.GivenAsList);
        Assert.False(one.Acts);
        Assert.Equal("[cannot undo]", one.Mark);
        Assert.Equal("undo", one.Label);

        UndoOrders list = Order("""{"id": ["place-2", "remove-3"]}""");

        Assert.Equal(new[] { "place-2", "remove-3" }, list.Ids!.Ids);
        Assert.True(list.Ids.GivenAsList);
    }

    [Fact]
    public void NoIdAtAllIsMissingTarget()
    {
        UndoOrders order = Order("""{"confirm": true}""");

        Assert.Null(order.Ids);
        VerbError missing = UndoOrders.Missing();
        Assert.Equal("missing_target", missing.Code);
        Assert.Contains("undo: name the job by id", missing.Message);
        Assert.Contains("paste", missing.Message);
        Assert.Equal("id", (string?)missing.Data!["options"]![0]);
    }

    [Fact]
    public void MaterialsLeftOutLeavesTheJobsOwnSourceToDecide()
    {
        UndoOrders order = Order("""{"id": "run-4"}""");

        Assert.False(order.HolderGiven);
        Assert.Null(order.Holder);
        Assert.Null(order.RefundTo);
    }

    [Fact]
    public void MaterialsPlayerIsThePlayerAndGiven()
    {
        UndoOrders order = Order("""{"id": "run-4", "materials": "player"}""");

        Assert.True(order.HolderGiven);
        Assert.Null(order.Holder);
    }

    [Fact]
    public void OneHolderByIdReadsAsThatHolder()
    {
        UndoOrders order = Order("""{"id": "run-4", "materials": "4936423"}""");

        Assert.True(order.HolderGiven);
        Assert.Equal("4936423", Assert.IsType<Payer.Holder>(order.Holder).Id);
    }

    [Fact]
    public void MaterialsAsAListIsAProblem()
    {
        OptionReader reader = Read("""{"id": "run-4", "materials": ["4936423", "7"]}""");
        UndoOrders order = UndoOrders.Bind(reader);

        Assert.False(order.HolderGiven);
        OptionProblem problem = Assert.Single(reader.Problems);
        Assert.Equal("materials", problem.Path);
        Assert.Contains("one holder", problem.Problem);
    }

    [Fact]
    public void RefundToLeftOutIsNoRouteAndAWordIsTheRoute()
    {
        Assert.Null(Order("""{"id": "run-4"}""").RefundTo);
        Assert.Same(RefundRoute.Nothing, Order("""{"id": "run-4", "refund_to": "none"}""").RefundTo);
        Assert.Same(RefundRoute.WherePieceStood, Order("""{"id": "run-4", "refund_to": "ground"}""").RefundTo);
    }

    [Fact]
    public void ABlankAcknowledgeIsAProblem()
    {
        OptionReader reader = Read("""{"id": "run-4", "acknowledge_gas_lost": "run-3"}""");
        UndoOrders.Bind(reader);
        Assert.Empty(reader.Problems);

        OptionReader blank = Read("""{"id": "run-4", "acknowledge_gas_lost": "  "}""");
        UndoOrders order = UndoOrders.Bind(blank);

        Assert.Equal("  ", order.AcknowledgeGasLost);
        Assert.Equal("acknowledge_gas_lost", Assert.Single(blank.Problems).Path);
    }

    [Fact]
    public void AllowBridgeIsKeptWhole()
    {
        UndoOrders order = Order("""{"id": "run-4", "allow_bridge": ["4936423"]}""");

        Assert.Equal("4936423", (string?)order.AllowBridge![0]);
    }

    [Fact]
    public void AllowAndFromStepAreTheBulkWritesWords()
    {
        UndoOrders order = Order("""{"id": "write-4", "allow": ["overwrite"], "from_step": 3}""");

        Assert.Equal(new[] { "overwrite" }, order.Allow!);
        Assert.Equal(3, order.FromStep);
    }

    [Fact]
    public void ConfirmIsWhatMakesTheCallAct()
    {
        Assert.False(Order("""{"id": "run-4"}""").Acts);
        Assert.True(Order("""{"id": "run-4", "confirm": true}""").Acts);
    }

    [Fact]
    public void TheIdsRunNewestFirstAndOneWithNoNumberKeepsItsPlaceLast()
    {
        List<(string Id, int Given)> ordered = UndoOrders.NewestFirst(new[] { "run-4", "place-12", "run-9", "paste" });

        Assert.Equal(new[] { "place-12", "run-9", "run-4", "paste" },
            ordered.ConvertAll(pair => pair.Id).ToArray());
        Assert.Equal(new[] { 1, 2, 0, 3 }, ordered.ConvertAll(pair => pair.Given).ToArray());
    }

    private static UndoOrders Order(string json) => UndoOrders.Bind(Read(json));

    private static OptionReader Read(string json) => new OptionReader(UndoOptions.Spec, JObject.Parse(json));
}
