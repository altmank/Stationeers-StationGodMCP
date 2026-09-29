#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>Fixes from round 3 of the 1.4.4 live test of the structure tools (structures-30..33).</summary>
public sealed class StructuresRound3Tests
{
    // F5 of the live test: in-line tank 660 (6000 L) then pipes 663, 664, 665 (10 L each) in a line.
    private static readonly List<TakedownMember> Line = new List<TakedownMember>
    {
        new TakedownMember(660, 6000.0, 60795.0),
        new TakedownMember(663, 10.0, 60795.0),
        new TakedownMember(664, 10.0, 60795.0),
        new TakedownMember(665, 10.0, 60795.0)
    };

    private static readonly List<Link> LineLinks = Links((660, 663), (663, 664), (664, 665));

    private static List<Link> Links(params (long From, long To)[] pairs)
    {
        List<Link> links = new List<Link>();
        foreach ((long from, long to) in pairs)
        {
            links.Add(new Link(from, to));
            links.Add(new Link(to, from));
        }

        return links;
    }

    // structures-31: pipes first, then the tank: the split leaves the tank alone with 6000/6020 of the gas, and the
    // tank goes as the last member of its part, so that gas is deleted.
    [Fact]
    public void PipesBeforeTheirTankSplitItOffAndItsLastMemberDeletesTheGas()
    {
        TakedownOutcome outcome = PipeTakedown.Run(Line, LineLinks, new long[] { 663, 664, 660 }, false, 602.0);
        TakedownPart part = Assert.Single(outcome.Parts);
        Assert.Equal(new List<long> { 665 }, part.Members);
        Assert.Equal(2.0, part.Moles, 6);
        Assert.Equal(600.0, outcome.LostMol, 6);
    }

    // The same request with every removed member leaving the network first (the network stays whole): nothing is
    // divided, every mole stays in the pipe left.
    [Fact]
    public void AKeptWholeNetworkKeepsEveryMoleInWhatIsLeft()
    {
        TakedownOutcome outcome = PipeTakedown.Run(Line, LineLinks, new long[] { 663, 664, 660 }, true, 602.0);
        TakedownPart part = Assert.Single(outcome.Parts);
        Assert.Equal(new List<long> { 665 }, part.Members);
        Assert.Equal(10.0, part.VolumeL, 6);
        Assert.Equal(602.0, part.Moles, 6);
        Assert.Equal(0.0, outcome.LostMol, 6);
        Assert.Equal(6020.0, outcome.RemovedL, 6);
        Assert.False(outcome.Split);
    }

    // The tank first, then its pipes, not kept whole: the chain of rebuilds carries every mole down to 665.
    [Fact]
    public void TheTankBeforeItsPipesCarriesTheGasDownTheChain()
    {
        TakedownOutcome outcome = PipeTakedown.Run(Line, LineLinks, new long[] { 660, 663, 664 }, false, 602.0);
        TakedownPart part = Assert.Single(outcome.Parts);
        Assert.Equal(602.0, part.Moles, 6);
        Assert.Equal(0.0, outcome.LostMol, 6);
    }

    // F6: a tank in the middle, alone: the game divides the gas between the two sides by volume (150 mol each).
    [Fact]
    public void AMiddleTankAloneDividesTheGasByVolume()
    {
        List<TakedownMember> members = new List<TakedownMember>
        {
            new TakedownMember(840, 10.0, 60795.0),
            new TakedownMember(843, 10.0, 60795.0),
            new TakedownMember(837, 100.0, null),
            new TakedownMember(844, 10.0, 60795.0),
            new TakedownMember(845, 10.0, 60795.0)
        };
        TakedownOutcome outcome = PipeTakedown.Run(members, Links((840, 843), (843, 837), (837, 844), (844, 845)),
            new long[] { 837 }, false, 300.0);
        Assert.True(outcome.Split);
        Assert.Equal(2, outcome.Parts.Count);
        Assert.Equal(150.0, outcome.Parts[0].Moles, 6);
        Assert.Equal(150.0, outcome.Parts[1].Moles, 6);
        Assert.Equal(20.0, outcome.Parts[0].VolumeL, 6);
        Assert.Equal(0.0, outcome.LostMol, 6);
    }

    // A-B-T-C, removing B then T: B's split gives A its share by volume and T+C the rest, which T hands to C; the
    // parts do not end at one pressure, so a pooled forecast would be wrong for C.
    [Fact]
    public void ASplitWithTheTankGoingLastGivesThePartBesideTheTankItsVolume()
    {
        List<TakedownMember> members = new List<TakedownMember>
        {
            new TakedownMember(1, 10.0, 100.0),
            new TakedownMember(2, 10.0, 100.0),
            new TakedownMember(3, 60.0, null),
            new TakedownMember(4, 20.0, 50.0)
        };
        TakedownOutcome outcome = PipeTakedown.Run(members, Links((1, 2), (2, 3), (3, 4)), new long[] { 2, 3 },
            false, 100.0);
        Assert.Equal(2, outcome.Parts.Count);
        Assert.Equal(new List<long> { 1 }, outcome.Parts[0].Members);
        Assert.Equal(100.0 * 10.0 / 90.0, outcome.Parts[0].Moles, 6);
        Assert.Equal(100.0 * 80.0 / 90.0, outcome.Parts[1].Moles, 6);
        Assert.Equal(50.0, outcome.Parts[1].LowestKpa);
        Assert.Equal(0.0, outcome.LostMol, 6);
    }

    // A split's would_burst names the network left that is over its rating and says the gas is divided.
    [Fact]
    public void ASplitSqueezeNamesThePartAndTheDivision()
    {
        GuardFinding? finding = RemovalRule.Squeeze(new NetworkSqueeze(661, 100.0, 90000.0, 70.0, 20.0, 50000.0, 2));
        Assert.NotNull(finding);
        Assert.Equal("would_burst", finding!.Code);
        Assert.Contains("divides the gas among the 2 networks left", finding.Message);
        Assert.Contains("the 20 L one gets", finding.Message);
        Assert.Contains("44.5 %", finding.Message);
    }

    [Fact]
    public void RemovingEveryMemberLosesEveryMole()
    {
        TakedownOutcome outcome = PipeTakedown.Run(Line, LineLinks, new long[] { 663, 664, 665, 660 }, false, 50.0);
        Assert.Empty(outcome.Parts);
        Assert.Equal(50.0, outcome.LostMol, 6);
    }
}
