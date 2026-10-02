#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>replace_walls and replace_frames: face and slot arithmetic in Grid3 decimetres.</summary>
public sealed class FaceMathTests
{
    [Fact]
    public void AFacePointSeparatesTheCellsOnEitherSideOfItsAxis()
    {
        Assert.True(FaceMath.TrySplitFace(new GridPoint(20, 10, -30), out GridPoint a, out GridPoint b));
        Assert.Equal(new GridPoint(10, 10, -30), a);
        Assert.Equal(new GridPoint(30, 10, -30), b);

        Assert.True(FaceMath.TrySplitFace(new GridPoint(10, -40, 10), out a, out b));
        Assert.Equal(new GridPoint(10, -50, 10), a);
        Assert.Equal(new GridPoint(10, -30, 10), b);
    }

    [Fact]
    public void ACellCentreAnEdgeOrAnOffGridPointIsNoFace()
    {
        Assert.False(FaceMath.TrySplitFace(new GridPoint(10, 10, 10), out _, out _));
        Assert.False(FaceMath.TrySplitFace(new GridPoint(20, 20, 10), out _, out _));
        Assert.False(FaceMath.TrySplitFace(new GridPoint(25, 10, 10), out _, out _));
        Assert.True(FaceMath.IsCellCentre(new GridPoint(-10, 30, 50)));
    }

    [Fact]
    public void AFacePointRegistersInTheCellOnItsOriginsSide()
    {
        GridPoint face = new GridPoint(20, 10, 10);
        Assert.Equal(new GridPoint(10, 10, 10), FaceMath.CellOnOriginSide(face, 1.5f, 1f, 1f));
        Assert.Equal(new GridPoint(30, 10, 10), FaceMath.CellOnOriginSide(face, 2.5f, 1f, 1f));
        // Exactly on the face, Grid3.GridCenter counts the origin as past it (num <= origin).
        Assert.Equal(new GridPoint(30, 10, 10), FaceMath.CellOnOriginSide(face, 2f, 1f, 1f));

        GridPoint negative = new GridPoint(-20, 10, 10);
        Assert.Equal(new GridPoint(-10, 10, 10), FaceMath.CellOnOriginSide(negative, -1.5f, 1f, 1f));
        Assert.Equal(new GridPoint(-30, 10, 10), FaceMath.CellOnOriginSide(negative, -2.5f, 1f, 1f));
    }

    [Fact]
    public void ACellCentreRegistersInItselfWhateverTheOrigin()
    {
        GridPoint centre = new GridPoint(30, -10, 50);
        Assert.Equal(centre, FaceMath.CellOnOriginSide(centre, -100f, 100f, 0f));
    }

    [Fact]
    public void DecimetresRoundHalfAwayFromZeroAsGrid3Does()
    {
        Assert.Equal(13, FaceMath.Decimetres(1.25f));
        Assert.Equal(-13, FaceMath.Decimetres(-1.25f));
        Assert.Equal(20, FaceMath.Decimetres(2.0f));
    }

    [Fact]
    public void NeighboursAndFacesAreOneCellAndHalfACellAway()
    {
        GridPoint cell = new GridPoint(10, 10, 10);
        Assert.Equal(new[] { new GridPoint(30, 10, 10), new GridPoint(-10, 10, 10), new GridPoint(10, 30, 10),
            new GridPoint(10, -10, 10), new GridPoint(10, 10, 30), new GridPoint(10, 10, -10) },
            FaceMath.Neighbours(cell));
        Assert.Equal(new[] { new GridPoint(20, 10, 10), new GridPoint(0, 10, 10), new GridPoint(10, 20, 10),
            new GridPoint(10, 0, 10), new GridPoint(10, 10, 20), new GridPoint(10, 10, 0) },
            FaceMath.FacesOf(cell));
    }

    [Fact]
    public void TheSameSlotsInAnyOrderAreTheSameFootprint()
    {
        StructureSlot east = new StructureSlot(new GridPoint(10, 10, 10), new GridPoint(20, 10, 10));
        StructureSlot up = new StructureSlot(new GridPoint(10, 10, 10), new GridPoint(10, 20, 10));
        Assert.True(FaceMath.SameSlots(new[] { east, up }, new[] { up, east }));
        Assert.Equal(new GridPoint(10, 0, 0), east.Offset);
    }

    [Fact]
    public void TheSameFacePointInTheOtherCellIsADifferentFootprint()
    {
        StructureSlot fromWest = new StructureSlot(new GridPoint(10, 10, 10), new GridPoint(20, 10, 10));
        StructureSlot fromEast = new StructureSlot(new GridPoint(30, 10, 10), new GridPoint(20, 10, 10));
        Assert.False(FaceMath.SameSlots(new[] { fromWest }, new[] { fromEast }));
        Assert.False(FaceMath.SameSlots(new[] { fromWest }, new[] { fromWest, fromEast }));
    }
}

/// <summary>replace_walls and replace_frames: the game's material rule and the per-swap netting.</summary>
public sealed class MaterialRuleTests
{
    private const int Kit = 1;
    private const int IronSheets = 2;
    private const int Steel = 3;
    private const int Welder = 9;

    private static List<IReadOnlyList<BuildEntry>> States(params BuildEntry[][] states) =>
        states.Select(state => (IReadOnlyList<BuildEntry>)state.ToList()).ToList();

    [Fact]
    public void StatesUpToTheLastAreSummedPerItemSkippingToolsAndZeros()
    {
        List<IReadOnlyList<BuildEntry>> frame = States(
            new[] { new BuildEntry(Kit, 1, false) },
            new[] { new BuildEntry(IronSheets, 2, false), new BuildEntry(Welder, 1, true) },
            new[] { new BuildEntry(IronSheets, 2, false), new BuildEntry(Steel, 0, false) });

        List<ItemCount> all = MaterialRule.Totals(frame, 2);
        Assert.Equal(new[] { (Kit, 1), (IronSheets, 4) }, all.Select(c => (c.Item, c.Quantity)));

        List<ItemCount> first = MaterialRule.Totals(frame, 0);
        Assert.Equal(new[] { (Kit, 1) }, first.Select(c => (c.Item, c.Quantity)));
        Assert.Empty(MaterialRule.Totals(frame, -1));
    }

    [Fact]
    public void ASwapChargesTheShortfallAndGivesBackTheSurplusPerItem()
    {
        List<ItemCount> cost = new List<ItemCount> { new ItemCount(Kit, 1), new ItemCount(Steel, 4) };
        List<ItemCount> refund = new List<ItemCount> { new ItemCount(Kit, 1), new ItemCount(IronSheets, 4) };

        List<MaterialLine> lines = MaterialRule.Net(cost, refund);

        Assert.Equal(new[] { Kit, Steel, IronSheets }, lines.Select(line => line.Item));
        MaterialLine kit = lines[0];
        Assert.Equal((0, 0, 0), (kit.Net, kit.Charge, kit.GiveBack));
        MaterialLine steel = lines[1];
        Assert.Equal((4, 4, 0), (steel.Net, steel.Charge, steel.GiveBack));
        MaterialLine iron = lines[2];
        Assert.Equal((-4, 0, 4), (iron.Net, iron.Charge, iron.GiveBack));
    }

    [Fact]
    public void FinishingAFrameInPlaceChargesOnlyTheStatesItLacks()
    {
        List<IReadOnlyList<BuildEntry>> frame = States(
            new[] { new BuildEntry(Kit, 1, false) },
            new[] { new BuildEntry(IronSheets, 2, false), new BuildEntry(Welder, 1, true) },
            new[] { new BuildEntry(IronSheets, 2, false) });

        List<MaterialLine> lines = MaterialRule.Net(MaterialRule.Totals(frame, 2), MaterialRule.Totals(frame, 0));

        Assert.Equal(new[] { (Kit, 0), (IronSheets, 4) }, lines.Select(line => (line.Item, line.Charge)));
        Assert.All(lines, line => Assert.Equal(0, line.GiveBack));
    }

    [Fact]
    public void RunTotalsSumEachSwapsChargeAndGiveBackWithoutNettingAcrossSwaps()
    {
        List<MaterialLine> first = new List<MaterialLine> { new MaterialLine(Steel, 2, 0) };
        List<MaterialLine> second = new List<MaterialLine> { new MaterialLine(Steel, 0, 2) };

        List<MaterialTotal> totals = MaterialRule.Sum(new[] { first, second });

        MaterialTotal steel = Assert.Single(totals);
        Assert.Equal((2, 2, 2, 2), (steel.Cost, steel.Refund, steel.Charge, steel.GiveBack));
    }
}

/// <summary>replace_walls and replace_frames: never open, and the game's wall stress rule.</summary>
public sealed class AirRuleTests
{
    private static PieceBlocking Blocks(bool air, bool gravity) => new PieceBlocking(air, gravity);

    [Fact]
    public void AnAirtightPieceForAnAirtightPieceKeeps()
    {
        Assert.Equal(AirChange.Keeps, AirRule.Judge(Blocks(true, true), Blocks(true, true)));
        Assert.Equal(AirChange.Keeps, AirRule.Judge(Blocks(false, false), Blocks(false, false)));
    }

    [Fact]
    public void ALeakyPieceMadeAirtightSeals()
    {
        Assert.Equal(AirChange.Seals, AirRule.Judge(Blocks(false, true), Blocks(true, true)));
        Assert.Equal(AirChange.Seals, AirRule.Judge(Blocks(false, false), Blocks(true, true)));
    }

    [Fact]
    public void LettingThroughAirOrGravityTheOldPieceBlockedWouldOpen()
    {
        Assert.Equal(AirChange.WouldOpen, AirRule.Judge(Blocks(true, true), Blocks(false, true)));
        Assert.Equal(AirChange.WouldOpen, AirRule.Judge(Blocks(true, true), Blocks(true, false)));
        Assert.Equal(AirChange.WouldOpen, AirRule.Judge(Blocks(false, true), Blocks(true, false)));
    }

    [Fact]
    public void AFaceAtOrAboveItsSummedRatingWithTheNewWallIsOverstressed()
    {
        Assert.Equal(StressVerdict.Overstressed, WallStress.Judge(new FaceLoad(150, 50, false), 100, 0.8));
        Assert.Equal(StressVerdict.Overstressed, WallStress.Judge(new FaceLoad(200, 0, false), 100, 0.8));
    }

    [Fact]
    public void AboveTheNewWallsStressMarkIsAWarningOnly()
    {
        Assert.Equal(StressVerdict.Stressed, WallStress.Judge(new FaceLoad(81, 0, false), 100, 0.8));
        Assert.Equal(StressVerdict.Ok, WallStress.Judge(new FaceLoad(80, 0, false), 100, 0.8));
    }

    [Fact]
    public void AShieldedFaceOrAWallWithoutARatingIsNeverStressed()
    {
        Assert.Equal(StressVerdict.Ok, WallStress.Judge(new FaceLoad(1000, 0, true), 100, 0.8));
        Assert.Equal(StressVerdict.Ok, WallStress.Judge(new FaceLoad(1000, 0, false), -1, 0.8));
    }
}

/// <summary>replace_walls and replace_frames: the room check after the game re-evaluated its rooms.</summary>
public sealed class RoomDiffTests
{
    private static HashSet<GridPoint> Row(params int[] xs) =>
        new HashSet<GridPoint>(xs.Select(x => new GridPoint(x, 10, 10)));

    private static RoomRecord Room(long id, HashSet<GridPoint> cells, double mol, double energy) =>
        new RoomRecord(id, cells, mol, energy);

    [Fact]
    public void TheSameRoomWithTheSameCellsAndAirHasNoProblem()
    {
        RoomOutcome outcome = RoomDiff.Compare(Room(5, Row(10, 30, 50), 100, 1e6),
            Room(5, Row(10, 30, 50), 100.4, 1e6 + 5000), RoomExpectation.None);

        Assert.Empty(outcome.Problems);
        Assert.Equal(3, outcome.ExpectedCells);
        Assert.Equal(1.0, outcome.Tolerance.Mol, 6);
        Assert.Equal(10000.0, outcome.Tolerance.EnergyJ, 6);
    }

    [Fact]
    public void ARoomThatNoLongerExistsIsGone()
    {
        RoomOutcome outcome = RoomDiff.Compare(Room(5, Row(10, 30), 10, 1000), null, RoomExpectation.None);
        Assert.Equal(new[] { RoomDiff.RoomGone }, outcome.Problems);
        Assert.Null(outcome.DeltaMol);
    }

    [Fact]
    public void ACellASealingFrameNowBlocksIsExpectedToLeave()
    {
        RoomExpectation sealedCell = new RoomExpectation(Row(50), 0, 0);
        RoomOutcome outcome = RoomDiff.Compare(Room(5, Row(10, 30, 50), 100, 1e6), Room(5, Row(10, 30), 100, 1e6),
            sealedCell);

        Assert.Empty(outcome.Problems);
        Assert.Equal(2, outcome.ExpectedCells);
    }

    [Fact]
    public void AnUnexpectedCellLostOrGainedChangesTheRoom()
    {
        RoomOutcome lost = RoomDiff.Compare(Room(5, Row(10, 30, 50), 100, 1e6), Room(5, Row(10, 30), 100, 1e6),
            RoomExpectation.None);
        Assert.Equal(new[] { RoomDiff.RoomCellsChanged }, lost.Problems);
        Assert.Equal((1, 0), (lost.Cells.Lost, lost.Cells.Gained));

        RoomOutcome gained = RoomDiff.Compare(Room(5, Row(10), 100, 1e6), Room(5, Row(10, 30), 100, 1e6),
            RoomExpectation.None);
        Assert.Equal((0, 1), (gained.Cells.Lost, gained.Cells.Gained));
    }

    [Fact]
    public void AirOutsideTheToleranceChangesTheRoom()
    {
        // A small room: the 0.5 mol floor applies; a large one: 1 %.
        Assert.Empty(RoomDiff.Compare(Room(5, Row(10), 10, 1e5), Room(5, Row(10), 10.5, 1e5),
            RoomExpectation.None).Problems);
        Assert.Equal(new[] { RoomDiff.RoomAirChanged }, RoomDiff.Compare(Room(5, Row(10), 10, 1e5),
            Room(5, Row(10), 10.6, 1e5), RoomExpectation.None).Problems);
        Assert.Empty(RoomDiff.Compare(Room(5, Row(10), 1000, 1e8), Room(5, Row(10), 990, 1e8),
            RoomExpectation.None).Problems);
        Assert.Equal(new[] { RoomDiff.RoomAirChanged }, RoomDiff.Compare(Room(5, Row(10), 1000, 1e8),
            Room(5, Row(10), 1000, 1.02e8), RoomExpectation.None).Problems);
    }

    [Fact]
    public void GasOfASealedCellMayMoveWithinItsAllowance()
    {
        RoomExpectation divided = new RoomExpectation(new HashSet<GridPoint>(), 3, 2000);
        Assert.Empty(RoomDiff.Compare(Room(5, Row(10), 10, 1e5), Room(5, Row(10), 13.4, 1e5 + 2900), divided)
            .Problems);
        Assert.Equal(new[] { RoomDiff.RoomAirChanged },
            RoomDiff.Compare(Room(5, Row(10), 10, 1e5), Room(5, Row(10), 13.6, 1e5), divided).Problems);
    }
}

/// <summary>replace_walls and replace_frames: the request forms.</summary>
public sealed class StructureSwapArgsTests
{
    private static StructureSwapForm Parse(string json, bool targetRequired = true) =>
        StructureSwapArgs.Parse(new Args(JObject.Parse(json)), targetRequired);

    private static ApiException Refusal(string json, bool targetRequired = true) =>
        Assert.Throws<ApiException>(() => Parse(json, targetRequired));

    [Fact]
    public void ADryRunIsTheDefaultWithTheToolsDefaults()
    {
        StructureSwapForm.Run run = Assert.IsType<StructureSwapForm.Run>(
            Parse("{\"to\": \" StructureCompositeWall \", \"reference_ids\": [\"11\", 12]}"));

        Assert.False(run.Confirmed);
        StructureSwapArguments arguments = run.Arguments;
        Assert.Equal("StructureCompositeWall", arguments.To);
        StructureScope.Pieces pieces = Assert.IsType<StructureScope.Pieces>(arguments.Scope);
        Assert.Equal(new[] { 11L, 12L }, pieces.Ids.Select(id => id.Value));
        Assert.Null(arguments.FromPrefabs);
        Assert.Null(arguments.From);
        Assert.Equal((true, false, StructureSwapArgs.DefaultLimit),
            (arguments.Refund, arguments.SkipUnmatched, arguments.Limit));
    }

    [Fact]
    public void ARoomIdIsTakenAsAStringOrANumber()
    {
        Assert.Equal(4711L, Assert.IsType<StructureScope.Room>(
            ((StructureSwapForm.Run)Parse("{\"to\": \"W\", \"room_id\": \"4711\"}")).Arguments.Scope).Id);
        Assert.Equal(4711L, Assert.IsType<StructureScope.Room>(
            ((StructureSwapForm.Run)Parse("{\"to\": \"W\", \"room_id\": 4711}")).Arguments.Scope).Id);
        Assert.Equal("invalid_argument", Refusal("{\"to\": \"W\", \"room_id\": \"kitchen\"}").Code);
    }

    [Fact]
    public void ExactlyOneScopeIsNeeded()
    {
        Assert.Equal("invalid_argument",
            Refusal("{\"to\": \"W\", \"room_id\": \"1\", \"reference_ids\": [\"2\"]}").Code);
        Assert.Equal("invalid_argument", Refusal("{\"to\": \"W\"}").Code);
    }

    [Fact]
    public void WallsNeedATargetAndFramesDoNot()
    {
        ApiException missing = Refusal("{\"room_id\": \"1\"}");
        Assert.Equal("invalid_argument", missing.Code);
        Assert.Contains("'to'", missing.Message);

        StructureSwapForm.Run frames = Assert.IsType<StructureSwapForm.Run>(Parse("{\"room_id\": \"1\"}", false));
        Assert.Null(frames.Arguments.To);
        Assert.Equal("invalid_argument", Refusal("{\"to\": \"  \", \"room_id\": \"1\"}", false).Code);
    }

    [Fact]
    public void ARealRunNeedsDryRunFalseAndConfirm()
    {
        Assert.Equal("confirm_required", Refusal("{\"to\": \"W\", \"room_id\": \"1\", \"dry_run\": false}").Code);
        Assert.Equal("invalid_argument", Refusal("{\"to\": \"W\", \"room_id\": \"1\", \"confirm\": true}").Code);
        Assert.True(Assert.IsType<StructureSwapForm.Run>(
            Parse("{\"to\": \"W\", \"room_id\": \"1\", \"dry_run\": false, \"confirm\": true}")).Confirmed);
    }

    [Fact]
    public void AJobIdStandsAlone()
    {
        Assert.Equal("replace-3", Assert.IsType<StructureSwapForm.Poll>(Parse("{\"job_id\": \" replace-3 \"}")).JobId);
        ApiException mixed = Refusal("{\"job_id\": \"replace-3\", \"room_id\": \"1\"}");
        Assert.Equal("invalid_argument", mixed.Code);
        Assert.Contains("'room_id'", mixed.Message);
    }

    [Fact]
    public void FromPrefabsAreNamesAndTheOtherOptionsAreRead()
    {
        StructureSwapForm.Run run = Assert.IsType<StructureSwapForm.Run>(Parse(
            "{\"to\": \"W\", \"room_id\": \"1\"," +
            " \"from_prefabs\": [\"StructureWallIron\", \" StructureWallIron02 \"]," +
            " \"from_id\": \"77\", \"refund\": false, \"skip_unmatched\": true, \"limit\": 5}"));

        Assert.Equal(new[] { "StructureWallIron", "StructureWallIron02" }, run.Arguments.FromPrefabs);
        Assert.Equal(77L, run.Arguments.From!.Value.Value);
        Assert.Equal((false, true, 5), (run.Arguments.Refund, run.Arguments.SkipUnmatched, run.Arguments.Limit));
        Assert.Equal("invalid_argument",
            Refusal("{\"to\": \"W\", \"room_id\": \"1\", \"from_prefabs\": [\"\"]}").Code);
        Assert.Equal("invalid_argument", Refusal("{\"to\": \"W\", \"room_id\": \"1\", \"limit\": 0}").Code);
    }
}

/// <summary>replace_walls and replace_frames: reply shapes the tool descriptions promise.</summary>
public sealed class StructureSwapWireTests
{
    private static StructureSwapReportView Report(List<UpgradeProblemView> problems, string status = "dry_run",
        string? jobId = null)
    {
        UpgradeHeader header = new UpgradeHeader("replace_walls", "StructureCompositeWall", status, jobId,
            new List<string>());
        StructureFaceView face = new StructureFaceView(new PositionView(2, 1, 1),
            new List<PositionView> { new PositionView(1, 1, 1), new PositionView(3, 1, 1) },
            new StructureFacePressure(101.3, 0.5, 100, 400), "ok");
        StructurePieceView piece = new StructurePieceView(
            new ThingView(new ThingId(301), "StructureWallIron", "Iron Wall"), new PositionView(2, 1, 1),
            new RotationView(0, 90, 0), new StructureTargetView("StructureCompositeWall", 1, 1),
            new StructureBlockingView(true, true, true, true, "keeps", false), new List<StructureFaceView> { face },
            new List<string> { "88" },
            new List<StructureMaterialLineView>
            {
                new StructureMaterialLineView("ItemKitWall", 1, 0, refundEnabled: true),
                new StructureMaterialLineView("ItemIronSheets", 0, 2, refundEnabled: true)
            });
        StructureSwapLists lists = new StructureSwapLists(problems, new List<StructurePieceView> { piece },
            new List<StructureMappingView>
            {
                new StructureMappingView("StructureWallIron", "StructureCompositeWall", 1)
            },
            new List<UpgradeSkippedView>(), new List<UpgradeSkippedView>());
        StructureSwapResources resources = new StructureSwapResources(
            new ThingView(new ThingId(7), "Character", "Player"),
            new List<StructureMaterialView>
            {
                new StructureMaterialView("ItemKitWall", "Kit (Wall)", new StructureMaterialCounts(1, 0, 1, 0, 3),
                    new List<UpgradeStackView> { new UpgradeStackView(new ThingId(55), 3, new ThingId(7), 2) })
            },
            true, new List<StructureRoomView> { new StructureRoomView("88", 12, 480.5, 2.1e7) });
        return new StructureSwapReportView(header, new UpgradeCounts(3, 1, 2, 0), lists, resources);
    }

    [Fact]
    public void ADryRunReportsEachPieceItsFacesAndMaterials()
    {
        string json = WireCheck.New(Report(new List<UpgradeProblemView>()));

        Assert.StartsWith(
            "{\"tool\":\"replace_walls\",\"target\":\"StructureCompositeWall\",\"status\":\"dry_run\"," +
            "\"job_id\":null,\"ready\":true,\"problems\":[],\"pieces_total\":3,\"to_swap\":1,\"kept\":2," +
            "\"unmatched\":0,\"pieces\":[{\"reference_id\":\"301\",\"prefab_name\":\"StructureWallIron\"," +
            "\"position\":{\"x\":2.0,\"y\":1.0,\"z\":1.0},\"rotation_deg\":{\"x\":0.0,\"y\":90.0,\"z\":0.0}," +
            "\"build_state\":1,\"target_prefab_name\":\"StructureCompositeWall\",\"target_build_state\":1," +
            "\"blocks_air_before\":true,\"blocks_air_after\":true,\"blocks_gravity_before\":true," +
            "\"blocks_gravity_after\":true,\"air_change\":\"keeps\",\"seals\":false,\"stressed\":false," +
            "\"faces\":[{\"position\":{\"x\":2.0,\"y\":1.0,\"z\":1.0},\"cells\":[{\"x\":1.0,\"y\":1.0,\"z\":1.0}," +
            "{\"x\":3.0,\"y\":1.0,\"z\":1.0}],\"pressure_kpa_a\":101.3,\"pressure_kpa_b\":0.5," +
            "\"difference_kpa\":100.8,\"max_pressure_delta_kpa_before\":100.0," +
            "\"max_pressure_delta_kpa_after\":400.0,\"verdict\":\"ok\"}],\"room_ids\":[\"88\"]," +
            "\"materials\":[{\"prefab_name\":\"ItemKitWall\",\"cost\":1,\"refund\":0,\"net\":1}," +
            "{\"prefab_name\":\"ItemIronSheets\",\"cost\":0,\"refund\":2,\"net\":-2}]}],\"pieces_listed\":1," +
            "\"by_prefab\":[{\"prefab_name\":\"StructureWallIron\",\"target_prefab_name\":\"StructureCompositeWall\"," +
            "\"count\":1}],\"kept_pieces\":[],\"unmatched_pieces\":[]",
            json);
        Assert.Contains(
            "\"materials\":[{\"prefab_name\":\"ItemKitWall\",\"display_name\":\"Kit (Wall)\",\"cost\":1," +
            "\"refund\":0,\"charge\":1,\"give_back\":0,\"available\":3,\"stacks\":[{\"reference_id\":\"55\"," +
            "\"quantity\":3,\"held_in\":\"7\",\"slot_index\":2}]}],\"refund_enabled\":true," +
            "\"rooms\":[{\"room_id\":\"88\",\"cell_count\":12,\"total_mol\":480.5,\"energy_j\":21000000.0}]",
            json);
    }

    [Fact]
    public void AFrameReportLeavesFacesOut()
    {
        StructurePieceView frame = new StructurePieceView(
            new ThingView(new ThingId(9), "StructureFrameIron", "Iron Frame"), new PositionView(1, 1, 1),
            new RotationView(0, 0, 0), new StructureTargetView("StructureFrameIron", 0, 2),
            new StructureBlockingView(false, true, true, true, "seals", false), null, new List<string>(),
            new List<StructureMaterialLineView>());

        string json = WireCheck.New(frame);

        Assert.DoesNotContain("\"faces\"", json);
        Assert.Contains("\"air_change\":\"seals\",\"seals\":true,\"stressed\":false,\"room_ids\":[]", json);
    }

    [Fact]
    public void AJobReportsTheHeldCheckAndTheRoomCheck()
    {
        StructureSwapLogView log = new StructureSwapLogView();
        log.Swapped.Add(new UpgradeSwappedView(new ThingId(301), new ThingId(302), "StructureWallIron",
            "StructureCompositeWall"));
        StructureRoomResultView room = new StructureRoomResultView("88", true,
            new StructureRoomCells(12, 12, 12, 0, 0), new StructureRoomAir(480.5, 480.5, 4.805, 2e7, 2e7, 2e5),
            new List<string>());
        StructureRoomCheckView rooms = new StructureRoomCheckView(true, 3, new List<UpgradeProblemView>(),
            new List<StructureRoomResultView> { room }, new List<StructureRoomView>());
        StructureSwapJobView job = new StructureSwapJobView("replace-1", "replace_walls", "applied",
            Report(new List<UpgradeProblemView>(), "scheduled", "replace-1"),
            new StructureSwapJobResult(null, log,
                new StructureChecks(new StructureHeldCheckView(new List<UpgradeProblemView>()), rooms), null));

        string json = WireCheck.New(job);

        Assert.StartsWith("{\"job_id\":\"replace-1\",\"tool\":\"replace_walls\",\"status\":\"applied\"," +
                          "\"preflight\":{\"tool\":\"replace_walls\"", json);
        Assert.Contains(
            "\"final_check\":null,\"swapped\":[{\"old_reference_id\":\"301\",\"new_reference_id\":\"302\"," +
            "\"prefab_name\":\"StructureWallIron\",\"target_prefab_name\":\"StructureCompositeWall\"}]," +
            "\"swapped_count\":1,\"not_swapped\":[],\"stopped_at\":null,\"used\":[],\"refund_delivered\":[]," +
            "\"refund_error\":null,\"held_check\":{\"ok\":true,\"problems\":[]},\"room_check\":{\"settled\":true," +
            "\"ticks_run\":3,\"ok\":true,\"problems\":[],\"rooms\":[{\"room_id\":\"88\",\"exists\":true," +
            "\"cell_count_before\":12,\"cell_count_expected\":12,\"cell_count_after\":12,\"cells_lost\":0," +
            "\"cells_gained\":0,\"total_mol_before\":480.5,\"total_mol_after\":480.5,\"delta_mol\":0.0," +
            "\"tolerance_mol\":4.805,\"energy_j_before\":20000000.0,\"energy_j_after\":20000000.0," +
            "\"delta_energy_j\":0.0,\"tolerance_energy_j\":200000.0,\"problems\":[]}],\"new_rooms\":[]}," +
            "\"error\":null}",
            json);
    }

    [Fact]
    public void AStoppedJobSaysWhetherThePieceWasRestored()
    {
        StructureSwapLogView log = new StructureSwapLogView
        {
            StoppedAt = new StructureStopView(new ThingId(301), new ErrorView("placement_mismatch", "m"), true, true,
                null)
        };
        StructureSwapJobView job = new StructureSwapJobView("replace-2", "replace_frames", "stopped",
            Report(new List<UpgradeProblemView>()), new StructureSwapJobResult(null, log, StructureChecks.None, null));

        Assert.Contains(
            "\"stopped_at\":{\"reference_id\":\"301\",\"error\":{\"code\":\"placement_mismatch\",\"message\":\"m\"}," +
            "\"piece_intact\":true,\"rolled_back\":true,\"replacement_id\":null}",
            WireCheck.New(job));
    }

    [Fact]
    public void AWaitingJobHasNoChecksYet()
    {
        string json = WireCheck.New(new StructureSwapJobView("replace-3", "replace_walls", "waiting",
            Report(new List<UpgradeProblemView>()), null));
        Assert.Contains("\"held_check\":null,\"room_check\":null,\"error\":null}", json);
    }
}

/// <summary>Every tool the sidecar lists has a definition and a handler in the mod, and the other way round.</summary>
public sealed class ToolRegistrationTests
{
    // Answered by the sidecar itself (it samples through read_logic), never sent to the mod.
    private static readonly string[] SidecarOnly = { "sample_logic" };

    [Fact]
    public void TheToolListAndTheModsHandlersAgree()
    {
        HashSet<string> names = new HashSet<string>(Server.ToolCatalogue.Names);
        SyntaxNode host = Parse("src", "StationGodMCP.Mod", "Api", "ApiHost.cs");
        HashSet<string> handled = new HashSet<string>(host.DescendantNodes().OfType<ImplicitElementAccessSyntax>()
            .Select(access => access.ArgumentList.Arguments[0].Expression)
            .OfType<LiteralExpressionSyntax>()
            .Select(literal => literal.Token.ValueText));

        Assert.Empty(names.Except(handled).Except(SidecarOnly));
        Assert.Empty(handled.Except(names));
        Assert.Subset(names, new HashSet<string> { "replace_walls", "replace_frames" });
        Assert.Subset(handled, new HashSet<string> { "replace_walls", "replace_frames" });
    }

    private static HashSet<string> StringsIn(SyntaxNode node) =>
        new HashSet<string>(node.DescendantNodes().OfType<LiteralExpressionSyntax>()
            .Where(literal => literal.IsKind(SyntaxKind.StringLiteralExpression))
            .Select(literal => literal.Token.ValueText));

    private static SyntaxNode Parse(params string[] path) =>
        CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(new[] { Root() }.Concat(path).ToArray())))
            .GetRoot();

    private static string Root([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
}
