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

/// <summary>remove terrain's box, ore handling, tally and report; write health's report and reply.</summary>
public sealed class TerrainDigAndHealTests
{
    [Fact]
    public void ABoxTakesEveryVoxelWhoseCentreLiesInsideInEitherCornerOrder()
    {
        List<OptionProblem> problems = new();
        DigBox box = DigBox.Of(new BoxCorners(new Vec3(110, 45, 210), new Vec3(100, 40, 200)), problems)!;

        Assert.Empty(problems);
        Assert.Equal(new VoxelPoint(100, 40, 200), box.Low);
        Assert.Equal(new VoxelPoint(109, 44, 209), box.High);
        Assert.Equal(10L * 5 * 10, box.Cells);
        Assert.Equal(new Vec3(100, 40, 200), box.Min);
        Assert.Equal(new Vec3(110, 45, 210), box.Max);
    }

    [Fact]
    public void AHalfMetreEdgeTakesTheVoxelWhoseCentreItReaches()
    {
        DigBox box = DigBox.Of(new BoxCorners(new Vec3(0.5, 10.5, 0.5), new Vec3(0.5, 10.5, 0.5)), new())!;

        Assert.Equal(1L, box.Cells);
        Assert.Equal(new VoxelPoint(0, 10, 0), box.Low);
    }

    [Fact]
    public void ABoxThatHoldsNoVoxelCentreIsRefused()
    {
        List<OptionProblem> problems = new();

        Assert.Null(DigBox.Of(new BoxCorners(new Vec3(0.6, 10, 0), new Vec3(1.4, 12, 3)), problems));
        Assert.Equal("box", Assert.Single(problems).Path);
        Assert.Contains("holds no voxel", problems[0].Problem);
    }

    [Fact]
    public void ABoxOverAThirtyTwoMetreCubeIsRefusedWithItsCount()
    {
        List<OptionProblem> problems = new();
        Assert.NotNull(DigBox.Of(new BoxCorners(new Vec3(0, 10, 0), new Vec3(32, 42, 32)), problems));
        Assert.Empty(problems);

        Assert.Null(DigBox.Of(new BoxCorners(new Vec3(0, 10, 0), new Vec3(33, 42, 32)), problems));
        Assert.Contains("holds 33792 voxels", Assert.Single(problems).Problem);
        Assert.Null(DigBox.Of(null, problems));
    }

    [Fact]
    public void TheDigGoesTopLayerFirstAndKeepsBedrock()
    {
        DigBox box = DigBox.Of(new BoxCorners(new Vec3(0, 0, 0), new Vec3(2, 4, 1)), new())!;
        List<VoxelPoint> order = box.TopDown().ToList();

        Assert.Equal(box.Cells, order.Count);
        Assert.Equal(new VoxelPoint(0, 3, 0), order[0]);
        Assert.Equal(new VoxelPoint(1, 0, 0), order[^1]);
        Assert.All(order.Take(2), voxel => Assert.Equal(3, voxel.Y));
        Assert.False(DigBox.AboveBedrock(new VoxelPoint(0, 1, 0)));
        Assert.True(DigBox.AboveBedrock(new VoxelPoint(0, 2, 0)));
        Assert.True(box.Holds(new Vec3(1, 4, 0.5)));
        Assert.False(box.Holds(new Vec3(1, 4.1, 0.5)));
    }

    [Fact]
    public void OresDropUnlessDiscarded()
    {
        Assert.IsType<OreHandling.Drop>(OreHandling.Of(null));
        Assert.IsType<OreHandling.Drop>(OreHandling.Of("drop"));
        Assert.IsType<OreHandling.Discard>(OreHandling.Of("discard"));
    }

    [Fact]
    public void TheTallyCountsOreByPrefabInTheOrderFirstMet()
    {
        DigTally tally = new();
        tally.Solid();
        tally.Solid();
        tally.Empty();
        tally.Bedrock();
        tally.Ore("ItemIronOre", 3);
        tally.Ore("ItemIce", null);
        tally.Ore("ItemIronOre", 5);

        Assert.Equal((2, 1, 1), (tally.Dug, tally.AlreadyEmpty, tally.BedrockKept));
        Assert.Equal(new[] { "ItemIronOre", "ItemIce" }, tally.Ores.Select(ore => ore.PrefabName));
        Assert.Equal(2, tally.Ores[0].Minables);
        Assert.Equal(8, tally.Ores[0].Quantity);
        Assert.Null(tally.Ores[1].Quantity);
    }

    [Fact]
    public void TheDigReportGivesTheBoxShowPreviewTakesAndOneTarget()
    {
        DigBox box = DigBox.Of(new BoxCorners(new Vec3(100, 40, 200), new Vec3(104, 42, 204)), new())!;
        DigTally tally = new();
        for (int i = 0; i < 30; i++)
        {
            tally.Solid();
        }

        tally.Ore("ItemIronOre", null);
        TerrainDigView reading = new(box, tally, OreHandling.Dropped, new List<ThingView>());

        JObject report = Replies.Tree(TerrainDigReplies.Reported(reading, "[cannot undo: terrain dig]"));

        Assert.Equal(new[]
        {
            "box", "cells", "dug", "already_empty", "bedrock_kept", "ores_handling", "ores", "report_only", "targets"
        }, report.Properties().Select(property => property.Name));
        Assert.Equal(100.0, (double)report["box"]!["min"]!["x"]!);
        Assert.Equal(204.0, (double)report["box"]!["max"]!["z"]!);
        Assert.Equal("drop", (string?)report["ores_handling"]);
        Assert.Equal(JTokenType.Null, report["ores"]![0]!["quantity"]!.Type);
        Assert.Equal("terrain", (string?)report["targets"]![0]!["name"]);
        Assert.EndsWith(", terrain, 30 -> 0] [cannot undo: terrain dig]", (string?)report["targets"]![0]!["text"]);
    }

    [Fact]
    public void PlayersInTheBoxAreNamedOnlyWhenThere()
    {
        DigBox box = DigBox.Of(new BoxCorners(new Vec3(0, 10, 0), new Vec3(2, 12, 2)), new())!;
        ThingView player = new(new ThingId(77), "Character", "LU");

        JObject with = Replies.Tree(new TerrainDigView(box, new DigTally(), OreHandling.Discarded,
            new List<ThingView> { player }));
        JObject without = Replies.Tree(new TerrainDigView(box, new DigTally(), OreHandling.Discarded,
            new List<ThingView>()));

        Assert.Equal("77", (string?)with["players_in_box"]![0]!["reference_id"]);
        Assert.Null(without["players_in_box"]);
    }

    [Fact]
    public void TheHealReportListsEachPartsDamageGoingToNone()
    {
        HealView reading = Reading(healed: null, brainAfter: DamagePartsView.None);

        JObject report = Replies.Tree(HealReplies.Reported(reading, "[cannot undo: heal]"));

        Assert.Null(report["healed"]);
        Assert.Equal(new[] { "player", "online", "state", "parts", "report_only", "targets" },
            report.Properties().Select(property => property.Name));
        Assert.Equal("[12, brain, 34 -> 0] [cannot undo: heal]", (string?)report["targets"]![1]!["text"]);
    }

    [Fact]
    public void HealedSaysWhetherEveryPartReadsNoDamage()
    {
        DamagePartsView stunned = new(0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 5f);

        Assert.True(HealReplies.AllWhole(Reading(true, DamagePartsView.None).Parts));
        Assert.False(HealReplies.AllWhole(Reading(false, stunned).Parts));
        Assert.True((bool)Replies.Tree(Reading(true, DamagePartsView.None))["healed"]!);
    }

    [Theory]
    [InlineData(-848.0, -848.0)]
    [InlineData(-849.0, -848.0)]
    [InlineData(-848.5, -848.0)]
    [InlineData(-847.5, -846.0)]
    [InlineData(-847.0, -846.0)]
    [InlineData(-846.0, -846.0)]
    [InlineData(-850.0, -850.0)]
    public void AFacePlacedPiecesOwnPositionGivesItBack(double atX, double placedX)
    {
        // An industrial elevator level, 2 m grid, front toward -x: the level LU built stands at x -848.
        Vec3 forward = new(-1, 0, 0);

        Vec3 placed = FacePlacement.Position(new Vec3(atX, 167, -551), forward, 2.0, 0.0);

        Assert.Equal(placedX, placed.X, 6);
        Assert.Equal(167.0, placed.Y, 6);
        Assert.Equal(-551.0, placed.Z, 6);
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(-1, 0, 0)]
    [InlineData(0, 0, 1)]
    [InlineData(0, 0, -1)]
    public void EveryFacingReproducesAStandingPiece(int fx, int fy, int fz)
    {
        Vec3 forward = new(fx, fy, fz);
        // A piece stands on the face of the cell centred (-849, 167, -551) its front faces into.
        Vec3 standing = new Vec3(-849, 167, -551) - forward * 1.0;

        Assert.Equal(standing, FacePlacement.Position(standing, forward, 2.0, 0.0));
    }

    private static HealView Reading(bool? healed, DamagePartsView brainAfter)
    {
        DamagePartsView brainBefore = new(0f, 0f, 60f, 0f, 0f, 8f, 0f, 0f, 0f);
        List<HealedPartView> parts = new()
        {
            new HealedPartView("body", new ThingView(new ThingId(11), "Character", "LU"), 0, 0, DamagePartsView.None,
                DamagePartsView.None),
            new HealedPartView("brain", new ThingView(new ThingId(12), "OrganBrain", "Brain"), 34, 0, brainBefore,
                brainAfter)
        };
        return new HealView(new ThingView(new ThingId(11), "Character", "LU"), false, "Alive", parts, healed);
    }
}
