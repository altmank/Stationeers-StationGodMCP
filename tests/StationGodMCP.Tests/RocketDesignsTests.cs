using System.Collections.Generic;
using Newtonsoft.Json;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Rockets;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// Every rocket design the game builds, offline: the six engines' feed laws (decompile Assets.Scripts.Objects.Pipes
/// GovernedGasEngine, PressureFedEngine, PressureFedGasEngine(+Heavy), PumpedLiquidEngine, PressureFedLiquidEngine
/// (+Heavy)), gas and liquid lines, payload deploys and rocket-to-rocket transfers at a stop, landing at an orbital
/// mount, and the mining formulas (MineableDeposit, RocketMiner, RocketGasCollector, RocketMiningDrillHead). Expected
/// numbers are worked out by hand from the game's formulas, not by calling the code under test.
/// </summary>
public sealed class RocketDesignsTests
{
    private const float Step = 0.02f;
    private const double Tick = 0.5;

    private static FlightSimulator Simulator => new FlightSimulator(Step, 0.5f);

    // Methane/oxygen premix gas at 300 K: 16.7 g/mol, no liquid.
    private static LinePhases GasPhases(double kelvin = 300.0) => new LinePhases(0.0167, 0.0, 0.0, kelvin);

    // Liquid hydrazine (0.03 L/mol, 32 g/mol) under a nitrogen pressurant (28 g/mol).
    private static LinePhases HydrazinePhases => new LinePhases(0.028, 0.032, 0.03, 300.0);

    // 900 N per mole drawn, whatever the make-up; exhaust is the propellant's own mass.
    private static IThrustModel Linear(double newtonsPerMol = 900.0) =>
        new ThrustFunction(draw => new ThrustReading(newtonsPerMol * draw.Total, draw.Total * 0.0167));

    private static PowerBank Battery() => new PowerBank(3600000.0, 3600000.0, 10.0, 200.0);

    // ---- engines: feed laws ----

    [Fact]
    public void PressureFedGasDrawsByEachInputsOwnPressure()
    {
        // p = 3,000 kPa in a 100 L pipe at 300 K: r = 3000/60795, most = 5000 x (3 - 2.999/(1 + (r/2)^0.7)) = 1050.2 kPa,
        // moved = 3000 x (1050.2 - 101.325)/60795 + 101.325 = 148.15 kPa over 10 L at 300 K = 0.5939 mol.
        double moles = 3000.0 * 100.0 / (8.3144 * 300.0);
        FuelLine fuel = new FuelLine(100.0, moles, 0.0, new List<FuelTank>(), GasPhases());
        FuelLine oxidiser = new FuelLine(100.0, moles / 2.0, 0.0, new List<FuelTank>(), GasPhases());
        PressureFedGasFeed feed = new PressureFedGasFeed(5000.0, 101.325);

        EngineDraw draw = feed.Draw(100f, fuel, oxidiser);

        Assert.Equal(0.5939, draw.Input1.Gas, 3);
        Assert.True(draw.Input2.Gas < draw.Input1.Gas, "the oxidiser line is at half the pressure");
        Assert.True(feed.NeedsInput2);
        Assert.Equal(0.5939 / 2.0, new PressureFedGasFeed(5000.0, 101.325).Draw(50f,
            new FuelLine(100.0, moles, 0.0, new List<FuelTank>(), GasPhases()), oxidiser).Input1.Gas, 3);
    }

    [Fact]
    public void TheHeavyPressureFedGasEngineMovesMoreAtTheSamePressure()
    {
        // MAXPressurePerTick 8,500 instead of 5,000: 0.7393 mol at 3,000 kPa and 300 K.
        Assert.Equal(0.7393, new PressureFedGasFeed(8500.0, 101.325).MolesPerTick(3000.0, 300.0,
            PressureFedFeed.GasPipeMaxKpa), 3);
        Assert.Equal(0.5939, new PressureFedGasFeed(5000.0, 101.325).MolesPerTick(3000.0, 300.0,
            PressureFedFeed.GasPipeMaxKpa), 3);
        // At 0 kPa it still asks for PressurePerTick's worth (101.325 kPa over 10 L): 0.4062 mol.
        Assert.Equal(0.4062, new PressureFedGasFeed(5000.0, 101.325).MolesPerTick(0.0, 300.0,
            PressureFedFeed.GasPipeMaxKpa), 3);
    }

    [Fact]
    public void PressureFedThrustSagsAsTheLineDrains()
    {
        // One small tank, no refill: each tick draws by the falling pressure, so the second half of the hop pushes less.
        double moles = 8000.0 * 300.0 / (8.3144 * 300.0);
        FuelLine fuel = new FuelLine(50.0, moles / 7.0, 0.0,
            new List<FuelTank> { new FuelTank(300.0, moles * 6.0 / 7.0, true) }, GasPhases());
        FuelLine oxidiser = fuel.Copy();
        List<EngineUnit> engines = new List<EngineUnit>
        {
            new EngineUnit("Pressure Fed Gas Engine", new PressureFedGasFeed(5000.0, 101.325), 0, 1, Linear(9000.0))
        };
        RocketCraft craft = new RocketCraft(400f, new List<FuelLine> { fuel, oxidiser }, engines, 9000f, 9000f, true,
            100f, true, 0f, Battery());

        double first = craft.Copy().Engines[0].Burn(100f, craft.Copy().Lines);
        LegResult hop = new SpaceHop("A", "B", 5000f, 0f).Fly(Simulator, craft);

        Assert.IsType<LegOutcome.Arrived>(hop.Outcome);
        Assert.True(craft.Force < first * 0.9, $"thrust {craft.Force} N after the hop should be below the first tick's {first} N");
        Assert.True(craft.Lines[0].PressureKpa < 8000.0);
    }

    [Fact]
    public void PumpedLiquidMetersVolumeBySettingAndLeavesTheGas()
    {
        // 0.55 L x 100 % throttle: Setting 75 gives 0.4125 L from input 1, 0.1375 L from input 2; at 0.03 L/mol that is
        // 13.75 and 4.583 mol.
        FuelLine one = new FuelLine(100.0, 5.0, 1000.0, new List<FuelTank>(), HydrazinePhases);
        FuelLine two = new FuelLine(100.0, 5.0, 1000.0, new List<FuelTank>(), HydrazinePhases);

        EngineDraw draw = new PumpedLiquidFeed(75f).Draw(100f, one, two);

        Assert.Equal(13.75, draw.Input1.Liquid, 3);
        Assert.Equal(4.583, draw.Input2.Liquid, 2);
        Assert.Equal(0.0, draw.Input1.Gas);
        Assert.Equal(5.0, one.PipeGasMoles);
    }

    [Fact]
    public void LiquidHydrazineAloneRunsThePumpedLiquidEngineAtSetting100()
    {
        // Setting 100: all 0.55 L a tick from input 1 (18.33 mol of hydrazine), input 2 untouched though it must exist.
        FuelLine hydrazine = new FuelLine(100.0, 0.0, 3000.0, new List<FuelTank>(), HydrazinePhases);
        FuelLine empty = new FuelLine(20.0, 0.0, 0.0, new List<FuelTank>(), HydrazinePhases);
        PumpedLiquidFeed feed = new PumpedLiquidFeed(100f);

        EngineDraw draw = feed.Draw(100f, hydrazine, empty);

        Assert.Equal(0.55 / 0.03, draw.Input1.Liquid, 3);
        Assert.Equal(0.0, draw.Input2.Total);
        Assert.True(feed.NeedsInput2, "the engine is inoperable without a second input network (PumpedLiquidEngine.cs:25)");
        Assert.True(feed.HasFuel(hydrazine, empty));
        Assert.False(new PumpedLiquidFeed(0f).HasFuel(hydrazine, empty));
    }

    [Fact]
    public void PressureFedLiquidLitresFollowTheGasPressure()
    {
        // 0.04 + p x (0.8 - 0.04)/6079.5, clamped: 0.04 L at 0, 0.4150 L at 3,000 kPa, 0.8 at and past 6,079.5.
        PressureFedLiquidFeed normal = new PressureFedLiquidFeed(0.04, 0.8, 5000.0, 101.325);
        PressureFedLiquidFeed heavy = new PressureFedLiquidFeed(0.04, 1.5, 6000.0, 101.325);

        Assert.Equal(0.04, normal.LitresPerTick(0.0), 4);
        Assert.Equal(0.4150, normal.LitresPerTick(3000.0), 3);
        Assert.Equal(0.8, normal.LitresPerTick(9000.0), 4);
        Assert.Equal(0.7605, heavy.LitresPerTick(3000.0), 3);
        Assert.Equal(1.5, heavy.LitresPerTick(6079.5), 3);
        Assert.False(normal.NeedsInput2);
        Assert.False(normal.Input2IsPropellant, "input 2 is a heat exchanger (PressureFedLiquidEngine.cs:72-88)");
    }

    [Fact]
    public void PressureFedLiquidAlsoTakesAGasDrawFromTheSameLine()
    {
        // Pressurant: 3,000 kPa of gas over the liquid. Liquid: 0.415 L / 0.03 = 13.83 mol. Then MoveGas with the liquid
        // pipe's 6,079.5 kPa limit: 1.197 mol at 300 K, taken from gas and liquid in proportion.
        FuelLine line = new FuelLine(1000.0, 0.0, 10000.0, new List<FuelTank>(), HydrazinePhases);
        double gasVolume = 1000.0 - 10000.0 * 0.03;
        line.PipeGasMoles = 3000.0 * gasVolume / (8.3144 * 300.0);
        double before = line.PipeLiquidMoles;

        EngineDraw draw = new PressureFedLiquidFeed(0.04, 0.8, 5000.0, 101.325).Draw(100f, line, null);

        Assert.Equal(1.197, draw.Input1.Total - 0.4150 / 0.03, 2);
        Assert.Equal(before - draw.Input1.Liquid, line.PipeLiquidMoles, 6);
        Assert.True(draw.Input1.Gas > 0.0);
    }

    [Fact]
    public void LiquidTakesItsVolumeFromTheGasAndMixesByWholeVolume()
    {
        // 1,000 mol of liquid at 0.03 L/mol fills 30 L of a 100 L pipe: 10 mol of gas at 300 K reads n R T / 70 L.
        FuelLine line = new FuelLine(100.0, 10.0, 1000.0,
            new List<FuelTank> { new FuelTank(900.0, 0.0, 0.0, true) }, HydrazinePhases);

        Assert.Equal(10.0 * 8.3144 * 300.0 / 70.0, line.PipeGasPressureKpa, 6);
        line.Mix();
        Assert.Equal(900.0, line.Tanks[0].LiquidMoles, 6);
        Assert.Equal(9.0, line.Tanks[0].GasMoles, 6);
        Assert.Equal(1000.0 * 0.032 + 10.0 * 0.028, line.CountedMassKg, 6);
        Assert.Equal(100.0, line.TakeLiquidLitres(3.0), 6);
    }

    [Fact]
    public void ALiquidEngineWithOnlyPressurantLeftIsOutOfFuel()
    {
        FuelLine line = new FuelLine(100.0, 50.0, 0.0, new List<FuelTank>(), HydrazinePhases);
        FuelLine second = new FuelLine(20.0, 0.0, 0.0, new List<FuelTank>(), HydrazinePhases);
        List<EngineUnit> engines = new List<EngineUnit> { new EngineUnit("Pumped Liquid Engine", new PumpedLiquidFeed(100f), 0, 1, Linear()) };
        RocketCraft craft = new RocketCraft(400f, new List<FuelLine> { line, second }, engines, 9000f, 9000f, true,
            100f, true, 0f, Battery());

        LegResult hop = new SpaceHop("A", "B", 50f, 0f).Fly(Simulator, craft);

        Assert.True(craft.OutOfFuel);
        Assert.Contains("No fuel left", Assert.IsType<LegOutcome.Stalled>(hop.Outcome).Reason);
    }

    [Fact]
    public void AHydrazineRocketLaunchesAndHopsOnItsPumpedLiquidEngine()
    {
        // 18.33 mol a tick at 1,400 N/mol (about 25.7 kN) lifts 1,500 kg against 5.5 m/s2 and flies 400 + 250 delta-v.
        FuelLine hydrazine = new FuelLine(100.0, 0.0, 600.0,
            new List<FuelTank> { new FuelTank(1500.0, 0.0, 30000.0, true) }, HydrazinePhases);
        FuelLine second = new FuelLine(20.0, 0.0, 0.0, new List<FuelTank>(), HydrazinePhases);
        List<EngineUnit> engines = new List<EngineUnit> { new EngineUnit("Pumped Liquid Engine", new PumpedLiquidFeed(100f), 0, 1, Linear(1400.0)) };
        RocketCraft craft = new RocketCraft(1500f, new List<FuelLine> { hydrazine, second }, engines, 20000f, 0f, true,
            100f, false, 0f, Battery());
        List<FlightLeg> plan = new List<FlightLeg>
        {
            new LaunchLeg("Pad", "Vulcan Orbit", 400f, -5.5f, LaunchStart.Pad),
            new SpaceHop("Vulcan Orbit", "Sparse Vapors", 250f, 0f)
        };

        FlightRun run = FlightForecast.Run(Simulator, craft, plan);

        Assert.True(run.Succeeded, run.Legs[run.Legs.Count - 1].Outcome.Detail);
        Assert.True(run.Final.Lines[0].LiquidMoles < 30600.0);
        Assert.Equal(0.0, run.Final.Lines[1].Moles);
    }

    [Fact]
    public void MixedGasAndLiquidEnginesBurnFromTheirOwnLines()
    {
        // Two engines, as code allows (the hull allows one, EngineFuselage.cs:44-48): each drains only its own line.
        FuelLine gas = new FuelLine(100.0, 1000.0, 0.0, new List<FuelTank>(), GasPhases());
        FuelLine liquid = new FuelLine(100.0, 0.0, 1000.0, new List<FuelTank>(), HydrazinePhases);
        FuelLine spare = new FuelLine(20.0, 0.0, 0.0, new List<FuelTank>(), HydrazinePhases);
        List<EngineUnit> engines = new List<EngineUnit>
        {
            new EngineUnit("Pumped Gas Engine", PumpedGasFeed.Instance, 0, null, Linear()),
            new EngineUnit("Pumped Liquid Engine", new PumpedLiquidFeed(100f), 1, 2, Linear())
        };
        RocketCraft craft = new RocketCraft(400f, new List<FuelLine> { gas, liquid, spare }, engines, 9000f, 9000f,
            true, 100f, true, 0f, Battery());

        double force = craft.Engines[0].Burn(100f, craft.Lines) + craft.Engines[1].Burn(100f, craft.Lines);

        Assert.Equal(982.0, craft.Lines[0].Moles, 6);
        Assert.Equal(1000.0 - 0.55 / 0.03, craft.Lines[1].Moles, 4);
        Assert.Equal(900.0 * (18.0 + 0.55 / 0.03), force, 1);
    }

    [Fact]
    public void BurnTimeRunsTheFeedsUntilNothingIsDrawn()
    {
        // 3,000 mol at 18 a tick: 166 full ticks and a part tick = 167 ticks of 0.5 s.
        FuelLine line = new FuelLine(100.0, 3000.0, 0.0, new List<FuelTank>(), GasPhases());
        List<EngineUnit> engines = new List<EngineUnit> { new EngineUnit("Pumped Gas Engine", PumpedGasFeed.Instance, 0, null, Linear()) };
        RocketCraft craft = new RocketCraft(400f, new List<FuelLine> { line }, engines, 9000f, 9000f, true, 100f, true,
            0f, Battery());

        Assert.Equal(83.5, BurnTime.Seconds(craft, 100f, Tick));
        Assert.Equal(167.0, BurnTime.Seconds(craft, 50f, Tick));
        Assert.Equal(3000.0, craft.FuelMoles);
    }

    [Theory]
    [InlineData("GovernedGasEngine", typeof(PumpedGasFeed))]
    [InlineData("PressureFedGasEngine", typeof(PressureFedGasFeed))]
    [InlineData("PressureFedGasEngineHeavy", typeof(PressureFedGasFeed))]
    [InlineData("PumpedLiquidEngine", typeof(PumpedLiquidFeed))]
    [InlineData("PressureFedLiquidEngine", typeof(PressureFedLiquidFeed))]
    [InlineData("PressureFedLiquidEngineHeavy", typeof(PressureFedLiquidFeed))]
    public void EverySixEngineClassHasItsFeed(string className, System.Type feed)
    {
        Assert.IsType(feed, EngineSpecs.FeedOf(className, 101.325, 50f));
        Assert.NotEqual(className, EngineSpecs.NameOf(className));
    }

    [Fact]
    public void AModsEngineClassHasNoFeed()
    {
        Assert.Null(EngineSpecs.FeedOf("SomeModEngine", 101.325, 50f));
    }

    // ---- mass, stops, landing ----

    [Fact]
    public void ADeployedPayloadLeavesItsMassAtTheStop()
    {
        RocketCraft craft = RocketForecastTestsShared.MkII(6000.0);
        float before = craft.StructureMassKg;

        new ParkLeg("Vulcan Orbit", 20f, 10.0, 0, -200.0, new List<LineTransfer>(), 0.0).Fly(Simulator, craft);

        Assert.Equal(before - 200f, craft.StructureMassKg, 1);
    }

    [Fact]
    public void FuelAndChargeTransferredAtTheStopArriveOnTheLine()
    {
        RocketCraft craft = RocketForecastTestsShared.MkII(3000.0, batteryJ: 1000000.0);

        new ParkLeg("Vulcan Orbit", 60f, 0.0, 0, 0.0, new List<LineTransfer> { new LineTransfer(0, 1500.0) }, 500000.0)
            .Fly(Simulator, craft);

        Assert.Equal(4500.0, craft.FuelMoles, 3);
        Assert.Equal(1500000.0, craft.Power.ChargeJ, 3);
    }

    [Fact]
    public void SocketsEqualiseByVolume()
    {
        // 100 mol in 1,000 L meets 500 mol in 1,000 L: both end at 300 mol, so this side gains 200.
        Assert.Equal(200.0, SocketTransfer.Equalised(100.0, 1000.0, 500.0, 1000.0), 9);
        Assert.Equal(-100.0, SocketTransfer.Equalised(400.0, 3000.0, 0.0, 1000.0), 9);
        Assert.Equal(0.0, SocketTransfer.Equalised(10.0, 0.0, 10.0, 0.0));
    }

    [Fact]
    public void TheOrbitalMountLandsWhatVulcanCannot()
    {
        // At 2,000 kg the Mk II's 16,390 N fails the check at 5.5 m/s2 (rockets.md: 1,882 kg), but at the orbital mount
        // (gravity -1, Rocket.cs:2174-2181) the check passes and it lands.
        LandingLeg surface = new LandingLeg("Vulcan Orbit", "Pad", 400f, -5.5f,
            new LandingStart.ReEntry(25000f, false, 200f, -5.5f));
        LandingLeg orbital = new LandingLeg("Low Orbit", "Orbital Launch Mount", 400f, -1f,
            new LandingStart.ReEntry(25000f, true, 200f, -5.5f));

        LegResult down = surface.Fly(Simulator, RocketForecastTestsShared.MkII(6000.0, dryKg: 2000f));
        LegResult up = orbital.Fly(Simulator, RocketForecastTestsShared.MkII(6000.0, dryKg: 2000f));

        Assert.IsType<LegOutcome.LandingAborted>(down.Outcome);
        Assert.IsType<LegOutcome.Landed>(up.Outcome);
    }

    // ---- mining ----

    private static DepositReading OreSite(float survey = 0f) =>
        new DepositReading(DepositReading.Ore, 5f, 4f, 5f, survey, 0.0, 1.0, false, false);

    private static DepositReading IceSite() => new DepositReading(DepositReading.Ice, 5f, 4f, 5f, 0f, 0.0, 0.0, true, false);

    private static DepositReading GasSite() => new DepositReading(DepositReading.Gas, 5f, 4f, 5f, 0f, 0.0, 0.0, false, true);

    private static DrillHeadReading Head(string name, float speed, float ore, float ice, float health = 2f) =>
        new DrillHeadReading(name, speed, ore, ice, health, 1f, 100f, 100f);

    private static MinerReading Miner(DrillHeadReading? head, bool hold = true) =>
        new MinerReading("Rocket Miner", 1f, 1f, 1f, head, true, 100.0, hold);

    [Fact]
    public void OreQuantityIsBaselineTimesRichnessToThe16()
    {
        // Size 5: baseline 2 + 4 x 4/9 = 3.778; richness 4: 4^1.6 = 9.190; 34.72 -> 35 (after the cycle's own
        // shrink, MineDeposit shrinks first: 34). Ice x4. Survey past 200 % x1.1.
        Assert.Equal(35, DepositReading.OreQuantity(DepositReading.Ore, 5f, 4f, 5f, 0f));
        Assert.Equal(139, DepositReading.OreQuantity(DepositReading.Ice, 5f, 4f, 5f, 0f));
        Assert.Equal(38, DepositReading.OreQuantity(DepositReading.Ore, 5f, 4f, 5f, 250f));
        Assert.Equal(43, DepositReading.OreQuantity(DepositReading.Ore, 5f, 4f, 5f, 1000f));
        Assert.Equal(1, DepositReading.OreQuantity(DepositReading.Ore, 0f, 4f, 5f, 0f));
        Assert.Equal(8f, DepositReading.TimeToMine(5f));
    }

    [Fact]
    public void AMineralHeadMinesOreWithItsFactor()
    {
        MachineYield yield = MiningYields.Miner(Miner(Head("Mineral", 1f, 1.2f, 0f)), OreSite(), Tick, 1);

        // 34 units after the shrink, x1.2 = 40.8 -> 41; 8 s cycle = 16 ticks; 3,600 / 8 x 41 = 18,450 an hour.
        Assert.True(yield.Collected);
        Assert.Equal("ore", yield.Collects);
        Assert.Equal(41, yield.UnitsPerCycle);
        Assert.Equal(8.0, yield.CycleSeconds);
        Assert.Equal(18450.0, yield.UnitsPerHour, 3);
        Assert.Equal(2778.0, yield.CyclesToDeplete);
        Assert.Equal(2000.0, yield.HeadCyclesLeft);
    }

    [Fact]
    public void AnIceHeadAtAnOreSiteGetsNothingButStillEatsTheSite()
    {
        MachineYield yield = MiningYields.Miner(Miner(Head("Mining-Drill Head (Ice)", 1f, 0f, 1.2f)), OreSite(), Tick, 1);

        Assert.False(yield.Collected);
        Assert.Equal(0, yield.UnitsPerCycle);
        Assert.Contains(yield.Problems, problem => problem.Contains("an ice head at an ore site") &&
                                                   problem.Contains("still depletes the site and wears the head"));
        Assert.Equal(2778.0, yield.CyclesToDeplete);
    }

    [Fact]
    public void AnOreHeadAtAnIceSiteGetsNoIce()
    {
        MachineYield ice = MiningYields.Miner(Miner(Head("High Speed Ice", 1.5f, 0f, 1.2f)), IceSite(), Tick, 1);
        MachineYield mineral = MiningYields.Miner(Miner(Head("Mineral", 1f, 1.2f, 0f)), IceSite(), Tick, 1);

        // 136.86 -> 137 x1.2 = 164.4 -> 164; speed 1.5: 8 / 0.75 = 10.67 -> 11 ticks = 5.5 s.
        Assert.Equal(164, ice.UnitsPerCycle);
        Assert.Equal(5.5, ice.CycleSeconds);
        Assert.Equal(0, mineral.UnitsPerCycle);
        Assert.Contains(mineral.Problems, problem => problem.Contains("an ore head at an ice site"));
    }

    [Fact]
    public void MinersSkipGasAndCollectorsGetOnlyGas()
    {
        MachineYield miner = MiningYields.Miner(Miner(Head("Basic", 1f, 1f, 1f)), GasSite(), Tick, 1);
        MachineYield collector = MiningYields.Collector(new CollectorReading("Rocket Gas Collector", true, true), GasSite(), Tick);
        MachineYield atOre = MiningYields.Collector(new CollectorReading("Rocket Gas Collector", true, true), OreSite(), Tick);

        Assert.False(miner.Collected);
        Assert.Contains(miner.Problems, problem => problem.Contains("miners skip gas deposits"));
        Assert.True(collector.Collected);
        Assert.Equal(35, collector.UnitsPerCycle);
        Assert.Null(collector.CyclesToDeplete);
        Assert.False(atOre.Collected);
    }

    [Fact]
    public void NoHeadOrNoHoldStopsTheMiner()
    {
        Assert.Contains(MiningYields.Miner(Miner(null), OreSite(), Tick, 1).Problems, p => p.Contains("No drill head"));
        Assert.Contains(MiningYields.Miner(Miner(Head("Basic", 1f, 1f, 1f), hold: false), OreSite(), Tick, 1).Problems,
            p => p.Contains("no cargo hold"));
        Assert.Contains(MiningYields.Collector(new CollectorReading("C", true, false), GasSite(), Tick).Problems,
            p => p.Contains("No pipe network"));
    }

    [Fact]
    public void ADepositListingOreAndIceDigsOreOnly()
    {
        // SpawnRatioSum counts only the Ores and ReagentMixes weights (MineableDeposit.cs:276-290): the pick never
        // reaches the frozen gas.
        DepositReading mixed = new DepositReading(DepositReading.Ore | DepositReading.Ice, 5f, 4f, 5f, 0f, 0.0, 1.0, true, false);

        Assert.Equal(MinedKind.Ore, mixed.Kind);
        Assert.Equal(1f, mixed.TypeMultiplier);
        Assert.Equal(MinedKind.Ice, IceSite().Kind);
    }

    [Fact]
    public void TwoMinersDepleteTheSiteTwiceAsFast()
    {
        MachineYield one = MiningYields.Miner(Miner(Head("Basic", 1f, 1f, 1f)), OreSite(), Tick, 1);
        MachineYield two = MiningYields.Miner(Miner(Head("Basic", 1f, 1f, 1f)), OreSite(), Tick, 2);

        Assert.Equal(one.CyclesToDeplete!.Value / 2.0, two.CyclesToDeplete!.Value, 6);
        Assert.True(one.TotalUnitsLeft > 0.0);
    }

    // ---- replies ----

    [Fact]
    public void ASiteIsCollectableOnlyWhenAMachineBringsSomethingHome()
    {
        DepositReading ore = OreSite();
        MachineYieldView ice = new MachineYieldView(
            MiningYields.Miner(Miner(Head("Ice", 1f, 0f, 1.2f)), ore, Tick, 2), 50);
        MachineYieldView mineral = new MachineYieldView(
            MiningYields.Miner(Miner(Head("Mineral", 1f, 1.2f, 0f)), ore, Tick, 2), 50);
        SpaceNodeView node = new SpaceNodeView(new ThingId(1), "Metallic Asteroid Field", "01", 1UL, "Generated", true);

        MiningSiteView both = new MiningSiteView(node, null, "ore", ore, 50, new List<SiteMaterialView>(),
            new List<MachineYieldView> { ice, mineral }, new List<string>());
        MiningSiteView iceOnly = new MiningSiteView(node, null, "ore", ore, 50, new List<SiteMaterialView>(),
            new List<MachineYieldView> { ice }, new List<string>());
        RocketMiningOptionsView options = new RocketMiningOptionsView(new ThingId(2), "Ore rocket",
            new MiningLoadoutView(new List<MinerView>(), new List<CollectorView>(), new List<ScannerView>(),
                new CargoSummaryView(1, 100, 0), 0.0, 0.0, new List<string> { "ore" }),
            new List<MiningSiteView> { both, iceOnly }, new List<string>());

        Assert.True(both.Collectable);
        Assert.Equal(18450.0, both.UnitsPerHour);
        Assert.Equal(369.0, mineral.SlotsPerHour);
        Assert.False(iceOnly.Collectable);
        Assert.Single(options.Collectable);
        Assert.Equal("Metallic Asteroid Field (ore)", options.NotCollectable[0]);
    }

    [Fact]
    public void CompactStatusLeavesTheLongListsOut()
    {
        string compact = JsonConvert.SerializeObject(new RocketMassView(2000.0, 1800.0, 200.0, 5.0, null, 200.0),
            ApiJson.Settings);
        string full = JsonConvert.SerializeObject(new RocketMassView(2000.0, 1800.0, 200.0, 5.0,
            new List<MassPartView> { new MassPartView("Fuselage", 2, 500.0) }, 200.0), ApiJson.Settings);

        Assert.DoesNotContain("parts", compact);
        Assert.Contains("\"payload_kg\":200", compact);
        Assert.Contains("parts", full);
    }

    [Fact]
    public void APartPoseSaysWhereItSitsFromTheEngineMount()
    {
        PartPoseView pose = new PartPoseView(new ThingId(5), "StructureCapsuleTankGas", null, "internal", 0.5, 2.25, -0.5,
            OrientationView.Of(CubeRotation.Identity), new List<string> { "ItemRocketMiningDrillHead" });

        string json = JsonConvert.SerializeObject(pose, ApiJson.Settings);

        Assert.Contains("\"offset\":{\"x\":0.5,\"y\":2.25,\"z\":-0.5}", json);
        Assert.Contains("\"prefab\":\"StructureCapsuleTankGas\"", json);
        Assert.DoesNotContain("\"name\"", json);
    }
}

/// <summary>The Mk II of RocketForecastTests, shared.</summary>
internal static class RocketForecastTestsShared
{
    internal static RocketCraft MkII(double fuelMol, float dryKg = 1730f, double batteryJ = 3600000.0)
    {
        FuelBurn burn = new FuelBurn(16390f / 18f, 0.0167, 0.0167 * 7.25, 1.0, 437.0, 8.3144 * 437.0);
        FuelLine line = new FuelLine(120.0, fuelMol * 120.0 / 3120.0,
            new List<FuelTank> { new FuelTank(3000.0, fuelMol * 3000.0 / 3120.0, true) }, 1, burn);
        PowerBank power = new PowerBank(batteryJ, 3600000.0, 10.0, 200.0);
        return new RocketCraft(dryKg, new List<FuelLine> { line }, 15800f, 16390f, true, 100f, false, 0f, power);
    }
}
