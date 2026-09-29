#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// remove_structure allow_burst (LU 2026-09-29): an in-line tank or passive vent whose removal squeezes the network left
/// past its weakest pipe is refused (would_burst) unless allow_burst, e.g. outdoors, where a burst into the atmosphere
/// is acceptable. With it the removal goes ahead and warns will_burst with the forecast, the pipes expected to burst,
/// where they leak and the gas expected out; the job's gas check expects that release (none to all of it gone).
/// </summary>
public sealed class AllowBurstTests
{
    private const double Rating = 60795.0;

    // structures-23: 605 mol N2 left in 20 L of network 2679 at 73 688 kPa, two pipes rated 60 795 kPa.
    private static NetworkSqueeze Squeeze() => new NetworkSqueeze(2679, 5460.0, 73688.0, 250.0, 20.0, Rating);

    private static BurstForecast Forecast(params BurstPipe[] pipes)
    {
        double share = RemovalRule.ReleasedShare(73688.0, pipes);
        return new BurstForecast(Squeeze(), 605.0, new List<BurstPipe>(pipes),
            pipes.Length > 0
                ? new List<ReleasedGas> { new ReleasedGas("Nitrogen", 600.0 * share), new ReleasedGas("Oxygen", 5.0 * share) }
                : new List<ReleasedGas>());
    }

    private static Args Of(string json) => new Args(JObject.Parse(json));

    private static string CodeOf(System.Action action) => Assert.Throws<ApiException>(action).Code;

    [Fact]
    public void WithoutAllowBurstTheSqueezeIsStillRefusedAndNamesTheOverride()
    {
        GuardFinding? finding = RemovalRule.Squeeze(Squeeze());

        Assert.NotNull(finding);
        Assert.Equal("would_burst", finding!.Code);
        Assert.Equal(GuardLevel.Refusal, finding.Level);
        Assert.Contains("17.5 %", finding.Message);
        Assert.Contains("allow_burst", finding.Message);
        Assert.False(new RemovalAllowance(true, true, true).Burst);
    }

    [Fact]
    public void AllowedTheBurstIsAWarningNamingPressureRatingPipesPlaceAndGas()
    {
        BurstForecast forecast = Forecast(
            new BurstPipe(2677, "StructurePipeStraight", Rating, BurstPipe.Outdoors, 9.0),
            new BurstPipe(2678, "StructurePipeStraight", Rating, "812", 101.3));

        GuardFinding finding = RemovalRule.WillBurst(forecast);

        Assert.Equal("will_burst", finding.Code);
        Assert.Equal(GuardLevel.Warning, finding.Level);
        Assert.Contains("73688 kPa", finding.Message);
        Assert.Contains("rated 60795 kPa", finding.Message);
        Assert.Contains("pipe network 2679", finding.Message);
        Assert.Contains("allow_burst is set", finding.Message);
        Assert.Contains("StructurePipeStraight 2677 (outdoors, 9 kPa there)", finding.Message);
        Assert.Contains("StructurePipeStraight 2678 (room 812, 101.3 kPa there)", finding.Message);
        Assert.Contains("into outdoors or room 812", finding.Message);
        Assert.Contains("Nitrogen", finding.Message);
        Assert.Contains("of the 605 mol it holds", finding.Message);
        Assert.Equal(new List<string> { "outdoors", "812" }, forecast.Where);
        Assert.True(forecast.Bursts);
    }

    [Fact]
    public void TheReleaseLeaksDownToTheLowestPressureWhereAPipeBursts()
    {
        List<BurstPipe> pipes = new List<BurstPipe>
        {
            new BurstPipe(1, "StructurePipeStraight", Rating, "5", 100.0),
            new BurstPipe(2, "StructurePipeStraight", Rating, BurstPipe.Outdoors, 10.0)
        };

        Assert.Equal(1.0 - 10.0 / 73688.0, RemovalRule.ReleasedShare(73688.0, pipes), 9);
        Assert.Equal(0.0, RemovalRule.ReleasedShare(73688.0, new List<BurstPipe>()));
        Assert.Equal(0.0, RemovalRule.ReleasedShare(50.0,
            new List<BurstPipe> { new BurstPipe(1, "StructurePipeStraight", 10.0, "5", 100.0) }));

        BurstForecast forecast = Forecast(pipes.ToArray());
        Assert.Equal(605.0 * (1.0 - 10.0 / 73688.0), forecast.ReleasedMol, 6);
    }

    [Fact]
    public void WithNoWeakestPipeInAirNothingIsExpectedToBurst()
    {
        BurstForecast forecast = Forecast();

        GuardFinding finding = RemovalRule.WillBurst(forecast);

        Assert.False(forecast.Bursts);
        Assert.Equal(0.0, forecast.ReleasedMol);
        Assert.Equal("will_burst", finding.Code);
        Assert.Equal(GuardLevel.Warning, finding.Level);
        Assert.Contains("none is expected to burst", finding.Message);
    }

    [Fact]
    public void TheReplyListsEachBurstWithItsPipesAndGases()
    {
        BurstView view = new BurstView(Forecast(
            new BurstPipe(2677, "StructurePipeStraight", Rating, BurstPipe.Outdoors, 9.0)));
        JObject wire = JObject.Parse(WireCheck.New(view));

        Assert.Equal("2679", (string?)wire["network_id"]);
        Assert.Equal(73688.0, (double)wire["pressure_after_kpa"]!);
        Assert.Equal(Rating, (double)wire["rating_kpa"]!);
        Assert.Equal("2677", (string?)wire["pipes"]![0]!["reference_id"]);
        Assert.Equal("outdoors", (string?)wire["pipes"]![0]!["where"]);
        Assert.Equal("outdoors", (string?)wire["where"]![0]);
        Assert.Equal("Nitrogen", (string?)wire["gases"]![0]!["gas"]);
        Assert.Equal(605.0, (double)wire["holds_mol"]!);
        Assert.True((double)wire["released_mol"]! > 604.0);
    }

    [Fact]
    public void RemovalsReadAllowBurst()
    {
        BuildForm<RemoveArguments>.Run run = Assert.IsType<BuildForm<RemoveArguments>.Run>(
            BuildArgs.ParseRemove(Of("{\"reference_ids\":[\"2681\"],\"allow_burst\":true}")));
        Assert.True(run.Arguments.Allow.Burst);
        Assert.False(run.Arguments.Allow.Contents);

        BuildForm<RemoveArguments>.Run plain = Assert.IsType<BuildForm<RemoveArguments>.Run>(
            BuildArgs.ParseRemove(Of("{\"reference_ids\":[\"2681\"]}")));
        Assert.False(plain.Arguments.Allow.Burst);
        Assert.Equal(ApiErrors.InvalidArgumentCode,
            CodeOf(() => BuildArgs.ParseRemove(Of("{\"job_id\":\"remove-3\",\"allow_burst\":true}"))));
    }
}

/// <summary>The gas check of a job whose plan lets a network burst (allow_burst): the release is expected.</summary>
public sealed class PlannedBurstGasCheckTests
{
    private const double Rating = 60795.0;

    private static GasMix N2(double mol) => new GasMix(new[] { mol }, new[] { mol * 6000.0 });

    private static NetworkGas Net(long id, double mol, double volumeL, params long[] members) =>
        new NetworkGas(id, N2(mol), volumeL, members, new long[0], null, Rating);

    private static double IdealKpa(NetworkGas network, GasMix contents) =>
        contents.TotalMol * 8.314462618 * 293.0 / network.VolumeL;

    // Network 2679: tank 2681 between pipes 2677 and 2678; the tank goes, the pipes keep all 605 mol in 20 L.
    private static List<NetworkGas> Before() => new List<NetworkGas> { Net(2679, 605.0, 270.0, 2677, 2681, 2678) };

    private static List<NetworkGas> After(double mol) => new List<NetworkGas> { Net(2679, mol, 20.0, 2677, 2678) };

    private static readonly List<PlannedGasLoss> Planned =
        new List<PlannedGasLoss> { PlannedGasLoss.Burst(2679, 605.0, 605.0) };

    private static GasCheckView ViewOf(GasAudit audit, GasRefillPlan plan) =>
        GasCheckView.Of(audit, plan.Refills, new List<long>(),
            GasOrphans.Of(new List<NetworkGas>(), new List<NetworkGas>()), plan.Withheld);

    [Theory]
    [InlineData(605.0)]
    [InlineData(300.0)]
    [InlineData(0.0)]
    public void AnythingFromNoneToAllOfThePlannedReleaseGoneIsOk(double left)
    {
        GasAudit audit = GasAudit.Of(Before(), After(left), GasTolerance.Default, Planned);
        GasRefillPlan plan = GasRefills.Plan(audit, GasTolerance.Default, IdealKpa);
        GasCheckView check = ViewOf(audit, plan);

        Assert.True(audit.Ok);
        GasFamily family = Assert.Single(audit.Families);
        Assert.True(family.Conserved);
        Assert.Equal(605.0, family.PlannedRelease.TotalMol, 6);
        Assert.Equal(0.0, family.PlannedLoss.TotalMol, 6);
        Assert.Empty(plan.Refills);
        Assert.Empty(plan.Withheld);
        Assert.Equal(605.0, check.Families[0].PlannedReleaseMol, 6);
        Assert.Contains("Up to 605 mol may leak out", check.Summary);
        Assert.Equal("applied", GasCheckView.JobStatus("applied", check));
    }

    [Fact]
    public void GasThatAppearsIsStillAFailure()
    {
        GasAudit audit = GasAudit.Of(Before(), After(700.0), GasTolerance.Default, Planned);

        Assert.False(audit.Ok);
    }

    [Fact]
    public void ALossBeyondThePlannedReleaseIsRefilledOnlyBeyondIt()
    {
        List<PlannedGasLoss> half = new List<PlannedGasLoss> { PlannedGasLoss.Burst(2679, 302.5, 605.0) };
        GasAudit audit = GasAudit.Of(Before(), After(200.0), GasTolerance.Default, half);
        GasRefillPlan plan = GasRefills.Plan(audit, GasTolerance.Default, static (_, _) => 0.0);

        Assert.False(audit.Ok);
        GasRefill refill = Assert.Single(plan.Refills);
        Assert.Equal(102.5, refill.Gas.TotalMol, 6);
    }

    [Fact]
    public void WithoutAPlannedBurstTheCheckIsUnchanged()
    {
        GasAudit audit = GasAudit.Of(Before(), After(300.0), GasTolerance.Default);

        Assert.False(audit.Ok);
        Assert.Equal(0.0, Assert.Single(audit.Families).PlannedRelease.TotalMol);
        Assert.False(new PlannedGasLoss(1, 0.5).AtMost);
        Assert.True(PlannedGasLoss.Burst(1, 1.0, 2.0).AtMost);
        Assert.Equal(0.5, PlannedGasLoss.Burst(1, 1.0, 2.0).Share, 9);
    }
}
