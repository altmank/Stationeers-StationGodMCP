#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>Fixes from round 6 of the live test of 1.4.4: solar and pipes.</summary>
public sealed class SolarPipesRound6Tests
{
    private static GridStep S(string name) => RunModels.Step(name);

    // solar-23: the medium dish on the frame at y = 224, its mesh x -1022.94..-1019.06 (3.9 m).
    [Fact]
    public void AMediumDishCrossesSeamsItCannotAvoid()
    {
        Box3 cells = new Box3(new Vec3(-1022, 224, -714), new Vec3(-1020, 226, -712));
        Box3 mesh = new Box3(new Vec3(-1022.94, 224, -714.94), new Vec3(-1019.06, 227, -711.06));
        MountRect rect = MountRect.Of(cells, S("+y"), mesh)!;
        Assert.True(rect.CrossesSeam);
        Assert.False(rect.FitsOneSection);
        Assert.False(rect.CrossesAvoidableSeam);
    }

    // solar-23: the landing pad's Data And Power piece, z -716.02..-713.74 (2.28 m).
    [Fact]
    public void ALandingPadPieceWiderThanASectionDoesNotWarn()
    {
        Box3 cells = new Box3(new Vec3(-1018, 224, -716), new Vec3(-1016, 224.5, -714));
        Box3 mesh = new Box3(new Vec3(-1017.9, 224, -716.02), new Vec3(-1016.1, 224.3, -713.74));
        MountRect rect = MountRect.Of(cells, S("+y"), mesh)!;
        Assert.True(rect.CrossesSeam);
        Assert.False(rect.CrossesAvoidableSeam);
    }

    [Fact]
    public void TheConsoleOverTheSeamStillWarnsBecauseAShiftFixesIt()
    {
        MountRect rect = MountRect.Of(VisualClashTests.ConsoleCells, S("+z"), VisualClashTests.ConsoleMesh)!;
        Assert.True(rect.FitsOneSection);
        Assert.True(rect.CrossesAvoidableSeam);
    }

    [Theory]
    [InlineData(2.2, true)]
    [InlineData(2.21, false)]
    public void APieceFitsOneSectionUpTo2Point2Metres(double width, bool fits)
    {
        Box3 cells = new Box3(new Vec3(716, 200, 667.75), new Vec3(718, 201, 668.25));
        Box3 mesh = new Box3(new Vec3(717 - width / 2, 200, 667.9), new Vec3(717 + width / 2, 201, 668.2));
        MountRect rect = MountRect.Of(cells, S("+z"), mesh)!;
        Assert.Equal(fits, rect.FitsOneSection);
    }

    // pipes-30: remove_structure quoted remove_pipes' advice to pass allow_split and root, which it does not take.
    [Fact]
    public void RemoveStructuresSplitWarningNamesNoArgumentItLacks()
    {
        string warning = RemovalRule.SplitWarning(
            "Network 1349 would fall into 2 networks. Devices per part: []; []. No root is on it (pass root to name " +
            "one). Pass allow_split if that is meant.");
        Assert.Equal(
            "Network 1349 would fall into 2 networks. Devices per part: []; []. No root is on it; remove_structure " +
            "only warns of a split and removes it anyway (plan_removal or remove_pipes with root name the devices a " +
            "root still reaches).", warning);
        Assert.DoesNotContain("allow_split", warning);
        Assert.DoesNotContain("pass root", warning);
    }

    [Fact]
    public void ACutPortsSplitWarningLosesItsAdviceToo()
    {
        string warning = RemovalRule.SplitWarning(
            "Port 0 of device 12 would lose its connection to network 7. Pass allow_split if that is meant.");
        Assert.StartsWith("Port 0 of device 12 would lose its connection to network 7; remove_structure", warning);
    }

    // solar-24: a dry run's credits_after is the predicted balance, refused lines left out.
    [Fact]
    public void ADryRunPredictsTheBalance()
    {
        TradeLine coil = new TradeLine("Cable Coil", "ItemCableCoil", gas: false, creditsEach: 0.4f);
        BatchBuilder sell = new BatchBuilder(4);
        sell.Succeeded(new TradedView(0, coil, buying: false, 4, 1.6f, 8));
        sell.Succeeded(new TradedView(1, coil, buying: false, 4, 1.6f, 4));
        sell.Succeeded(new TradedView(2, coil, buying: false, 4, 1.6f, 0));
        sell.Failed(new NotTradedView(3, coil, 0, 0f, ApiErrors.Refused("not_wanted", "no")));
        Assert.Equal(4.8f, TradeView.PredictedCredits(0f, sell.Build().Results), 3);

        BatchBuilder buy = new BatchBuilder(1);
        buy.Succeeded(new TradedView(0, coil, buying: true, 1, 3.75f, 9));
        Assert.Equal(0.65f, TradeView.PredictedCredits(4.4f, buy.Build().Results), 3);
    }
}
