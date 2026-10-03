#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure.Rockets;

/// <summary>What one engine took from its inputs in one tick, by input and phase.</summary>
internal readonly struct EngineDraw
{
    internal static readonly EngineDraw None = new EngineDraw(PhaseMoles.None, PhaseMoles.None);

    internal EngineDraw(PhaseMoles input1, PhaseMoles input2)
    {
        Input1 = input1;
        Input2 = input2;
    }

    internal PhaseMoles Input1 { get; }

    internal PhaseMoles Input2 { get; }

    internal double Total => Input1.Total + Input2.Total;
}

/// <summary>The engine's Force for one tick's draw, and the mass its chamber holds until the next tick's exhaust.</summary>
internal readonly struct ThrustReading
{
    internal static readonly ThrustReading None = new ThrustReading(0.0, 0.0);

    internal ThrustReading(double forceN, double exhaustKg)
    {
        ForceN = forceN;
        ExhaustKg = exhaustKg;
    }

    internal double ForceN { get; }

    internal double ExhaustKg { get; }
}

/// <summary>
/// How much thrust a draw gives. In the game this is the engine's own combustion run on a copy of what was drawn
/// (Game/Flight/ThrustProbe); here it is whatever the caller supplies.
/// </summary>
internal interface IThrustModel
{
    ThrustReading Burn(EngineDraw draw);
}

/// <summary>A draw of one make-up all flight: force and exhaust per mole, from one probe (FuelBurn).</summary>
internal sealed class ConstantThrust : IThrustModel
{
    private readonly FuelBurn _burn;

    internal ConstantThrust(FuelBurn burn)
    {
        _burn = burn;
    }

    public ThrustReading Burn(EngineDraw draw) =>
        new ThrustReading(_burn.ForcePerMolN * draw.Total, _burn.ExhaustKgPerMol * draw.Total);
}

/// <summary>A thrust model given as a function (tests, and the game side's cached probe).</summary>
internal sealed class ThrustFunction : IThrustModel
{
    private readonly Func<EngineDraw, ThrustReading> _burn;

    internal ThrustFunction(Func<EngineDraw, ThrustReading> burn)
    {
        _burn = burn;
    }

    public ThrustReading Burn(EngineDraw draw) => draw.Total <= 0.0 ? ThrustReading.None : _burn(draw);
}

/// <summary>
/// An engine's feed law: how much it takes from its input pipe networks each tick it burns (RocketEngineBase
/// .OnPreAtmosphere calls MovePropellant while the engine is on and powered, RocketEngineBase.cs:436-459). One subclass
/// per law in the game; the engine's class decides which (EngineSpecs).
/// </summary>
internal abstract class EngineFeed
{
    /// <summary>How the feed takes propellant, in a sentence with the game's own numbers.</summary>
    internal abstract string Law { get; }

    /// <summary>Whether the engine is operable only with a valid second input network (its IsOperable).</summary>
    internal abstract bool NeedsInput2 { get; }

    /// <summary>Whether input 2 carries propellant (false: it is a heat exchanger or unused).</summary>
    internal virtual bool Input2IsPropellant => NeedsInput2;

    internal abstract EngineDraw Draw(float throttle, FuelLine input1, FuelLine? input2);

    /// <summary>Whether anything is left in the inputs that this law takes.</summary>
    internal abstract bool HasFuel(FuelLine input1, FuelLine? input2);
}

/// <summary>
/// The Pumped Gas Engine (GovernedGasEngine): Lerp(0, 18, Throttle / 100) mol a tick from input 1, gas and liquid
/// alike, whatever the pressure (GovernedGasEngine.cs:10, 49-55).
/// </summary>
internal sealed class PumpedGasFeed : EngineFeed
{
    internal const double MaxMolesPerTick = 18.0;

    internal static readonly PumpedGasFeed Instance = new PumpedGasFeed();

    private PumpedGasFeed()
    {
    }

    internal override string Law =>
        "Pumped: 18 x throttle/100 mol a tick from input 1, gas and liquid in proportion, whatever the pressure " +
        "(GovernedGasEngine.cs:49-55).";

    internal override bool NeedsInput2 => false;

    internal override EngineDraw Draw(float throttle, FuelLine input1, FuelLine? input2) =>
        new EngineDraw(input1.TakeAll(UnityFloat.Lerp(0f, (float)MaxMolesPerTick, throttle / 100f)), PhaseMoles.None);

    internal override bool HasFuel(FuelLine input1, FuelLine? input2) => input1.Moles > 1e-6;
}

/// <summary>
/// The pressure-fed engines' gas move (PressureFedEngine.MoveGas and LimitedMolesPerTick, PressureFedEngine.cs:9-33):
/// at the input's gas pressure p, r = clamp(p / pipe max, 0, 1), the most it moves a tick is MAXPressurePerTick x
/// (3 - 2.999 / (1 + (r / 2)^0.7)); the pressure moved is p mapped from 0..60,795 kPa onto PressurePerTick..that most;
/// the moles are that pressure over a 10 L pipe at the input's temperature (Chemistry.PipeVolume), times throttle/100,
/// taken from every gas and liquid in proportion.
/// </summary>
internal abstract class PressureFedFeed : EngineFeed
{
    /// <summary>Chemistry.Limits.MAXPressureGasPipe (Chemistry.cs:19).</summary>
    internal const double GasPipeMaxKpa = 60794.99816894531;

    /// <summary>Chemistry.Limits.MAXPressureLiquidPipe (Chemistry.cs:21).</summary>
    internal const double LiquidPipeMaxKpa = 6079.499816894531;

    /// <summary>Chemistry.PipeVolume (Chemistry.cs:177).</summary>
    internal const double PipeVolumeL = 10.0;

    protected PressureFedFeed(double maxPressurePerTickKpa, double pressurePerTickKpa)
    {
        MaxPressurePerTickKpa = maxPressurePerTickKpa;
        PressurePerTickKpa = pressurePerTickKpa;
    }

    /// <summary>The engine class's MAXPressurePerTick: 5,000 kPa, 8,500 for the heavy gas engine, 6,000 for the heavy liquid one.</summary>
    internal double MaxPressurePerTickKpa { get; }

    /// <summary>The engine's PressurePerTick (DeviceAtmospherics.pressurePerTick, a prefab value; 101.325 in code).</summary>
    internal double PressurePerTickKpa { get; }

    /// <summary>The moles MoveGas takes at this gas pressure and temperature, at full throttle.</summary>
    internal double MolesPerTick(double pressureKpa, double temperatureK, double pipeMaxKpa)
    {
        float ratio = (float)UnityFloat.Clamp((float)(pressureKpa / pipeMaxKpa), 0f, 1f);
        float factor = 3f + -2.999f / (1f + (float)Math.Pow(ratio / 2f, 0.7f));
        double most = MaxPressurePerTickKpa * factor;
        double moved = pressureKpa * (most - PressurePerTickKpa) / GasPipeMaxKpa + PressurePerTickKpa;
        return temperatureK > 0.0 ? moved * PipeVolumeL / (temperatureK * LinePhases.GasConstant) : 0.0;
    }

    protected PhaseMoles MoveGas(FuelLine input, float throttle, double pipeMaxKpa)
    {
        double moles = MolesPerTick(input.PipeGasPressureKpa, input.Phases.TemperatureK, pipeMaxKpa) * (throttle / 100f);
        return moles > 0.0 ? input.TakeAll(moles) : PhaseMoles.None;
    }
}

/// <summary>
/// The Pressure Fed Gas Engine and its heavy variant: MoveGas on input 1 and on input 2, each by its own pressure
/// (PressureFedGasEngine.MovePropellant, PressureFedGasEngine.cs:46-51); operable only with both inputs (:20).
/// </summary>
internal sealed class PressureFedGasFeed : PressureFedFeed
{
    internal PressureFedGasFeed(double maxPressurePerTickKpa, double pressurePerTickKpa)
        : base(maxPressurePerTickKpa, pressurePerTickKpa)
    {
    }

    internal override string Law =>
        $"Pressure fed: each input gives moles by its own gas pressure, up to {MaxPressurePerTickKpa:0} kPa x " +
        "(3 - 2.999/(1 + (p/60,795/2)^0.7)) over a 10 L pipe, times throttle; both inputs needed " +
        "(PressureFedEngine.cs:9-33, PressureFedGasEngine.cs:46-51).";

    internal override bool NeedsInput2 => true;

    internal override EngineDraw Draw(float throttle, FuelLine input1, FuelLine? input2) =>
        new EngineDraw(MoveGas(input1, throttle, GasPipeMaxKpa),
            input2 != null ? MoveGas(input2, throttle, GasPipeMaxKpa) : PhaseMoles.None);

    internal override bool HasFuel(FuelLine input1, FuelLine? input2) =>
        input1.Moles > 1e-6 || (input2 != null && input2.Moles > 1e-6);
}

/// <summary>
/// The Pumped Liquid Engine: 0.55 L a tick in all at full throttle, input 1 giving Setting % and input 2 the rest,
/// liquid only (PumpedLiquidEngine.cs:9-13, 54-62, MoveLiquidVolume); operable only with both inputs (:25).
/// </summary>
internal sealed class PumpedLiquidFeed : EngineFeed
{
    /// <summary>PumpedLiquidEngine.MAXFuelFlow (PumpedLiquidEngine.cs:9).</summary>
    internal const double MaxLitresPerTick = 0.550000011920929;

    internal PumpedLiquidFeed(float setting)
    {
        Setting = Math.Max(0f, Math.Min(100f, setting));
    }

    /// <summary>OutputSetting: input 1's share in percent (Ratio1; input 2 gets 100 - Setting).</summary>
    internal float Setting { get; }

    internal override string Law =>
        $"Pumped liquid: 0.55 L a tick x throttle/100, {Setting:0.#} % from input 1 and {100f - Setting:0.#} % from " +
        "input 2 (Setting), liquid only; gas in the lines stays (PumpedLiquidEngine.cs:54-62).";

    internal override bool NeedsInput2 => true;

    internal override EngineDraw Draw(float throttle, FuelLine input1, FuelLine? input2)
    {
        float share = throttle / 100f;
        double litres1 = MaxLitresPerTick * (share * (Setting / 100f));
        double litres2 = MaxLitresPerTick * (share * ((100f - Setting) / 100f));
        return new EngineDraw(new PhaseMoles(0.0, input1.TakeLiquidLitres(litres1)),
            new PhaseMoles(0.0, input2 != null ? input2.TakeLiquidLitres(litres2) : 0.0));
    }

    internal override bool HasFuel(FuelLine input1, FuelLine? input2) =>
        (Setting > 0f && input1.LiquidMoles > 1e-6) || (Setting < 100f && input2 != null && input2.LiquidMoles > 1e-6);
}

/// <summary>
/// The Pressure Fed Liquid Engine and its heavy variant: liquid by volume, FlowRateMin..FlowRateMax litres mapped from
/// input 1's gas pressure over 0..6,079.5 kPa and clamped, times throttle; then MoveGas on input 1 with the liquid
/// pipe's limit (PressureFedLiquidEngine.cs:59-70). Input 2 is a heat exchanger with the engine's chamber after the
/// burn (OnPreAtmosphere then HandleHeatExchange, :72-88): it never changes that tick's thrust and is not a propellant input.
/// </summary>
internal sealed class PressureFedLiquidFeed : PressureFedFeed
{
    internal PressureFedLiquidFeed(double flowMinL, double flowMaxL, double maxPressurePerTickKpa,
        double pressurePerTickKpa)
        : base(maxPressurePerTickKpa, pressurePerTickKpa)
    {
        FlowMinL = flowMinL;
        FlowMaxL = flowMaxL;
    }

    internal double FlowMinL { get; }

    internal double FlowMaxL { get; }

    internal override string Law =>
        $"Pressure fed liquid: {FlowMinL:0.##}-{FlowMaxL:0.##} L of liquid a tick, scaled by input 1's gas pressure " +
        "over 0-6,079.5 kPa, times throttle, plus a pressure-fed gas draw from the same line; input 2 is a heat " +
        "exchanger only (PressureFedLiquidEngine.cs:59-88).";

    internal override bool NeedsInput2 => false;

    internal override bool Input2IsPropellant => false;

    /// <summary>The litres of liquid a full-throttle tick takes at this gas pressure.</summary>
    internal double LitresPerTick(double gasPressureKpa)
    {
        float value = (float)((gasPressureKpa - 0.0) * (FlowMaxL - FlowMinL) / LiquidPipeMaxKpa + FlowMinL);
        return UnityFloat.Clamp(value, (float)FlowMinL, (float)FlowMaxL);
    }

    internal override EngineDraw Draw(float throttle, FuelLine input1, FuelLine? input2)
    {
        double litres = LitresPerTick(input1.PipeGasPressureKpa) * (throttle / 100f);
        if (!(litres > 0.0))
        {
            return EngineDraw.None;
        }

        double liquid = input1.TakeLiquidLitres(litres);
        PhaseMoles gas = MoveGas(input1, throttle, LiquidPipeMaxKpa);
        return new EngineDraw(new PhaseMoles(0.0, liquid).Plus(gas), PhaseMoles.None);
    }

    internal override bool HasFuel(FuelLine input1, FuelLine? input2) => input1.Moles > 1e-6;
}

/// <summary>An engine as the forecast flies it: its feed law, the lines it draws from, its thrust, its chamber's mass.</summary>
internal sealed class EngineUnit
{
    internal EngineUnit(string name, EngineFeed feed, int input1, int? input2, IThrustModel thrust)
    {
        Name = name;
        Feed = feed;
        Input1 = input1;
        Input2 = input2;
        Thrust = thrust;
    }

    internal string Name { get; }

    internal EngineFeed Feed { get; }

    /// <summary>Index of input 1's line in the craft's lines.</summary>
    internal int Input1 { get; }

    /// <summary>Index of input 2's line, when the engine has one connected.</summary>
    internal int? Input2 { get; }

    internal IThrustModel Thrust { get; }

    /// <summary>The engine's internal atmosphere after its last burn (counted for the rocket's gas mass until exhausted).</summary>
    internal double ExhaustKg { get; set; }

    /// <summary>What the engine drew last tick.</summary>
    internal EngineDraw LastDraw { get; private set; }

    /// <summary>One tick's burn: the feed takes from the lines, the thrust model says what it gives.</summary>
    internal double Burn(float throttle, List<FuelLine> lines)
    {
        FuelLine? second = Input2.HasValue ? lines[Input2.Value] : null;
        LastDraw = Feed.Draw(throttle, lines[Input1], second);
        ThrustReading reading = Thrust.Burn(LastDraw);
        ExhaustKg = reading.ExhaustKg;
        return reading.ForceN;
    }

    /// <summary>The engine off or unpowered: RocketEngineBase.ClearEngineValues, and the chamber is exhausted.</summary>
    internal void Idle()
    {
        LastDraw = EngineDraw.None;
        ExhaustKg = 0.0;
    }

    internal bool HasFuel(List<FuelLine> lines) =>
        Feed.HasFuel(lines[Input1], Input2.HasValue ? lines[Input2.Value] : null);

    internal EngineUnit Copy() =>
        new EngineUnit(Name, Feed, Input1, Input2, Thrust) { ExhaustKg = ExhaustKg, LastDraw = LastDraw };
}

/// <summary>
/// The six engines the game builds, by class name: the feed law and the class's constants (every number from the
/// decompile, file:line in each feed's summary). PressurePerTick and the Pumped Liquid Engine's Setting are read from
/// the engine itself and passed in.
/// </summary>
internal static class EngineSpecs
{
    /// <summary>The feed for an engine class, or null when the class is not one of the six.</summary>
    internal static EngineFeed? FeedOf(string className, double pressurePerTickKpa, float setting) =>
        className switch
        {
            "GovernedGasEngine" => PumpedGasFeed.Instance,
            // PressureFedEngine.MAXPressurePerTick 5,000 (PressureFedEngine.cs:9).
            "PressureFedGasEngine" => new PressureFedGasFeed(5000.0, pressurePerTickKpa),
            // PressureFedGasEngineHeavy.MAXPressurePerTick 8,500 (PressureFedGasEngineHeavy.cs:9).
            "PressureFedGasEngineHeavy" => new PressureFedGasFeed(8500.0, pressurePerTickKpa),
            "PumpedLiquidEngine" => new PumpedLiquidFeed(setting),
            // FlowRateMin 0.04, FlowRateMax 0.8, MAXPressurePerTick 5,000 (PressureFedLiquidEngine.cs:13-15; PressureFedEngine.cs:9).
            "PressureFedLiquidEngine" => new PressureFedLiquidFeed(0.04, 0.8, 5000.0, pressurePerTickKpa),
            // FlowRateMax 1.5, MAXPressurePerTick 6,000 (PressureFedLiquidEngineHeavy.cs:9-17).
            "PressureFedLiquidEngineHeavy" => new PressureFedLiquidFeed(0.04, 1.5, 6000.0, pressurePerTickKpa),
            _ => null
        };

    /// <summary>The player-facing name of an engine class.</summary>
    internal static string NameOf(string className) =>
        className switch
        {
            "GovernedGasEngine" => "Pumped Gas Engine",
            "PressureFedGasEngine" => "Pressure Fed Gas Engine",
            "PressureFedGasEngineHeavy" => "Heavy Pressure Fed Gas Engine",
            "PumpedLiquidEngine" => "Pumped Liquid Engine",
            "PressureFedLiquidEngine" => "Pressure Fed Liquid Engine",
            "PressureFedLiquidEngineHeavy" => "Heavy Pressure Fed Liquid Engine",
            _ => className
        };
}

/// <summary>
/// How long the fuel lasts at a throttle: the engines' feeds run tick by tick on a copy (tanks mixing after each draw)
/// until none can take anything or the limit is reached. No thrust is computed.
/// </summary>
internal static class BurnTime
{
    internal const int TickLimit = 200000;

    /// <summary>Seconds of burning (ticks x tick length), or null when nothing is drawn at all.</summary>
    internal static double? Seconds(RocketCraft rocket, float throttle, double tickSeconds)
    {
        if (rocket.Engines.Count == 0 || throttle <= 0f)
        {
            return null;
        }

        RocketCraft craft = rocket.Copy();
        int ticks = 0;
        for (; ticks < TickLimit; ticks++)
        {
            double drawn = 0.0;
            for (int index = 0; index < craft.Engines.Count; index++)
            {
                EngineUnit engine = craft.Engines[index];
                FuelLine? second = engine.Input2.HasValue ? craft.Lines[engine.Input2.Value] : null;
                drawn += engine.Feed.Draw(throttle, craft.Lines[engine.Input1], second).Total;
            }

            for (int index = 0; index < craft.Lines.Count; index++)
            {
                craft.Lines[index].Mix();
            }

            if (drawn <= 1e-9)
            {
                break;
            }
        }

        return ticks == 0 ? null : ticks * tickSeconds;
    }
}
