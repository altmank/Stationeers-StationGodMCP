#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>Fixes from round 2 of the 1.4.4 live test of the structure tools (structures-22..29).</summary>
public sealed class StructuresRound2Tests
{
    // structures-23: in-line tank 2681 removed from network 2679 (270 L); 605 mol N2 stayed in two pipes rated
    // 60 795 kPa and read 73 688 kPa.
    [Fact]
    public void ATankRemovalThatSqueezesItsGasOverTheWeakestPipeIsWouldBurst()
    {
        GuardFinding? finding = RemovalRule.Squeeze(new NetworkSqueeze(2679, 5460.0, 73688.0, 250.0, 20.0, 60795.0));
        Assert.NotNull(finding);
        Assert.Equal("would_burst", finding!.Code);
        Assert.Equal(GuardLevel.Refusal, finding.Level);
        Assert.Contains("73688 kPa after", finding.Message);
        Assert.Contains("rated 60795 kPa", finding.Message);
        Assert.Contains("pipe network 2679", finding.Message);
        // 1 - 60795 / 73688 = 17.5 %: what must come out first.
        Assert.Contains("17.5 %", finding.Message);
    }

    [Fact]
    public void ASqueezeUnderTheRatingOrWithNoRatedPipeLeftIsNotRefused()
    {
        Assert.Null(RemovalRule.Squeeze(new NetworkSqueeze(1, 100.0, 60795.0, 250.0, 20.0, 60795.0)));
        Assert.Null(RemovalRule.Squeeze(new NetworkSqueeze(1, 100.0, 90000.0, 250.0, 20.0, null)));
    }

    // structures-22: from_id 2047 was the locker being removed; the refund went into it and was destroyed.
    [Fact]
    public void ARefundHolderTheRequestRemovesIsNamed()
    {
        string own = RemovalRule.HolderRemoved("Locker (StructureStorageLocker 2047)", null);
        Assert.Contains("from_id Locker (StructureStorageLocker 2047) is removed by this request", own);
        Assert.Contains("refund_to ground or none", own);
        string inside = RemovalRule.HolderRemoved("Iron Sheets (ItemIronSheets 12)", "Locker (StructureStorageLocker 2047)");
        Assert.Contains("is inside Locker (StructureStorageLocker 2047), which this request removes", inside);
    }

    // structures-27: wall 457 and the frame 1323 behind it both open the wall's face; the frame reported it again.
    [Fact]
    public void AFaceAnEarlierBreachNamedIsNotTheNextPiecesBreach()
    {
        BreachedFaces breached = new BreachedFaces();
        GridPoint wallFace = new GridPoint(-10650, 2210, -7100);
        GridPoint frameSide = new GridPoint(-10640, 2210, -7090);
        List<GridPoint> wall = breached.Unnamed(new[] { wallFace });
        Assert.Equal(new[] { wallFace }, wall);
        breached.Claim(wall);
        Assert.Equal(new[] { frameSide }, breached.Unnamed(new[] { wallFace, frameSide, frameSide }));
    }

    [Fact]
    public void AFaceIsNamedOnlyWhenItsBreachWasReported()
    {
        BreachedFaces breached = new BreachedFaces();
        GridPoint face = new GridPoint(0, 10, 0);
        Assert.Single(breached.Unnamed(new[] { face }));
        // Nothing claimed (the pressures on both sides matched): the next piece still sees the face.
        Assert.Single(breached.Unnamed(new[] { face }));
    }

    // Devices round 2: find_spot answered 0 spots, 31 rejected, and no reason.
    [Fact]
    public void SpotReasonsGroupByWordingWithoutNumbersMostFrequentFirst()
    {
        SpotReasons reasons = new SpotReasons();
        reasons.Add("2 cell(s) taken");
        reasons.Add("the game refuses it: Placement requires a Frame below for support");
        reasons.Add("3 cell(s) taken");
        reasons.Add("the game refuses it: Placement requires a Frame below for support");
        reasons.Add("the game refuses it: Placement requires a Frame below for support");
        reasons.Add("in a door's keep-out");
        List<(string Reason, int Count)> top = reasons.Top(8);
        Assert.Equal(3, top.Count);
        Assert.Equal(("the game refuses it: Placement requires a Frame below for support", 3), top[0]);
        Assert.Equal(("2 cell(s) taken", 2), top[1]);
        Assert.Equal(("in a door's keep-out", 1), top[2]);
        Assert.Single(reasons.Top(1));
    }

    [Fact]
    public void FindSpotReportsItsReasons()
    {
        FindSpotView view = new FindSpotView("StructureBattery", new List<string> { "y=228 seen from +y" },
            new List<SpotView>(), 31, 0, 31, 31,
            new List<SpotReasonView> { new SpotReasonView("the game refuses it: requires a Frame below", 31) });
        JObject json = JObject.Parse(WireCheck.New(view));
        Assert.Equal(31, (int)json["reasons"]![0]!["count"]!);
        Assert.Equal("the game refuses it: requires a Frame below", (string)json["reasons"]![0]!["reason"]!);
        Assert.Empty((JArray)JObject.Parse(WireCheck.New(new FindSpotView(null, new List<string>(),
            new List<SpotView>(), 0, 0, 0, 0)))["reasons"]!);
    }

    // structures-28: a bad at.from or at.frame is a top-level invalid_argument, as every other bad argument is.
    [Theory]
    [InlineData("{\"relative_to\":\"1412\",\"from\":\"sideways\"}")]
    [InlineData("{\"relative_to\":\"1412\",\"frame\":\"mine\"}")]
    [InlineData("{\"crosshair\":true,\"from\":\"up\"}")]
    public void BadRelativeWordsAreRefusedWithTheArguments(string at)
    {
        ApiException error = Assert.Throws<ApiException>(() => BuildArgs.AtOf(JObject.Parse(at), "at"));
        Assert.Equal(ApiErrors.InvalidArgumentCode, error.Code);
        Assert.StartsWith("at.", error.Message);
    }

    [Fact]
    public void GoodRelativeWordsStillPass()
    {
        Assert.IsType<AtArg.Relative>(BuildArgs.AtOf(
            JObject.Parse("{\"relative_to\":\"1412\",\"frame\":\"World\",\"from\":\"Top\"}"), "at"));
    }
}
