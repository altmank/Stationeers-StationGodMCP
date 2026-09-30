#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>place_structure: two placements of one request in one slot.</summary>
public sealed class PlacementSpotTests
{
    private static GridStep Step(string name)
    {
        Assert.True(GridStep.TryParse(name, out GridStep step));
        return step;
    }

    // Two plates on the face x = -1310, one facing into each cell: the game keeps one per side.
    [Fact]
    public void WallsBackToBackOnOneFaceAreTwoSlots()
    {
        PlacementSpot east = PlacementSpot.Of(-1310, 221, -707, "Face", Step("+x"));
        PlacementSpot west = PlacementSpot.Of(-1310, 221, -707, "Face", Step("-x"));
        Assert.NotEqual(east, west);
    }

    [Fact]
    public void TwoWallsOnOneSideOfAFaceOverlap()
    {
        Assert.Equal(PlacementSpot.Of(-1310, 221, -707, "Face", Step("+x")),
            PlacementSpot.Of(-1310.004, 221, -707, "Face", Step("+x")));
    }

    [Fact]
    public void GridPiecesOverlapByPositionAndSlotOnly()
    {
        Assert.Equal(PlacementSpot.Of(1, 3, 5, "Grid", null), PlacementSpot.Of(1, 3, 5, "Grid", null));
        Assert.NotEqual(PlacementSpot.Of(1, 3, 5, "small:Battery", null), PlacementSpot.Of(1, 3, 5, "Grid", null));
        Assert.NotEqual(PlacementSpot.Of(1, 3, 5, "Grid", null), PlacementSpot.Of(1, 3, 5.5, "Grid", null));
    }
}

/// <summary>place_structure: where the cursor sets a small-grid device down when aimed at a point in a cell.</summary>
public sealed class CursorAimTests
{
    private static GridStep Step(string name)
    {
        Assert.True(GridStep.TryParse(name, out GridStep step));
        return step;
    }

    // Live 1.3.0: a battery at the cell centre (y 221 on a floor at 220) stayed in the air and was refused
    // "requires a Frame below".
    [Theory]
    [InlineData(221.0, 220.0)]
    [InlineData(220.2, 220.0)]
    [InlineData(221.9, 220.0)]
    [InlineData(220.0, 220.0)]
    [InlineData(222.0, 222.0)]
    [InlineData(-3.0, -4.0)]
    public void APointInACellDropsToTheFloorPlaneBelow(double y, double floor)
    {
        (double x, double onFloor, double z) = CursorAim.OntoFloor(-1310, y, -707.5, Step("+y"));
        Assert.Equal(floor, onFloor, 6);
        Assert.Equal(-1310, x, 6);
        Assert.Equal(-707.5, z, 6);
    }

    [Fact]
    public void APieceUpsideDownDropsOntoTheCeilingAbove()
    {
        Assert.Equal(222.0, CursorAim.OntoFloor(0, 221, 0, Step("-y")).Y, 6);
    }

    // A mounted device (a transformer, a vent) facing +z mounts on the face behind it, at the cell's -z plane.
    [Fact]
    public void AMountedPieceMovesOntoTheFaceAtItsBack()
    {
        Assert.Equal((-1307.0, 227.0, -714.0), CursorAim.OntoFloor(-1307, 227, -713, Step("+z")));
    }

    [Fact]
    public void APieceOnItsSideDropsAlongItsOwnDown()
    {
        (double x, double y, double z) = CursorAim.OntoFloor(-1309, 221, -707, Step("+x"));
        Assert.Equal((-1310.0, 221.0, -707.0), (x, y, z));
    }
}

/// <summary>Messages give positions in metres, as players and the tools' arguments do.</summary>
public sealed class GridTextTests
{
    // Live 1.3.0: "Cell (-13060, 2200, -7075)" for the cell at (-1306, 220, -707.5).
    [Fact]
    public void CellsAndPointsPrintInMetres()
    {
        Assert.Equal("(-1306, 220, -707.5)", new GridCell(-13060, 2200, -7075).ToString());
        Assert.Equal("(-1306, 220, -707.5)", new GridPoint(-13060, 2200, -7075).ToString());
        Assert.Equal("(0.5, -0.5, 1)", GridText.Metres(5, -5, 10));
    }
}

/// <summary>remove_structure: one would_breach per face.</summary>
public sealed class BreachedFacesTests
{
    [Fact]
    public void TwoPlatesBackToBackReportTheirFaceOnce()
    {
        BreachedFaces breached = new BreachedFaces();
        GridPoint face = new GridPoint(-13100, 2210, -7070);
        Assert.True(breached.Claim(new[] { face }));
        Assert.False(breached.Claim(new[] { face }));
    }

    [Fact]
    public void APieceOpeningAFaceNotYetNamedStillReports()
    {
        BreachedFaces breached = new BreachedFaces();
        GridPoint first = new GridPoint(0, 10, 20);
        GridPoint second = new GridPoint(20, 10, 0);
        Assert.True(breached.Claim(new[] { first }));
        Assert.True(breached.Claim(new[] { first, second }));
        Assert.False(breached.Claim(new List<GridPoint>()));
    }
}

/// <summary>plan_removal on a dedicated server: the refund is only priced.</summary>
public sealed class SourceRuleTests
{
    [Fact]
    public void NoLocalPlayerIsOnlyAWarningForPlanRemoval()
    {
        Assert.Equal(GuardLevel.Warning, SourceRule.NoLocalPlayer(SourceRule.PlanRemoval));
        Assert.Equal(GuardLevel.Refusal, SourceRule.NoLocalPlayer("remove_cables"));
        Assert.Equal(GuardLevel.Refusal, SourceRule.NoLocalPlayer("place_cables"));
    }
}

/// <summary>The run log's created_by_part.</summary>
public sealed class BuiltPartTests
{
    private static GridCell At(int x, int y, int z) => RunModels.At(x, y, z);

    // Live 1.3.0: the trunk piece a tap changed into a junction was listed under "run".
    [Fact]
    public void ATrunkPieceTheTapChangedIsJoined()
    {
        RunShape shape = RunShape.Of(new[] { At(0, 0, 0), At(1, 0, 0) }, new List<RunBranch>(), out _)!;
        RunShape tapped = shape.WithTap(At(1, 0, 0), new[] { At(2, 0, 0), At(3, 0, 0) }, out _)!;

        Assert.Equal("joined", RunShape.BuiltPart(tapped, At(3, 0, 0), true));
        Assert.Equal("run", RunShape.BuiltPart(tapped, At(2, 0, 0), false));
    }

    [Fact]
    public void BranchesAndFillsKeepTheirNames()
    {
        RunShape shape = RunShape.Of(new[] { At(0, 0, 0), At(1, 0, 0) },
            new List<RunBranch> { new RunBranch(new[] { At(1, 0, 1) }, null) }, out _)!;
        Assert.Equal("branch 0", RunShape.BuiltPart(shape, At(1, 0, 1), false));
        Assert.Equal("fill", RunShape.BuiltPart(shape, At(9, 0, 0), false));
        Assert.Equal("fill", RunShape.BuiltPart(null, At(9, 0, 0), false));
        Assert.Equal("joined", RunShape.BuiltPart(null, At(9, 0, 0), true));
    }

    // The pieces form (undo_job's rebuilds) was logged under "run".
    [Fact]
    public void PiecesFormIsLoggedAsPieces()
    {
        RunShape pieces = RunShape.Pieces(new[] { At(0, 0, 0), At(4, 0, 0) }, out _)!;

        Assert.Equal("pieces", RunShape.BuiltPart(pieces, At(4, 0, 0), false));
        Assert.Equal("joined", RunShape.BuiltPart(pieces, At(0, 0, 0), true));
        Assert.Equal("pieces", RunShape.BuiltPart(pieces.WithFills(new Dictionary<GridCell, EndSet>()), At(0, 0, 0),
            false));
        Assert.Equal("run", RunShape.BuiltPart(RunShape.Line(new[] { At(0, 0, 0) }), At(0, 0, 0), false));
    }
}

/// <summary>The planners' resolved_networks for to's handle.</summary>
public sealed class JoinTargetTests
{
    [Fact]
    public void ANetworkIdTargetIsRecordedUnderToNetworkId()
    {
        JToken? handle = NetworkHandle.TargetOf(JObject.Parse("{\"network_id\": \"140001\"}"), out string argument);
        Assert.Equal("to.network_id", argument);
        Assert.Equal("140001", (string?)handle);
    }

    [Fact]
    public void ADevicePortTargetIsRecordedUnderTo()
    {
        JObject handle = Assert.IsType<JObject>(NetworkHandle.TargetOf(
            JObject.Parse("{\"reference_id\": \"1378\", \"port\": 1, \"at\": [1, 2, 3]}"), out string argument));
        Assert.Equal("to", argument);
        Assert.Equal("1378", (string?)handle["reference_id"]);
        Assert.Equal(1, (int)handle["port"]!);

        JObject piece = Assert.IsType<JObject>(NetworkHandle.TargetOf(
            JObject.Parse("{\"reference_id\": \"7\", \"port\": null}"), out _));
        Assert.Null(piece["port"]);
        Assert.Null(NetworkHandle.TargetOf(JObject.Parse("{\"at\": [1, 2, 3]}"), out _));
    }

    // Live 1.3.0: a plan listed both "to" and "to.network_id" for one handle.
    [Fact]
    public void OneHandleIsListedOnce()
    {
        ResolvedNetworks.Begin();
        NetworkHandle handle = NetworkHandle.Read(new JValue("5001"), "to.network_id");
        ResolvedNetworks.Record("to.network_id", handle, new ThingId(140001));
        ResolvedNetworks.Record("join_to", handle, new ThingId(140001));
        ResolvedNetworks.Record("to.network_id", handle, new ThingId(140001));
        ResolvedNetworks.Record("allow_bridge[0]", NetworkHandle.Read(new JValue("5002"), "x"), new ThingId(140002));

        JObject json = JObject.Parse(WireCheck.New(ResolvedNetworks.Attach(new GameClockView(1f, false, 0.5f, 3),
            ResolvedNetworks.Take())));
        JArray entries = Assert.IsType<JArray>(json["resolved_networks"]);
        Assert.Equal(2, entries.Count);
        Assert.Equal("to.network_id", (string?)entries[0]["argument"]);
        Assert.Equal("allow_bridge[0]", (string?)entries[1]["argument"]);
    }
}
