#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// remove structure without the game: the ids read against the world before the order (each one that could not be
/// read a problem at its place), the guards allow lifts, refund_to, the holder materials names, gas_to, include notes
/// and verbose, confirm, queue and a blank acknowledge_gas_lost; and the undo's replay of the same call: the ids of
/// what the job built, the undo's one holder and refund route, the caller's acknowledge, and no guard lifted.
/// </summary>
public sealed class RemoveStructureOrderTests
{
    [Fact]
    public void TheCallReadsItsGuardsPayersAndDetail()
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        RemoveStructureOrder order = Order("""
            {"id": "5", "allow": ["contents", "burst"], "refund_to": "ground", "materials": "4936423",
             "gas_to": "connected", "include": ["notes"], "verbose": true, "confirm": true, "queue": false}
            """, problems)!;

        Assert.Empty(problems);
        Assert.Equal("remove structure", order.Label);
        Assert.Equal("[undo]", order.Mark);
        Assert.Equal(new[] { 5L }, Ids(order));
        Assert.True(order.Allow.Contents);
        Assert.True(order.Allow.Burst);
        Assert.False(order.Allow.Breach || order.Allow.Broken || order.Allow.Unsupported);
        Assert.Same(RefundRoute.WherePieceStood, order.Materials.RefundTo);
        Assert.Equal("4936423", Assert.IsType<Payer.Holder>(Assert.Single(order.Materials.Payers)).Id);
        Assert.Same(GasTarget.AnyConnected, order.GasTo);
        Assert.True(order.IncludeNotes);
        Assert.True(order.SplitDetail);
        Assert.True(order.Report.Acts);
        Assert.False(order.Queue);
    }

    [Fact]
    public void TheTypeTheGamePlansIsTheCallsOwn()
    {
        RemoveStructureOrder order = Order("""{"id": "5", "refund_to": "none"}""", new List<OptionProblem>())!;

        RemoveRequest arguments = order.Arguments(new ThingId(4936423), new List<ThingId> { new ThingId(7) });

        Assert.Equal(new[] { 5L }, Ids(order));
        Assert.Same(RefundRoute.Nothing, arguments.RefundTo);
        Assert.Equal(new ThingId(4936423), arguments.From);
        Assert.False(arguments.Allow.Contents);
        Assert.Null(arguments.GasTo);
        Assert.False(arguments.IncludeNotes);
    }

    [Fact]
    public void AnIdThatCouldNotBeReadKeepsItsPlace()
    {
        List<ReportProblem> unread = new List<ReportProblem>();
        List<ThingId?> named = new List<ThingId?> { new ThingId(5), null, new ThingId(7) };
        unread.Add(new ReportProblem(1, null, new VerbError("thing_not_found", "remove structure: 'nope' is not a thing's id.")));

        RemoveStructureOrder order = Order("""{"id": ["5", "nope", "7"]}""", new List<OptionProblem>(), named, unread)!;

        Assert.Equal(new[] { 5L, 7L }, Ids(order));
        Assert.Equal(3, order.Named.Count);
        Assert.Null(order.Named[1]);
        Assert.Equal(1, Assert.Single(order.Unread).Index);
    }

    [Fact]
    public void ABlankAcknowledgeIsAProblemAndNoOrder()
    {
        List<OptionProblem> problems = new List<OptionProblem>();

        Assert.Null(Order("""{"id": "5", "acknowledge_gas_lost": " "}""", problems));
        Assert.Equal("acknowledge_gas_lost", Assert.Single(problems).Path);
    }

    [Fact]
    public void AGasTargetThatNamesNothingIsAProblemAndNoOrder()
    {
        List<OptionProblem> problems = new List<OptionProblem>();

        Assert.Null(Order("""{"id": "5", "gas_to": "the tank over there"}""", problems));
        Assert.Equal("gas_to", Assert.Single(problems).Path);
    }

    [Fact]
    public void TheReplayTakesTheUndosHolderRefundAndAcknowledgeAndLiftsNothing()
    {
        RemoveStructureOrder order = RemoveStructureOrder.Replay(new long[] { 12, 13 }, 4936423,
            RefundRoute.Nothing, "run-3", acts: true, queue: true);

        Assert.Equal(new[] { 12L, 13L }, Ids(order));
        Assert.Equal(2, order.Named.Count);
        Assert.Equal("4936423", Assert.IsType<Payer.Holder>(Assert.Single(order.Materials.Payers)).Id);
        Assert.Same(RefundRoute.Nothing, order.Materials.RefundTo);
        Assert.Equal("run-3", order.AcknowledgeGasLost);
        Assert.True(order.Report.Acts);
        Assert.True(order.Queue);
        Assert.False(order.Allow.Contents || order.Allow.Breach || order.Allow.Broken || order.Allow.Burst ||
                    order.Allow.Unsupported);
        Assert.False(order.IncludeNotes || order.SplitDetail);
        Assert.Null(order.GasTo);

        RemoveRequest arguments = order.Arguments(null, new List<ThingId>());
        Assert.Equal(new[] { 12L, 13L }, arguments.Ids.ConvertAll(id => id.Value).ToArray());
        Assert.Null(arguments.From);
    }

    [Fact]
    public void AReplayWithNoHolderPaysFromThePlayer()
    {
        RemoveStructureOrder order = RemoveStructureOrder.Replay(new long[] { 12 }, null, null, null, acts: false,
            queue: false);

        Assert.IsType<Payer.Player>(Assert.Single(order.Materials.Payers));
        Assert.Same(RefundRoute.Default, order.Materials.RefundTo);
        Assert.False(order.Report.Acts);
        Assert.False(order.Queue);
    }

    private static List<long> Ids(RemoveStructureOrder order) =>
        order.Ids.ConvertAll(id => id.Value);
    private static RemoveStructureOrder? Order(string json, List<OptionProblem> problems) =>
        Order(json, problems, new List<ThingId?> { new ThingId(5) }, new List<ReportProblem>());

    private static RemoveStructureOrder? Order(string json, List<OptionProblem> problems, List<ThingId?> named,
        List<ReportProblem> unread) =>
        RemoveStructureOrder.Of(RemoveStructureOptions.Bind(new OptionReader(RemoveStructureOptions.Spec,
            JObject.Parse(json))), named, unread, problems);
}
