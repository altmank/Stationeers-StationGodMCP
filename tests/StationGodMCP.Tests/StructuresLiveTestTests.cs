#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>Fixes from the 1.4.4 live test of the structure tools (structures-*).</summary>
public sealed class StructuresLiveTestTests
{
    private static GridStep S(string name) => RunModels.Step(name);

    private static IReadOnlyCollection<long> Holders(params long[] ids) => ids;

    // structures-6: a wall light's wall removed with nothing else on its face.
    [Fact]
    public void ADeviceLosesItsSupportWhenTheOnlyPlateUnderItGoes()
    {
        HashSet<long> removed = new HashSet<long> { 6848 };
        Assert.True(MountSupport.Loses(new[] { Holders(6848) }, 6848, removed));
    }

    [Fact]
    public void ADeviceKeepsItsSupportWhileAFrameOrABackPlateStays()
    {
        HashSet<long> removed = new HashSet<long> { 447 };
        Assert.False(MountSupport.Loses(new[] { Holders(447, 450) }, 447, removed));
        Assert.True(MountSupport.Loses(new[] { Holders(447, 450) }, 447, new HashSet<long> { 447, 450 }));
    }

    [Fact]
    public void ADeviceOnAnotherFaceIsNotThePiecesConcern()
    {
        Assert.False(MountSupport.Loses(new[] { Holders(12) }, 99, new HashSet<long> { 99 }));
    }

    [Fact]
    public void ADeviceAcrossASeamLosesSupportWhenEitherSectionIsLeftBare()
    {
        IReadOnlyCollection<long>[] faces = { Holders(443), Holders(444) };
        Assert.True(MountSupport.Loses(faces, 443, new HashSet<long> { 443 }));
    }

    // structures-7: the removal names where the gas is when it is the network's.
    [Fact]
    public void GasInAnEmptiedPipeNetworkIsHoldsGas()
    {
        RemovalFacts facts = new RemovalFacts
        {
            GasMoles = 5.0,
            GasFate = GasFate.Lost,
            GasWhere = " in pipe network 6910, whose last member this removal takes"
        };
        List<GuardFinding> refused = RemovalRule.Judge(facts, new RemovalAllowance(false, false));
        GuardFinding gas = Assert.Single(refused);
        Assert.Equal("holds_gas", gas.Code);
        Assert.Equal(GuardLevel.Refusal, gas.Level);
        Assert.Contains("pipe network 6910", gas.Message);
        GuardFinding allowed = Assert.Single(RemovalRule.Judge(facts, new RemovalAllowance(true, false)));
        Assert.Equal("gas_lost", allowed.Code);
    }

    // structures-2: the cell a step ahead of a point, read without rounding onto the face plane.
    [Theory]
    [InlineData(-1065.0, 221.0, -711.0, -1.01, 0.0, 0.0, -10670, 2210, -7110)]
    [InlineData(-1065.0, 221.0, -711.0, 1.01, 0.0, 0.0, -10630, 2210, -7110)]
    [InlineData(-1065.0, 221.0, -711.0, 0.0, 0.0, -1.01, -10650, 2210, -7130)]
    [InlineData(-1066.0, 221.0, -711.0, 1.01, 0.0, 0.0, -10650, 2210, -7110)]
    [InlineData(-1066.0, 221.0, -711.0, -1.01, 0.0, 0.0, -10670, 2210, -7110)]
    [InlineData(-1066.0, 222.0, -711.0, 0.0, 0.0, 0.0, -10650, 2230, -7110)]
    public void AStepAheadReadsTheNextCell(double x, double y, double z, double dx, double dy, double dz, int cx,
        int cy, int cz)
    {
        Assert.Equal(new GridCell(cx, cy, cz), LargeCells.Containing(new Vec3(x + dx, y + dy, z + dz)));
    }

    // structures-21: a box side on a face plane takes no cell beyond it.
    [Fact]
    public void ABoxUpToAFloorStopsBelowIt()
    {
        List<GridCell> cells = LargeCells.InBox(new Vec3(-1066, 220, -712), new Vec3(-1062, 222, -710));
        Assert.All(cells, cell => Assert.Equal(2210, cell.Y));
        Assert.Equal(new[] { -10650, -10630 }, cells.Select(cell => cell.X).Distinct().ToArray());
        Assert.Equal(cells.Count, LargeCells.CountInBox(new Vec3(-1066, 220, -712), new Vec3(-1062, 222, -710)));
    }

    [Fact]
    public void AFlatBoxTakesTheCellItsCoordinateBelongsTo()
    {
        GridCell cell = Assert.Single(LargeCells.InBox(new Vec3(-1065, 222, -711), new Vec3(-1065, 222, -711)));
        Assert.Equal(2230, cell.Y);
    }

    // structures-20 / pipes-12: clashes between the placements of one run.
    [Fact]
    public void AFrameAndAWallFacingIntoItsCellClashInEitherOrder()
    {
        GridCell cell = new GridCell(-10570, 2210, -7210);
        PlannedFootprint frame = PlannedFootprint.Large(0, "StructureFrameIron", new[] { cell }, true);
        PlannedFootprint wall = PlannedFootprint.Large(1, "StructureWallIron", new[] { cell }, false);
        PlanClash clash = Assert.Single(PlanClashes.Find(new[] { frame, wall }));
        Assert.Equal(1, clash.Index);
        Assert.Equal(0, clash.BlockedBy);
        Assert.Single(PlanClashes.Find(new[]
        {
            PlannedFootprint.Large(0, "StructureWallIron", new[] { cell }, false),
            PlannedFootprint.Large(1, "StructureFrameIron", new[] { cell }, true)
        }));
    }

    [Fact]
    public void WallsOnTwoFacesOfOneCellDoNotClash()
    {
        GridCell cell = new GridCell(-10570, 2210, -7210);
        Assert.Empty(PlanClashes.Find(new[]
        {
            PlannedFootprint.Large(0, "StructureWallIron", new[] { cell }, false),
            PlannedFootprint.Large(1, "StructureWallIron", new[] { cell }, false)
        }));
    }

    [Fact]
    public void OverlappingLongPipesClashButAPipeAndACableShareACell()
    {
        GridCell[] first = { new GridCell(0, 0, 0), new GridCell(0, 0, 5), new GridCell(0, 0, 10) };
        GridCell[] second = { new GridCell(0, 0, 10), new GridCell(0, 0, 15) };
        Assert.Single(PlanClashes.Find(new[]
        {
            PlannedFootprint.Small(0, "StructurePipeStraight10", first, "pipe"),
            PlannedFootprint.Small(1, "StructurePipeStraight10", second, "pipe")
        }));
        Assert.Empty(PlanClashes.Find(new[]
        {
            PlannedFootprint.Small(0, "StructurePipeStraight10", first, "pipe"),
            PlannedFootprint.Small(1, "StructureCableStraight", second, "cable")
        }));
    }

    // structures-10: spots within the radius, nearest first, none on a plane farther than it.
    [Fact]
    public void SpotsWithinTheRadiusComeNearestFirst()
    {
        List<(double U, double V, double Distance)> spots = SpotSearch.Within(0.0, 0.0, 1.0, 2.0);
        Assert.Equal((0.0, 0.0, 1.0), spots[0]);
        Assert.All(spots, spot => Assert.True(spot.Distance <= 2.0 + 1e-9));
        Assert.Equal(spots.OrderBy(spot => spot.Distance).ToList(), spots);
        Assert.Empty(SpotSearch.Within(0.0, 0.0, 12.5, 12.0));
    }

    // structures-12: a small cell on a seam belongs to the section on its plus side, whichever way the map looks.
    [Theory]
    [InlineData(-1066.0, -10650)]
    [InlineData(-1062.5, -10630)]
    [InlineData(-1062.0, -10610)]
    [InlineData(221.5, 2210)]
    public void ASmallCellBelongsToTheSectionOnItsPlusSide(double metres, int centre)
    {
        Assert.Equal(centre, PlaneCells.FaceCentre(metres));
    }

    // structures-8: float noise in a room's summed air is not a change.
    [Fact]
    public void HeldAirComparesWithinTheGasAuditsTolerance()
    {
        Assert.True(GasTolerance.Default.SameMol(99.3170922629665, 99.3170907811472));
        Assert.False(GasTolerance.Default.SameMol(99.3, 98.8));
        Assert.True(GasTolerance.Default.SameEnergy(1_000_000.0, 1_000_000.5));
    }

    // structures-9: a target standing in the port's joining cell lies straight ahead of the port.
    [Fact]
    public void APortTargetInTheJoiningCellIsNotMissed()
    {
        Vec3 joining = new Vec3(-1053.5, 225, -701);
        OrientPort port = new OrientPort(1, "Pipe", "Input", "in", joining, S("-x"));
        OrientIntent intent = new OrientIntent(null, true, null,
            new List<PortIntent> { new PortIntent("Input", null, null, new OrientTarget.At(joining, "pipe 1205")) },
            null, null);
        CubeRotation turn = CubeRotation.FromFacing(S("-x"), S("+y"))!;
        OrientCandidate candidate = new OrientCandidate(turn, S("-y"), turn.Forward, new Vec3(-1053, 225, -701),
            new List<OrientPort> { port }, null, 0, null);
        OrientScore score = OrientSearch.Score(candidate, intent, (_, _) => false);
        Assert.Equal(0.0, score.Score, 6);
    }

    // structures-18: an absolute path names a blueprint with or without the extension.
    [Fact]
    public void AnAbsolutePathWithoutTheExtensionGetsIt()
    {
        string path = Path.Combine(Path.GetTempPath(), "blueprints", "frame-floor-3x3");
        Assert.Equal(path + ".blueprint", BlueprintFiles.Resolve(path, null));
        string other = Path.Combine(Path.GetTempPath(), "blueprints", "notes.md");
        Assert.Equal(other, BlueprintFiles.Resolve(other, null));
    }

    // structures-21: a position far outside any world, and orient.mount's shape, are argument errors.
    [Fact]
    public void APositionOutsideTheWorldIsRefused()
    {
        ApiException error = Assert.Throws<ApiException>(() =>
            BuildArgs.PositionOf(JArray.Parse("[-1053, 1e9, -701]"), "at"));
        Assert.Equal("invalid_argument", error.Code);
        Assert.Equal(-1053.0, BuildArgs.PositionOf(JArray.Parse("[-1053, 225, -701]"), "at").X);
    }

    [Fact]
    public void AnUnknownOrientMountIsAnArgumentError()
    {
        Args args = new Args(JObject.Parse(
            "{\"prefab\": \"StructureGasSensor\", \"at\": [1, 2, 3], \"orient\": {\"mount\": \"sideways\"}}"));
        ApiException error = Assert.Throws<ApiException>(() => BuildArgs.Placement(args, 0, string.Empty));
        Assert.Equal("invalid_argument", error.Code);
        Assert.Contains("orient.mount", error.Message);
        BuildArgs.Placement(new Args(JObject.Parse(
            "{\"prefab\": \"StructureGasSensor\", \"at\": [1, 2, 3], \"orient\": {\"mount\": \"-z\"}}")), 0,
            string.Empty);
    }
}
