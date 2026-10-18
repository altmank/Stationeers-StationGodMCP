#nullable enable

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>build terrain's modes and guards, find void's pockets, and their replies.</summary>
public sealed class TerrainFillTests
{
    private static readonly VoxelReading Air = new(Densities.Empty, Densities.Solid, false);
    private static readonly VoxelReading Ground = new(Densities.Solid, Densities.Solid, false);
    private static readonly VoxelReading Frame = new(Densities.Empty, Densities.Empty, true);

    [Fact]
    public void TheModeIsSolidUnlessNamedAndLevelNeedsAHeight()
    {
        List<OptionProblem> problems = new();
        Assert.IsType<FillMode.Solid>(FillMode.Of(null, null, problems));
        Assert.IsType<FillMode.Voids>(FillMode.Of("voids", null, problems));
        Assert.Equal(150.0, Assert.IsType<FillMode.Level>(FillMode.Of("level", 150, problems)).Height);
        Assert.Empty(problems);

        Assert.Null(FillMode.Of("level", null, problems));
        Assert.Null(FillMode.Of("solid", 150, problems));
        Assert.Equal(new[] { "level", "level" }, problems.Select(problem => problem.Path));
    }

    [Fact]
    public void LevelFillsUnderThePlaneAndDigsAboveIt()
    {
        FillMode level = new FillMode.Level(150);

        VoxelChoice.Set fill = Assert.IsType<VoxelChoice.Set>(VoxelChoice.Of(level, new VoxelPoint(0, 149, 0), Air, false));
        Assert.Equal(Densities.Solid, fill.Density);
        Assert.True(fill.Adds);

        VoxelChoice.Set dig = Assert.IsType<VoxelChoice.Set>(VoxelChoice.Of(level, new VoxelPoint(0, 150, 0), Ground, false));
        Assert.Equal(Densities.Empty, dig.Density);
        Assert.True(dig.Opens);
        Assert.False(dig.Adds);

        Assert.Equal(VoxelChoice.KeepReason.Same,
            Assert.IsType<VoxelChoice.Keep>(VoxelChoice.Of(level, new VoxelPoint(0, 149, 0), Ground, false)).Reason);
    }

    [Fact]
    public void AFillNeverTouchesAStructuresCellOrBedrock()
    {
        foreach (FillMode mode in new FillMode[] { new FillMode.Solid(), new FillMode.Original(), new FillMode.Level(150) })
        {
            Assert.Equal(VoxelChoice.KeepReason.Structure,
                Assert.IsType<VoxelChoice.Keep>(VoxelChoice.Of(mode, new VoxelPoint(0, 149, 0), Frame, false)).Reason);
            Assert.Equal(VoxelChoice.KeepReason.Bedrock,
                Assert.IsType<VoxelChoice.Keep>(VoxelChoice.Of(mode, new VoxelPoint(0, 1, 0), Air, false)).Reason);
        }
    }

    [Fact]
    public void OriginalRestoresTheGeneratedGroundBothWays()
    {
        FillMode original = new FillMode.Original();
        VoxelReading dug = new(Densities.Empty, 200, false);
        VoxelReading added = new(Densities.Solid, Densities.Empty, false);

        Assert.Equal((byte)200, Assert.IsType<VoxelChoice.Set>(VoxelChoice.Of(original, new VoxelPoint(0, 9, 0), dug, false)).Density);
        Assert.False(Assert.IsType<VoxelChoice.Set>(VoxelChoice.Of(original, new VoxelPoint(0, 9, 0), added, false)).Adds);
    }

    [Fact]
    public void VoidsFillsOnlyThePocketsVoxels()
    {
        FillMode voids = new FillMode.Voids();

        Assert.IsType<VoxelChoice.Set>(VoxelChoice.Of(voids, new VoxelPoint(0, 9, 0), Air, true));
        Assert.Same(VoxelChoice.Untouched, VoxelChoice.Of(voids, new VoxelPoint(0, 9, 0), Air, false));
    }

    [Fact]
    public void ThePlayersBodyIsTheMetreAroundTheirFeetAndTwoUp()
    {
        PlayerBody body = new(new Vec3(10.5, 20, 30.5));

        Assert.True(body.Overlaps(new VoxelPoint(10, 20, 30)));
        Assert.True(body.Overlaps(new VoxelPoint(10, 21, 30)));
        Assert.False(body.Overlaps(new VoxelPoint(10, 22, 30)));
        Assert.False(body.Overlaps(new VoxelPoint(10, 19, 30)));
        Assert.False(body.Overlaps(new VoxelPoint(11, 20, 30)));
    }

    [Fact]
    public void APocketEnclosedByGroundIsAVoidAndSpaceReachingTheEdgeOrABuildingIsNot()
    {
        // A 7 x 7 x 7 box of ground with: a 2-voxel pocket inside, a shaft to the top layer, and a pocket beside a frame.
        DigBox box = DigBox.Between(new VoxelPoint(0, 10, 0), new VoxelPoint(6, 16, 6));
        VoxelReading[] readings = Enumerable.Repeat(Ground, (int)box.Cells).ToArray();
        readings[box.IndexOf(new VoxelPoint(2, 12, 2))] = Air;
        readings[box.IndexOf(new VoxelPoint(3, 12, 2))] = Air;
        for (int y = 12; y <= 16; y++)
        {
            readings[box.IndexOf(new VoxelPoint(5, y, 5))] = Air;
        }

        readings[box.IndexOf(new VoxelPoint(2, 14, 4))] = Air;
        readings[box.IndexOf(new VoxelPoint(2, 15, 4))] = Frame;

        VoidSearch.Result found = VoidSearch.Find(box, readings);

        TerrainVoid pocket = Assert.Single(found.Voids);
        Assert.Equal(2, pocket.Cells.Count);
        Assert.Equal(new VoxelPoint(2, 12, 2), pocket.Low);
        Assert.Equal(new VoxelPoint(3, 12, 2), pocket.High);
        Assert.Equal(new VoxelPoint(1, 11, 1), pocket.FillBox.Low);
        Assert.Equal(new VoxelPoint(4, 13, 3), pocket.FillBox.High);
        Assert.Equal((1, 5), (found.OpenToEdge, found.OpenToEdgeCells));
        Assert.Equal(1, found.TouchingStructures);
    }

    [Fact]
    public void ThePocketsFillBoxEnclosesItSoVoidsModeFindsItAgain()
    {
        DigBox searched = DigBox.Between(new VoxelPoint(0, 10, 0), new VoxelPoint(9, 19, 9));
        VoxelReading[] readings = Enumerable.Repeat(Ground, (int)searched.Cells).ToArray();
        readings[searched.IndexOf(new VoxelPoint(4, 14, 4))] = Air;
        TerrainVoid pocket = Assert.Single(VoidSearch.Find(searched, readings).Voids);

        DigBox fill = pocket.FillBox;
        VoxelReading[] again = Enumerable.Repeat(Ground, (int)fill.Cells).ToArray();
        again[fill.IndexOf(new VoxelPoint(4, 14, 4))] = Air;

        Assert.Single(VoidSearch.Find(fill, again).Voids);
        Assert.Equal(27L, fill.Cells);
    }

    [Fact]
    public void BoxIndicesRoundTrip()
    {
        DigBox box = DigBox.Between(new VoxelPoint(-3, 5, 7), new VoxelPoint(1, 8, 9));
        int index = 0;
        foreach (VoxelPoint voxel in box.BottomUp())
        {
            Assert.Equal(index, box.IndexOf(voxel));
            Assert.Equal(voxel, box.At(index));
            index++;
        }

        Assert.Equal(-1, box.IndexOf(new VoxelPoint(2, 5, 7)));
    }

    [Fact]
    public void FindVoidSearchesUpToASixtyFourMetreCube()
    {
        List<OptionProblem> problems = new();
        Assert.NotNull(DigBox.Of(new BoxCorners(new Vec3(0, 10, 0), new Vec3(64, 74, 64)), problems,
            DigBox.MostSearched, "a call searches"));
        Assert.Null(DigBox.Of(new BoxCorners(new Vec3(0, 10, 0), new Vec3(65, 74, 64)), problems,
            DigBox.MostSearched, "a call searches"));
        Assert.Contains("(a 64 m cube) a call searches", Assert.Single(problems).Problem);
    }

    [Fact]
    public void TheFillReportGivesItsModeLevelAndCounts()
    {
        DigBox box = DigBox.Of(new BoxCorners(new Vec3(-920, 146, -560), new Vec3(-910, 152, -548)), new())!;
        DigTally tally = new();
        tally.Fill();
        tally.Fill();
        tally.Solid();
        tally.Built();
        TerrainFillView reading = new(box, new FillMode.Level(150), tally, null, OreHandling.Dropped,
            new List<ThingView>());

        JObject report = Replies.Tree(TerrainFillReplies.Reported(reading, "[cannot undo: terrain fill]"));

        Assert.Equal("level", (string?)report["mode"]);
        Assert.Equal(150.0, (double)report["level"]!);
        Assert.Equal((2, 1, 1), ((int)report["filled"]!, (int)report["dug"]!, (int)report["structure_kept"]!));
        Assert.Null(report["voids"]);
        Assert.EndsWith(", terrain, as it is -> 2 filled, 1 dug] [cannot undo: terrain fill]",
            (string?)report["targets"]![0]!["text"]);
    }

    [Fact]
    public void AVoidGivesTheBoxBuildTerrainTakes()
    {
        TerrainVoid pocket = new(new List<int> { 0, 1 }, new VoxelPoint(-879, 153, -565), new VoxelPoint(-865, 171, -557));
        JObject entry = Replies.Tree(new VoidView(0, pocket, new VoxelPoint(-870, 160, -560), new List<ThingView>()));

        Assert.Equal(new JArray(new JArray(-880.0, 152.0, -566.0), new JArray(-863.0, 173.0, -555.0)), entry["fill_box"]);
        Assert.Equal(-864.0, (double)entry["max"]!["x"]!);
        Assert.Null(entry["players_inside"]);
    }

}
