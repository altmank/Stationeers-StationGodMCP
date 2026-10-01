#nullable enable

using System.Collections.Generic;
using System.Text.Json;
using StationGodMCP.Pure;
using StationGodMCP.Server;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>deep_miner_spots: the nearest-first disc search, bearings, ore matching and the sidecar schema.</summary>
public sealed class DeepMinerSpotsTests
{
    [Theory]
    [InlineData(0, 10, 0, "N")]
    [InlineData(10, 0, 90, "E")]
    [InlineData(0, -10, 180, "S")]
    [InlineData(-10, 0, 270, "W")]
    [InlineData(10, 10, 45, "NE")]
    [InlineData(-1, 100, 359.4, "N")]
    public void BearingsAreCompassDegreesFromPlusZ(double dx, double dz, double degrees, string compass)
    {
        Bearing bearing = Bearing.Of(dx, dz);

        Assert.Equal(degrees, bearing.Degrees, 1);
        Assert.Equal(compass, bearing.Compass);
    }

    [Fact]
    public void RingsCoverTheSquareOnce()
    {
        HashSet<(int, int)> seen = new HashSet<(int, int)>();
        for (int ring = 0; ring <= 3; ring++)
        {
            foreach ((int i, int j) cell in MinerSpotSearch.Ring(ring))
            {
                Assert.True(seen.Add(cell));
            }
        }

        Assert.Equal(49, seen.Count);
    }

    [Fact]
    public void TheNearestMatchWinsAndTheSearchStopsEarly()
    {
        // Matches everywhere x >= 30: the nearest is (30, 0), 30 m east.
        MinerSearchResult result = new MinerSpotSearch(1, 500, 0, 1, 1000000)
            .Run(0, 0, (x, _) => x >= 30, () => true);

        MinerSample spot = Assert.Single(result.Spots);
        Assert.Equal(30, spot.X);
        Assert.Equal(0, spot.Z);
        Assert.False(result.Truncated);
        Assert.True(result.SearchedRadius < 40);
    }

    [Fact]
    public void SpotsKeepTheirSeparation()
    {
        MinerSearchResult result = new MinerSpotSearch(1, 200, 50, 3, 1000000)
            .Run(0, 0, (x, _) => x >= 30, () => true);

        Assert.Equal(3, result.Spots.Count);
        for (int a = 0; a < result.Spots.Count; a++)
        {
            for (int b = a + 1; b < result.Spots.Count; b++)
            {
                double dx = result.Spots[a].X - result.Spots[b].X;
                double dz = result.Spots[a].Z - result.Spots[b].Z;
                Assert.True(System.Math.Sqrt(dx * dx + dz * dz) >= 50);
            }
        }

        Assert.True(result.Spots[0].Distance <= result.Spots[1].Distance);
    }

    [Fact]
    public void NothingWithinTheRadiusSearchesItAll()
    {
        MinerSearchResult result = new MinerSpotSearch(2, 20, 0, 5, 1000000)
            .Run(100, 100, (_, _) => false, () => true);

        Assert.Empty(result.Spots);
        Assert.Equal(20, result.SearchedRadius);
        Assert.False(result.Truncated);
        Assert.Equal(0, result.Matched);
    }

    [Fact]
    public void TheBudgetTruncates()
    {
        MinerSearchResult result = new MinerSpotSearch(1, 1000, 0, 5, 50)
            .Run(0, 0, (_, _) => false, () => true);

        Assert.True(result.Truncated);
        Assert.True(result.Samples < 200);
    }

    [Fact]
    public void TheStepStaysWithinTheBudgetButNotBelowTheMap()
    {
        Assert.Equal(4, MinerSpotSearch.StepFor(100, 4, 250000));
        double step = MinerSpotSearch.StepFor(3000, 1, 250000);
        Assert.True(System.Math.PI * (3000 / step) * (3000 / step) <= 250000);
    }

    [Fact]
    public void AProfileMatchesWhenItHoldsEveryOre()
    {
        Dictionary<string, double> goldSilver = new Dictionary<string, double>
        {
            ["Gold"] = 0.1, ["Silver"] = 0.1, ["Iron"] = 0.0
        };
        Dictionary<string, double> gold = new Dictionary<string, double> { ["Gold"] = 0.2, ["Silver"] = 0.0 };

        Assert.True(new OreQuery(new List<string> { "gold", "SILVER" }).Matches(goldSilver));
        Assert.False(new OreQuery(new List<string> { "Gold", "Silver" }).Matches(gold));
        Assert.True(new OreQuery(new List<string> { "Gold" }).Matches(gold));
        Assert.False(new OreQuery(new List<string> { "Iron" }).Matches(goldSilver));
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"at":[700,200,680],"ores":["Cobalt"],"radius_m":1500,"count":3,"min_separation_m":50}""")]
    [InlineData("""{"ores":["Gold","Silver"],"step_m":8}""")]
    public void TheSidecarTakesItsArguments(string arguments)
    {
        using JsonDocument document = JsonDocument.Parse(arguments);
        Assert.Empty(ArgumentCheck.Problems(Program.InputSchemas["deep_miner_spots"], document.RootElement));
    }

    [Fact]
    public void TheSidecarRefusesTooManyOres()
    {
        using JsonDocument document = JsonDocument.Parse("""{"ores":["a","b","c","d","e"]}""");
        Assert.NotEmpty(ArgumentCheck.Problems(Program.InputSchemas["deep_miner_spots"], document.RootElement));
    }
}
