#nullable enable

using System.Collections.Generic;
using System.Linq;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>Rocket building: the game's cell-type rule, the fuselage takedown rule and order, the umbilical search.</summary>
public sealed class RocketRulesTests
{
    private const int Pipes = 1;
    private const int Cables = 2;
    private const int Devices = 4;
    private const int Chutes = 8;
    private const int Engine = 32;

    [Theory]
    [InlineData(Pipes | Cables, Pipes, true)]
    [InlineData(Pipes | Cables, Cables, true)]
    [InlineData(Pipes, Cables, false)]
    [InlineData(Devices, Devices | Engine, false)]
    [InlineData(Devices | Engine, Devices | Engine, true)]
    [InlineData(Chutes, 0, false)]
    [InlineData(0, Pipes, false)]
    public void ACellTakesAPieceOnlyWhenItsTypeHoldsEveryBitOfThePiece(int cell, int piece, bool fits) =>
        Assert.Equal(fits, RocketCellRule.Fits(cell, piece));

    [Fact]
    public void AMovingRocketNamesItselfAndTheToolsOwnRule()
    {
        string refusal = RocketMotionRule.Refusal("Ice Hauler", "Launching");
        Assert.StartsWith("Ice Hauler is launching", refusal);
        Assert.Contains("the tool's own rule", refusal);
    }

    [Theory]
    [InlineData(true, true, "SupportsAnother")]
    [InlineData(true, false, "SupportsAnother")]
    [InlineData(false, true, "HoldsInternals")]
    [InlineData(false, false, "Allowed")]
    public void AFuselagePieceGoesOnlyWithNothingOnTopAndNothingInside(bool above, bool internals, string expected) =>
        Assert.Equal(expected, FuselageTakedownRule.Judge(above, internals).ToString());

    [Fact]
    public void TakedownsGoInternalsFirstThenTheHullFromTheTopDown()
    {
        List<RocketTakedownItem> items = new List<RocketTakedownItem>
        {
            new RocketTakedownItem(0, true, 101.0), // engine fuselage (bottom)
            new RocketTakedownItem(1, false, 102.0), // a tank inside
            new RocketTakedownItem(2, true, 105.0), // nose cone (top)
            new RocketTakedownItem(3, true, 103.0), // fuselage (middle)
            new RocketTakedownItem(4, false, 90.0) // something else
        };
        Assert.Equal(new[] { 1, 4, 2, 3, 0 }, RocketTakedownOrder.Of(items));
    }

    [Fact]
    public void WithoutAHullTheRequestOrderStands()
    {
        List<RocketTakedownItem> items = Enumerable.Range(0, 4)
            .Select(index => new RocketTakedownItem(index, false, 10 - index)).ToList();
        Assert.Equal(new[] { 0, 1, 2, 3 }, RocketTakedownOrder.Of(items));
    }

    private static UmbilicalProbe Female(long id, double facing = 1.0, bool compatible = true) =>
        new UmbilicalProbe.Umbilical(id, "Umbilical Socket", compatible, facing);

    [Fact]
    public void TheNearestFacingPartnerPairsAndTheDistanceIsTheStepPlusOne()
    {
        UmbilicalSearchResult result = UmbilicalSearch.Run(false,
            (column, step) => step == 4 ? Female(7) : UmbilicalProbe.Clear.Instance);
        Assert.Equal(7, result.Partner!.Id);
        Assert.Equal(4, result.Step);
        Assert.Equal(5, result.PartnerDistance);
        Assert.Empty(result.Stops);
    }

    [Fact]
    public void ASocketSearchesOneColumnAndAnUmbilicalThree()
    {
        List<int> socket = new List<int>();
        UmbilicalSearch.Run(false, (column, step) =>
        {
            socket.Add(column);
            return UmbilicalProbe.Clear.Instance;
        });
        Assert.Equal(new[] { 0 }, socket.Distinct());
        Assert.Equal(UmbilicalSearch.LastStep + 1, socket.Count);

        List<int> male = new List<int>();
        UmbilicalSearch.Run(true, (column, step) =>
        {
            male.Add(column);
            return UmbilicalProbe.Clear.Instance;
        });
        Assert.Equal(new[] { 0, 1, 2 }, male.Distinct());
    }

    [Fact]
    public void ADeviceBetweenStopsTheColumn()
    {
        UmbilicalSearchResult result = UmbilicalSearch.Run(false, (column, step) => step switch
        {
            2 => new UmbilicalProbe.Blocker(55, "Pipe Analyzer (StructurePipeAnalysizer)"),
            6 => Female(7),
            _ => UmbilicalProbe.Clear.Instance
        });
        Assert.Null(result.Partner);
        Assert.Equal(0, result.PartnerDistance);
        UmbilicalStop stop = Assert.Single(result.Stops);
        Assert.Equal(2, stop.Step);
        Assert.Contains("55", stop.Reason);
    }

    [Fact]
    public void AnUmbilicalThatDoesNotFaceBackOrDoesNotMatchStopsTheColumn()
    {
        UmbilicalSearchResult turned = UmbilicalSearch.Run(false,
            (column, step) => step == 3 ? Female(7, 0.5) : UmbilicalProbe.Clear.Instance);
        Assert.Null(turned.Partner);
        Assert.Contains("does not face it (dot 0.5, 0.9 needed)", Assert.Single(turned.Stops).Reason);

        UmbilicalSearchResult wrong = UmbilicalSearch.Run(false,
            (column, step) => step == 3 ? Female(7, 1.0, false) : UmbilicalProbe.Clear.Instance);
        Assert.Contains("not a matching umbilical", Assert.Single(wrong.Stops).Reason);
    }

    [Fact]
    public void ALaterColumnWinsOnlyWhenStrictlyNearerAndSearchesNoFurther()
    {
        List<(int Column, int Step)> probed = new List<(int, int)>();
        UmbilicalSearchResult result = UmbilicalSearch.Run(true, (column, step) =>
        {
            probed.Add((column, step));
            return (column, step) switch
            {
                (0, 5) => Female(1),
                (1, 5) => Female(2),
                (2, 3) => Female(3),
                _ => UmbilicalProbe.Clear.Instance
            };
        });
        Assert.Equal(3, result.Partner!.Id);
        Assert.Equal(4, result.PartnerDistance);
        Assert.DoesNotContain((1, 6), probed);
        Assert.Contains((1, 5), probed);
    }
}
