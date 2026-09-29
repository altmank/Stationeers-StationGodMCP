#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>Fixes from round 5 of the cables live test of 1.4.4.</summary>
public sealed class CablesRound5Tests
{
    private static ThingSnapshot Piece(long id, string tool, string grade) =>
        new ThingSnapshot(id, "StructureCableStraight", new Vec3(0, 0, 0), CubeRotation.All[0], 0, null,
            new NetworkPiece(tool, grade, new List<PieceCell>()));

    private static List<PieceRestore> Runs(params ThingSnapshot[] restore) =>
        new UndoPlan(new List<long>(), new List<ThingSnapshot>(restore), new List<string>(), new List<string>())
            .RestorePieces;

    // cables-38: undoing a tee added onto a trunk removes the job's pieces in the same place_cables job that builds the
    // old straight again, so the trunk is never cut between two jobs (the rebuild was refused would_bridge).
    [Fact]
    public void TheJobsPiecesAreRemovedByThePieceRunThatBuildsTheOldOnesAgain()
    {
        List<PieceRestore> runs = Runs(Piece(2395, "place_cables", "normal"));
        UndoRemovals removals = UndoRemovals.Of(new List<long> { 2776, 2777, 2778 }, runs,
            static _ => "place_cables");

        Assert.Empty(removals.Structures);
        Assert.Equal(new List<long> { 2776, 2777, 2778 }, removals.ByRun[0]);
        Assert.Equal(3, removals.InRuns);
    }

    // A tool with several grades to build again: its first run removes the job's pieces, the later ones none.
    [Fact]
    public void OnlyTheFirstRunOfAToolRemovesItsPieces()
    {
        List<PieceRestore> runs = Runs(Piece(1, "place_cables", "heavy"), Piece(2, "place_cables", "normal"),
            Piece(3, "place_pipes", "gas"));
        UndoRemovals removals = UndoRemovals.Of(new List<long> { 10, 11, 20 }, runs,
            id => id == 20 ? "place_pipes" : "place_cables");

        Assert.Equal(new List<long> { 10, 11 }, removals.ByRun[0]);
        Assert.Empty(removals.ByRun[1]);
        Assert.Equal(new List<long> { 20 }, removals.ByRun[2]);
        Assert.Empty(removals.Structures);
    }

    // What no piece run builds again (a new run with nothing changed, a device, a chute when only cables come back)
    // is still removed by remove_structure first.
    [Fact]
    public void WhatNoPieceRunBuildsAgainGoesThroughRemoveStructure()
    {
        List<PieceRestore> runs = Runs(Piece(1, "place_cables", "normal"));
        UndoRemovals removals = UndoRemovals.Of(new List<long> { 10, 30, 40 }, runs,
            id => id == 10 ? "place_cables" : id == 30 ? "place_chutes" : null);

        Assert.Equal(new List<long> { 30, 40 }, removals.Structures);
        Assert.Equal(new List<long> { 10 }, removals.ByRun[0]);

        UndoRemovals nothingBack = UndoRemovals.Of(new List<long> { 10 }, new List<PieceRestore>(),
            static _ => "place_cables");
        Assert.Equal(new List<long> { 10 }, nothingBack.Structures);
        Assert.Equal(0, nothingBack.InRuns);
    }

    // cables-37: removing the piece between an off full battery's output (371) and a consumer's input (372) splits
    // the network; switched on, each part counts only its own devices: the off output alone flows 0 W, the input alone
    // 0 W. Counted whole (the old way), both parts carried the network's 7900 W and warned.
    [Fact]
    public void ASplitNetworksPartsAreCountedFromTheirOwnDevicesWhenSwitchedOn()
    {
        ForecastPort output = new ForecastPort(371, 1, true, 7, 7);
        ForecastPort input = new ForecastPort(372, 0, true, 7, 7);
        Dictionary<long, NetworkPower> before = new Dictionary<long, NetworkPower>
        {
            [7] = new NetworkPower(3548350.0, 7900.0, 5000.0, null)
        };
        Dictionary<ForecastPort, PortPower> now = new Dictionary<ForecastPort, PortPower>
        {
            [output] = new PortPower(0.0, 0.0),
            [input] = new PortPower(0.0, 7900.0)
        };
        Dictionary<ForecastPort, PortPower> dormant = new Dictionary<ForecastPort, PortPower>
        {
            [output] = new PortPower(3548350.0, 0.0)
        };
        HashSet<long> split = new HashSet<long> { 7 };

        PowerAfter sourcePart = WhenOn(Part(output), before, now, dormant, split);
        PowerAfter sinkPart = WhenOn(Part(input), before, now, dormant, split);
        PowerAfter whole = WhenOn(Part(output), before, now, dormant, null);

        Assert.Equal(0.0, sourcePart.FlowW);
        Assert.False(sourcePart.Overloads);
        Assert.Equal(0.0, sinkPart.FlowW);
        Assert.False(sinkPart.Overloads);
        Assert.True(whole.Overloads);
    }

    // A part that keeps both an off source and the consumer still warns: the split changes nothing between them.
    [Fact]
    public void APartHoldingTheOffSourceAndTheConsumerStillOverloadsWhenOn()
    {
        ForecastPort output = new ForecastPort(371, 1, true, 7, 7);
        ForecastPort input = new ForecastPort(372, 0, true, 7, 7);
        Dictionary<long, NetworkPower> before = new Dictionary<long, NetworkPower>
        {
            [7] = new NetworkPower(0.0, 7900.0, 5000.0, null)
        };
        Dictionary<ForecastPort, PortPower> now = new Dictionary<ForecastPort, PortPower>
        {
            [output] = new PortPower(0.0, 0.0),
            [input] = new PortPower(0.0, 7900.0)
        };
        Dictionary<ForecastPort, PortPower> dormant = new Dictionary<ForecastPort, PortPower>
        {
            [output] = new PortPower(3548350.0, 0.0)
        };

        PowerAfter part = WhenOn(Part(output, input), before, now, dormant, new HashSet<long> { 7 });

        Assert.Equal(7900.0, part.FlowW);
        Assert.True(part.Overloads);
    }

    private static ForecastNetwork Part(params ForecastPort[] ports)
    {
        ForecastNetwork part = new ForecastNetwork(0);
        part.NetworksBefore.Add(7);
        part.Ports.AddRange(ports);
        return part;
    }

    private static PowerAfter WhenOn(ForecastNetwork part, Dictionary<long, NetworkPower> before,
        Dictionary<ForecastPort, PortPower> now, Dictionary<ForecastPort, PortPower> dormant, HashSet<long>? split) =>
        PowerAfter.Of(part, before, new Dictionary<long, double>(), new HashSet<long>(),
            port => now.TryGetValue(port, out PortPower found) ? found : null,
            port => dormant.TryGetValue(port, out PortPower found) ? found : null, split);

    // cables-39: a candidate remove_redundant leaves is reported with remove_redundant's own reason (a keep_ids piece
    // was counted nowhere, a needed one got simplify_junctions' "minimal" wording).
    [Fact]
    public void KeptCandidatesSayWhyRemoveRedundantLeftThem()
    {
        List<Link> links = new List<Link> { new Link(1, 2), new Link(2, 3) };
        RedundancyResult kept = RedundantPieces.Find(new long[] { 1, 2, 3 }, links, new HashSet<long>(),
            new long[] { 1, 2, 3 }, new HashSet<long> { 2 }, new Dictionary<long, string> { [3] = "fuse" },
            new HashSet<long>());

        Assert.Equal(new List<long> { 1 }, kept.Removed);
        Assert.Contains("keep_ids", kept.Kept.Find(static piece => piece.Id == 2)!.Message);
        Assert.Contains("(fuse)", kept.Kept.Find(static piece => piece.Id == 3)!.Message);

        KeptPiece needed = new KeptPiece(5, RedundantPieces.Needed, new List<long>(), 4);
        Assert.Contains("split the network (4 piece(s) cut off)", needed.Message);
        KeptPiece feeding = new KeptPiece(6, RedundantPieces.Needed, new List<long> { 374 }, 1);
        Assert.Contains("cut 374 off the root", feeding.Message);
        KeptPiece port = new KeptPiece(7, RedundantPieces.DevicePort, new List<long> { 372 }, 0);
        Assert.Contains("joins a port of 372", port.Message);
    }
}
