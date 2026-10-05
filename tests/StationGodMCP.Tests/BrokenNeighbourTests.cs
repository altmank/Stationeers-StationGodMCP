#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// Broken pieces a network no longer lists (thing_health and connections network forms), and the one-job place-tool
/// replacement the upgrade tools name when they leave a piece alone (rocket pieces, broken build states, pieces off
/// their line).
/// </summary>
public sealed class BrokenNeighbourTests
{
    // A line: 1 - 2 - 3 on the network; 4 (broken) hangs off 3, 5 (broken) off 4, 6 (intact) off 3, 2 broken itself.
    private static readonly Dictionary<long, long[]> Attached = new Dictionary<long, long[]>
    {
        [1] = new long[] { 2 },
        [2] = new long[] { 1, 3 },
        [3] = new long[] { 2, 4, 6 },
        [4] = new long[] { 3, 5 },
        [5] = new long[] { 4 },
        [6] = new long[] { 3 }
    };

    private static readonly HashSet<long> Broken = new HashSet<long> { 2, 4, 5 };

    private static List<BrokenNeighbour<long>> Search(params long[] members) =>
        BrokenNeighbourSearch.Find(members, static id => id,
            static id => Attached.TryGetValue(id, out long[]? next) ? next : new long[0],
            static id => Broken.Contains(id));

    [Fact]
    public void ABrokenPieceAtAMembersEndThatIsNotAMemberIsFound()
    {
        List<BrokenNeighbour<long>> found = Search(1, 2, 3);

        Assert.Equal(new long[] { 4, 5 }, found.ConvertAll(static n => n.Piece));
        Assert.Equal(3, found[0].Touches);
    }

    [Fact]
    public void TheSearchGoesOnThroughBrokenPiecesOnlyAndNamesTheNearerOne() =>
        Assert.Equal(4, Search(1, 2, 3)[1].Touches);

    [Fact]
    public void ABrokenMemberIsTheListsOwnAndAnIntactNeighbourIsNotReported()
    {
        List<BrokenNeighbour<long>> found = Search(1, 2, 3);

        Assert.DoesNotContain(found, static n => n.Piece == 2);
        Assert.DoesNotContain(found, static n => n.Piece == 6);
    }

    [Fact]
    public void EachBrokenPieceIsReportedOnceThoughSeveralMembersMeetIt()
    {
        List<BrokenNeighbour<long>> found = Search(3, 5);

        Assert.Equal(new long[] { 2, 4 }, found.ConvertAll(static n => n.Piece));
    }

    [Fact]
    public void AWholeNetworkFindsNothing() => Assert.Empty(Search(1, 2, 3, 4, 5, 6));

    [Fact]
    public void TheWarningIsAbsentWithoutBrokenNeighboursAndNamesTheListItCannotShow()
    {
        Assert.Null(BrokenNeighbourSearch.Warning(0, "pipe", "things", "broken_neighbours"));
        Assert.Equal(
            "1 broken pipe piece touches this network at its ends but is not on it, so things does not show it: " +
            "see broken_neighbours.", BrokenNeighbourSearch.Warning(1, "pipe", "things", "broken_neighbours"));
        Assert.Equal(
            "3 broken cable pieces touch this network at its ends but are not on it, so members does not show " +
            "them: see broken_neighbours with summarize true.",
            BrokenNeighbourSearch.Warning(3, "cable", "members", "broken_neighbours with summarize true"));
    }

    [Fact]
    public void TheHealthNetworkFormWiresTheBrokenNeighbours()
    {
        BrokenNeighbourView junction = new BrokenNeighbourView(
            new ThingView(new ThingId(2243415), "StructurePipeCrossJunction3", "Pipe (3-Way Junction)"),
            new PositionView(725, 206.5, 683), "pressure", null, new ThingId(2243420));
        BrokenNeighbourReport report = new BrokenNeighbourReport(new List<BrokenNeighbourView> { junction }, 1,
            BrokenNeighbourSearch.Warning(1, "pipe", "things", "broken_neighbours"));
        HealthNetworkView view = new HealthNetworkView(new ThingId(2243412), "pipe", 6, true,
            Slice<HealthView>.Page(new List<HealthView>(), PageRequest.From(new Args(new JObject()), 8, 500), 0),
            report);

        JObject wire = JObject.Parse(WireCheck.New(view));

        Assert.StartsWith("1 broken pipe piece touches this network", (string)wire["warning"]!);
        Assert.Equal(1, (int)wire["broken_neighbour_count"]!);
        Assert.Equal(
            """{"reference_id":"2243415","prefab_name":"StructurePipeCrossJunction3","position":{"x":725.0,"y":206.5,"z":683.0},"pipe_burst":"pressure","network":null,"touches":"2243420"}""",
            wire["broken_neighbours"]![0]!.ToString(Newtonsoft.Json.Formatting.None));
        Assert.Empty((JArray)wire["things"]!);
    }

    [Fact]
    public void WithoutBrokenNeighboursOnlyTheZeroCountIsSent()
    {
        JObject wire = JObject.Parse(WireCheck.New(new HealthNetworkView(new ThingId(5), "pipe", 0, false,
            Slice<HealthView>.Page(new List<HealthView>(), PageRequest.From(new Args(new JObject()), 8, 500), 0),
            BrokenNeighbourReport.None)));

        Assert.Equal(0, (int)wire["broken_neighbour_count"]!);
        Assert.Null(wire["broken_neighbours"]);
        Assert.Null(wire["warning"]);
    }

    [Fact]
    public void ThePagedMembersFormCarriesTheWarningOnly()
    {
        JObject wire = JObject.Parse(WireCheck.New(new NetworkMembersView(new NetworkRefView("pipe", new ThingId(5)),
            null, Slice<NetworkMemberView>.Page(new List<NetworkMemberView>(),
                PageRequest.From(new Args(new JObject()), 30, 1000), 0), 0, 0,
            BrokenNeighbourSearch.Warning(1, "pipe", "members", "broken_neighbours with summarize true"))));

        Assert.EndsWith("see broken_neighbours with summarize true.", (string)wire["warning"]!);
        Assert.Null(wire["broken_neighbour_count"]);
    }

    // Cells in decimetres; an end's Local is the neighbour's cell, Facing its own (EndCleanup.DirectionOf).
    private static PieceEnd End(GridCell own, int dx, int dy, int dz) =>
        new PieceEnd(new GridCell(own.X + dx, own.Y + dy, own.Z + dz), own, 1, 0);

    [Fact]
    public void AOneCellPieceIsReplacedByThePieceFormAtItsCellWithItsEnds()
    {
        GridCell cell = new GridCell(7250, 2065, 6830);
        PieceModel junction = new PieceModel(2243415, new[] { cell },
            new[] { End(cell, 5, 0, 0), End(cell, -5, 0, 0), End(cell, 0, 0, 5) }, null);

        Assert.Equal(
            "place_pipes {piece: {at: [725, 206.5, 683], ends: [\"+x\", \"-x\", \"+z\"]}, grade: \"gas\", " +
            "remove_ids: [\"2243415\"]}",
            ReplaceInPlace.Call("place_pipes", 2243415, junction, "gas"));
    }

    [Fact]
    public void ALongPieceIsReplacedByWaypointsFromEndToEnd()
    {
        GridCell a = new GridCell(10, 5, 0);
        GridCell b = new GridCell(15, 5, 0);
        GridCell c = new GridCell(20, 5, 0);
        PieceModel straight = new PieceModel(9, new[] { b, c, a }, new[] { End(a, -5, 0, 0), End(c, 5, 0, 0) }, null);

        Assert.Equal("place_cables {waypoints: [[1, 0.5, 0], [2, 0.5, 0]], grade: \"heavy\", remove_ids: [\"9\"]}",
            ReplaceInPlace.Call("place_cables", 9, straight, "heavy"));
    }

    [Fact]
    public void TheAdviceSaysToDryRunFirstAndNamesAMissingGrade()
    {
        GridCell cell = new GridCell(0, 0, 0);
        string advice = ReplaceInPlace.Advice("place_pipes", 7, new PieceModel(7, new[] { cell },
            new[] { End(cell, 0, 5, 0) }, null), null);

        Assert.StartsWith("To replace it in one job, as a player would: place_pipes {piece:", advice);
        Assert.Contains("grade: \"<its grade>\"", advice);
        Assert.EndsWith("(a dry run first, then dry_run false and confirm true).", advice);
    }

    [Theory]
    [InlineData(10L, new long[] { 10, 10 }, true)]
    [InlineData(10L, new long[] { 11, 10 }, true)]
    [InlineData(10L, new long[] { 11, 11 }, false)]
    [InlineData(10L, new long[0], true)]
    [InlineData(null, new long[] { 11 }, false)]
    [InlineData(null, new long[0], true)]
    public void APieceIsOnItsLineWhenAPipeItMeetsSharesItsNetwork(long? own, long[] attached, bool onLine) =>
        Assert.Equal(onLine, LineMembership.OnItsLine(own, System.Array.ConvertAll(attached, static id => (long?)id)));
}
