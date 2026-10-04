#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure.Rockets;

/// <summary>
/// One tank on a fuel line: a Tank (DeviceInternal) or a canister in a Gas/Liquid Tank Storage, by its volume and the
/// gas and liquid moles it holds. Each atmospheric tick it mixes with the pipe network by volume, gas and liquid alike
/// (Tank.MatterState is All, Tank.cs:23; DeviceInternal.OnAtmosphericTick -> AtmosphereHelper.Mix(..., All), which
/// shares everything out by each side's whole volume, DeviceInternal.cs:48-55, AtmosphereHelper.cs:70-87;
/// GasTankStorage.OnAtmosphericTick, GasTankStorage.cs:108-143). Only an atmosphere the rocket network lists counts for
/// its mass (RocketNetwork.CalculateGasMass over RocketAtmospheres, RocketNetwork.cs:67-75, filled at 320-332): a
/// Tank's internal atmosphere does; a canister in a tank storage slot does not.
/// </summary>
internal sealed class FuelTank
{
    internal FuelTank(double volumeL, double moles, bool countsForMass)
        : this(volumeL, moles, 0.0, countsForMass)
    {
    }

    internal FuelTank(double volumeL, double gasMoles, double liquidMoles, bool countsForMass)
    {
        VolumeL = volumeL;
        GasMoles = gasMoles;
        LiquidMoles = liquidMoles;
        CountsForMass = countsForMass;
    }

    internal double VolumeL { get; }

    internal double GasMoles { get; set; }

    internal double LiquidMoles { get; set; }

    internal double Moles => GasMoles + LiquidMoles;

    internal bool CountsForMass { get; }

    internal FuelTank Copy() => new FuelTank(VolumeL, GasMoles, LiquidMoles, CountsForMass);
}

/// <summary>
/// What one mole of this fuel does in the engine, measured with the game's own combustion on a copy of the fuel
/// (Atmosphere.TryCombust at the engines' combustion rate, CombustionRate: 0.96 or Terraforming Reloaded's, with force,
/// then RocketEngineBase.ExitVelocity and CombustEngine's flow rate,
/// RocketEngineBase.cs:518-526, 586-591). Draining a tank leaves its make-up and temperature as they were
/// (GasMixture.Remove takes every gas in proportion), so these hold for the whole flight unless heat moves in or out.
/// The thrust of a line whose draw always has the same make-up (one Pumped Gas Engine on one line).
/// </summary>
internal readonly struct FuelBurn
{
    internal FuelBurn(float forcePerMolN, double propellantKgPerMol, double exhaustKgPerMol, double fuelShare,
        double temperatureK, double kpaLitresPerMol)
    {
        ForcePerMolN = forcePerMolN;
        PropellantKgPerMol = propellantKgPerMol;
        ExhaustKgPerMol = exhaustKgPerMol;
        FuelShare = fuelShare;
        TemperatureK = temperatureK;
        KpaLitresPerMol = kpaLitresPerMol;
    }

    /// <summary>Engine Force for each mole drawn in a tick: CombustEngine's FlowRate x ExhaustVelocity over the moles.</summary>
    internal float ForcePerMolN { get; }

    /// <summary>Mass of a mole of the fuel as drawn.</summary>
    internal double PropellantKgPerMol { get; }

    /// <summary>Mass the engine holds after burning a mole (its internal atmosphere, counted until the next tick's exhaust).</summary>
    internal double ExhaustKgPerMol { get; }

    /// <summary>Share of the moles the game counts as fuel for its burn-time estimate (TotalFuel + TotalOxidiser + TotalHypergolics, Rocket.cs:2924-2966).</summary>
    internal double FuelShare { get; }

    internal double TemperatureK { get; }

    /// <summary>Pressure x volume per mole at this temperature (kPa L / mol): the tank's pressure is this x moles / volume.</summary>
    internal double KpaLitresPerMol { get; }
}

/// <summary>
/// The two phases of a fuel line as the forecast holds them: what a mole of its gas and of its liquid weighs, the
/// litres a mole of its liquid fills (GasMixture.GetMolarVolumeLiquids, used by AtmosphereHelper.RemoveLiquidVolume,
/// AtmosphereHelper.cs:352-357), and pressure x volume per gas mole at the line's temperature (IdealGas: n 8.3144 T / V,
/// PressurekPa.cs:100-103). Every removal the engines make takes each phase in proportion, so each phase keeps its
/// make-up; phase changes in flight (boiling, condensing) are not simulated.
/// </summary>
internal readonly struct LinePhases
{
    internal const double GasConstant = 8.3144;

    internal LinePhases(double gasKgPerMol, double liquidKgPerMol, double liquidLitresPerMol, double temperatureK)
        : this(gasKgPerMol, liquidKgPerMol, liquidLitresPerMol, temperatureK, GasConstant * temperatureK)
    {
    }

    internal LinePhases(double gasKgPerMol, double liquidKgPerMol, double liquidLitresPerMol, double temperatureK,
        double kpaLitresPerGasMol)
    {
        GasKgPerMol = gasKgPerMol;
        LiquidKgPerMol = liquidKgPerMol;
        LiquidLitresPerMol = liquidLitresPerMol;
        TemperatureK = temperatureK;
        KpaLitresPerGasMol = kpaLitresPerGasMol;
    }

    internal double GasKgPerMol { get; }

    internal double LiquidKgPerMol { get; }

    internal double LiquidLitresPerMol { get; }

    internal double TemperatureK { get; }

    internal double KpaLitresPerGasMol { get; }
}

/// <summary>Moles of gas and of liquid taken (or held) together.</summary>
internal readonly struct PhaseMoles
{
    internal static readonly PhaseMoles None = new PhaseMoles(0.0, 0.0);

    internal PhaseMoles(double gas, double liquid)
    {
        Gas = gas;
        Liquid = liquid;
    }

    internal double Gas { get; }

    internal double Liquid { get; }

    internal double Total => Gas + Liquid;

    internal PhaseMoles Plus(PhaseMoles other) => new PhaseMoles(Gas + other.Gas, Liquid + other.Liquid);
}

/// <summary>
/// One fuel line: a pipe network an engine input draws from (RocketEngineBase._inputNetwork1 or _inputNetwork2), with
/// the tanks on it. The engines draw from the pipe network's atmosphere; the tanks top it up as they mix with it.
/// </summary>
internal sealed class FuelLine
{
    /// <summary>GovernedGasEngine.MAX_MOLAR_INPUT (GovernedGasEngine.cs:10).</summary>
    internal const double MaxMolesPerTick = PumpedGasFeed.MaxMolesPerTick;

    /// <summary>Chemistry.MinimumGasVolume (Chemistry.cs:181): the gas volume never drops below it (Atmosphere.GetGasVolume, Atmosphere.cs:1515-1518).</summary>
    private const double MinimumGasVolumeL = 0.1;

    internal FuelLine(double pipeVolumeL, double pipeGasMoles, double pipeLiquidMoles, List<FuelTank> tanks,
        LinePhases phases)
    {
        PipeVolumeL = pipeVolumeL;
        PipeGasMoles = pipeGasMoles;
        PipeLiquidMoles = pipeLiquidMoles;
        Tanks = tanks;
        Phases = phases;
    }

    /// <summary>A gas line fed to engines whose draw has one make-up all flight (the Pumped Gas Engine).</summary>
    internal FuelLine(double pipeVolumeL, double pipeMoles, List<FuelTank> tanks, int engines, FuelBurn burn)
        : this(pipeVolumeL, pipeMoles, 0.0, tanks,
            new LinePhases(burn.PropellantKgPerMol, burn.PropellantKgPerMol, 0.0, burn.TemperatureK,
                burn.KpaLitresPerMol))
    {
        Engines = engines;
        Burn = burn;
    }

    private FuelLine(double pipeVolumeL, double pipeGasMoles, double pipeLiquidMoles, List<FuelTank> tanks,
        LinePhases phases, int engines, FuelBurn burn)
        : this(pipeVolumeL, pipeGasMoles, pipeLiquidMoles, tanks, phases)
    {
        Engines = engines;
        Burn = burn;
    }

    internal double PipeVolumeL { get; }

    internal double PipeGasMoles { get; set; }

    internal double PipeLiquidMoles { get; set; }

    /// <summary>Gas and liquid in the pipe together; setting it keeps their proportion.</summary>
    internal double PipeMoles
    {
        get => PipeGasMoles + PipeLiquidMoles;
        set
        {
            double now = PipeMoles;
            PipeGasMoles = now > 0.0 ? PipeGasMoles / now * value : value;
            PipeLiquidMoles = now > 0.0 ? PipeLiquidMoles / now * value : 0.0;
        }
    }

    internal List<FuelTank> Tanks { get; }

    internal LinePhases Phases { get; set; }

    /// <summary>Pumped Gas Engines built straight onto this line (the short form; RocketCraft turns them into engines).</summary>
    internal int Engines { get; }

    internal FuelBurn Burn { get; }

    internal double Moles => GasMoles + LiquidMoles;

    internal double GasMoles
    {
        get
        {
            double total = PipeGasMoles;
            for (int index = 0; index < Tanks.Count; index++)
            {
                total += Tanks[index].GasMoles;
            }

            return total;
        }
    }

    internal double LiquidMoles
    {
        get
        {
            double total = PipeLiquidMoles;
            for (int index = 0; index < Tanks.Count; index++)
            {
                total += Tanks[index].LiquidMoles;
            }

            return total;
        }
    }

    internal double LiquidLitres => LiquidMoles * Phases.LiquidLitresPerMol;

    internal double VolumeL
    {
        get
        {
            double total = PipeVolumeL;
            for (int index = 0; index < Tanks.Count; index++)
            {
                total += Tanks[index].VolumeL;
            }

            return total;
        }
    }

    /// <summary>The kg RocketNetwork.CalculateGasMass counts for this line (the pipe network and the tanks it lists).</summary>
    internal double CountedMassKg
    {
        get
        {
            double gas = PipeGasMoles;
            double liquid = PipeLiquidMoles;
            for (int index = 0; index < Tanks.Count; index++)
            {
                if (Tanks[index].CountsForMass)
                {
                    gas += Tanks[index].GasMoles;
                    liquid += Tanks[index].LiquidMoles;
                }
            }

            return gas * Phases.GasKgPerMol + liquid * Phases.LiquidKgPerMol;
        }
    }

    /// <summary>
    /// Atmosphere.PressureGasses of the pipe network, the pressure the pressure-fed engines read: gas moles x R x T over
    /// the gas volume (the volume less the liquid's, at least 0.1 L; Atmosphere.cs:693-703, 1515-1518).
    /// </summary>
    internal double PipeGasPressureKpa => GasPressure(PipeVolumeL, PipeGasMoles, PipeLiquidMoles);

    /// <summary>The gas pressure in the first tank (else the pipe), kPa.</summary>
    internal double PressureKpa =>
        Tanks.Count == 0
            ? PipeGasPressureKpa
            : GasPressure(Tanks[0].VolumeL, Tanks[0].GasMoles, Tanks[0].LiquidMoles);

    private double GasPressure(double volumeL, double gasMoles, double liquidMoles)
    {
        double gasVolume = Math.Max(volumeL - liquidMoles * Phases.LiquidLitresPerMol, MinimumGasVolumeL);
        return gasMoles * Phases.KpaLitresPerGasMol / gasVolume;
    }

    /// <summary>
    /// GasMixture.Remove(moles, All) on the pipe (GasMixture.cs:1693-1702): at most what the pipe holds, every gas and
    /// liquid in proportion.
    /// </summary>
    internal PhaseMoles TakeAll(double moles)
    {
        double held = PipeMoles;
        double take = Math.Min(Math.Max(0.0, moles), held);
        if (take <= 0.0)
        {
            return PhaseMoles.None;
        }

        double share = take / held;
        PhaseMoles taken = new PhaseMoles(PipeGasMoles * share, PipeLiquidMoles * share);
        PipeGasMoles -= taken.Gas;
        PipeLiquidMoles -= taken.Liquid;
        return taken;
    }

    /// <summary>
    /// AtmosphereHelper.RemoveLiquidVolume (AtmosphereHelper.cs:352-357): the litres over the liquids' molar volume, as
    /// moles of liquid, at most what the pipe holds (nothing when it holds no liquid).
    /// </summary>
    internal double TakeLiquidLitres(double litres)
    {
        if (Phases.LiquidLitresPerMol <= 0.0 || litres <= 0.0)
        {
            return 0.0;
        }

        double take = Math.Min(litres / Phases.LiquidLitresPerMol, PipeLiquidMoles);
        PipeLiquidMoles -= take;
        return take;
    }

    /// <summary>Each tank mixes with the pipe by volume, gas and liquid alike, one after another (AtmosphereHelper.Mix, All).</summary>
    internal void Mix()
    {
        for (int index = 0; index < Tanks.Count; index++)
        {
            FuelTank tank = Tanks[index];
            double volume = tank.VolumeL + PipeVolumeL;
            if (volume <= 0.0)
            {
                continue;
            }

            double gas = tank.GasMoles + PipeGasMoles;
            double liquid = tank.LiquidMoles + PipeLiquidMoles;
            tank.GasMoles = gas * tank.VolumeL / volume;
            tank.LiquidMoles = liquid * tank.VolumeL / volume;
            PipeGasMoles = gas * PipeVolumeL / volume;
            PipeLiquidMoles = liquid * PipeVolumeL / volume;
        }
    }

    /// <summary>
    /// The short form's draw: each of the line's Pumped Gas Engines takes Lerp(0, 18, throttle / 100) mol from the pipe
    /// in turn (GovernedGasEngine.MovePropellant, GovernedGasEngine.cs:49-55). Returns the moles drawn.
    /// </summary>
    internal double Draw(float throttle)
    {
        double drawn = 0.0;
        for (int engine = 0; engine < Engines; engine++)
        {
            drawn += PumpedGasFeed.Instance.Draw(throttle, this, null).Total;
        }

        return drawn;
    }

    /// <summary>Scales every tank and the pipe to hold this many moles in all, in the proportions they hold now (by volume, as gas, when empty).</summary>
    internal void SetMoles(double moles)
    {
        double now = Moles;
        double volume = VolumeL;
        double scale = now > 0.0 ? moles / now : 0.0;
        if (now > 0.0)
        {
            PipeGasMoles *= scale;
            PipeLiquidMoles *= scale;
        }
        else
        {
            PipeGasMoles = volume > 0.0 ? moles * PipeVolumeL / volume : moles;
        }

        for (int index = 0; index < Tanks.Count; index++)
        {
            FuelTank tank = Tanks[index];
            if (now > 0.0)
            {
                tank.GasMoles *= scale;
                tank.LiquidMoles *= scale;
            }
            else
            {
                tank.GasMoles = volume > 0.0 ? moles * tank.VolumeL / volume : 0.0;
            }
        }
    }

    internal FuelLine Copy()
    {
        List<FuelTank> tanks = new List<FuelTank>(Tanks.Count);
        for (int index = 0; index < Tanks.Count; index++)
        {
            tanks.Add(Tanks[index].Copy());
        }

        return new FuelLine(PipeVolumeL, PipeGasMoles, PipeLiquidMoles, tanks, Phases, Engines, Burn);
    }
}

/// <summary>
/// The rocket's power: the charge of its batteries and what the devices on their output networks draw. The game takes
/// each device's GetUsedPower off the batteries once per 0.5 s tick, as it stands (PowerTick.cs:88-91, 131-150;
/// Battery.UsePower, Battery.cs), so a "200 W" engine takes 200 J a tick, 400 J a second.
/// </summary>
internal sealed class PowerBank
{
    internal PowerBank(double chargeJ, double capacityJ, double otherLoadW, double engineLoadW)
    {
        ChargeJ = chargeJ;
        CapacityJ = capacityJ;
        OtherLoadW = otherLoadW;
        EngineLoadW = engineLoadW;
        Powered = chargeJ > 0.0;
    }

    internal double ChargeJ { get; set; }

    internal double CapacityJ { get; }

    /// <summary>Every switched-on device but the engines, per tick.</summary>
    internal double OtherLoadW { get; set; }

    /// <summary>The engines' UsedPower together, per tick, drawn while they are on.</summary>
    internal double EngineLoadW { get; }

    /// <summary>Whether the last power tick met the load; the engines burn only while powered (RocketEngineBase.cs:450-458).</summary>
    internal bool Powered { get; private set; }

    /// <summary>
    /// One power tick: the load comes off the charge; a charge that cannot cover it leaves the devices unpowered. A
    /// rocket without batteries has nothing to draw from once its power umbilical is off the tower.
    /// </summary>
    internal void Tick(bool enginesOn)
    {
        double load = OtherLoadW + (enginesOn ? EngineLoadW : 0.0);
        Powered = HasBattery && ChargeJ >= load;
        ChargeJ = Math.Max(0.0, ChargeJ - load);
    }

    /// <summary>Charge given by another rocket (positive) or to it (negative), kept within 0 and the capacity.</summary>
    internal void Transfer(double joules)
    {
        ChargeJ = Math.Min(CapacityJ, Math.Max(0.0, ChargeJ + joules));
        Powered = ChargeJ > 0.0;
    }

    /// <summary>
    /// Whether the rocket carries a battery at all: off the tower nothing else powers its engines, whatever their load
    /// reads.
    /// </summary>
    internal bool HasBattery => CapacityJ > 0.0;

    internal PowerBank Copy()
    {
        PowerBank copy = new PowerBank(ChargeJ, CapacityJ, OtherLoadW, EngineLoadW);
        copy.Powered = Powered;
        return copy;
    }
}

/// <summary>
/// A rocket as the forecast flies it: what RocketNetwork.CombinedMass counts (structure, which includes 1 kg per
/// filled cargo slot, plus the gas mass the last tick measured; RocketNetwork.cs:62-75, RocketChuteStorage.cs:30), its
/// fuel lines, its engines (each with its feed law and inputs), engine state and power. Mutable: the simulator flies a
/// copy.
/// </summary>
internal sealed class RocketCraft
{
    /// <summary>The short form: every line's own Pumped Gas Engines (FuelLine.Engines) burning its FuelBurn.</summary>
    internal RocketCraft(float structureMassKg, List<FuelLine> lines, float engineMaxThrust, float maxRecordedThrust,
        bool automatedLanding, float throttle, bool enginesOn, float force, PowerBank power)
        : this(structureMassKg, lines, EnginesOf(lines), engineMaxThrust, maxRecordedThrust, automatedLanding,
            throttle, enginesOn, force, power)
    {
    }

    internal RocketCraft(float structureMassKg, List<FuelLine> lines, List<EngineUnit> engines,
        float engineMaxThrust, float maxRecordedThrust, bool automatedLanding, float throttle, bool enginesOn,
        float force, PowerBank power)
    {
        StructureMassKg = structureMassKg;
        Lines = lines;
        Engines = engines;
        EngineMaxThrust = engineMaxThrust;
        MaxRecordedThrust = maxRecordedThrust;
        AutomatedLanding = automatedLanding;
        Throttle = throttle;
        EnginesOn = enginesOn;
        Force = force;
        Power = power;
        GasMassKg = CountedGasKg();
    }

    /// <summary>RocketNetwork.DryMass: every IRocketMassContributor (RocketNetwork.RecalculateStructureMass, 384-405).</summary>
    internal float StructureMassKg { get; set; }

    internal List<FuelLine> Lines { get; }

    internal List<EngineUnit> Engines { get; }

    /// <summary>The first engine's MaxThrust: its prefab thrust (RocketEngineBase.CalculateMaxThrust, 264-286).</summary>
    internal float EngineMaxThrust { get; }

    /// <summary>Rocket.MaxRecordedThrust: the highest GetThrust ever seen, raised every physics step (Rocket.cs:1988).</summary>
    internal float MaxRecordedThrust { get; set; }

    /// <summary>Rocket._highestRecordedThrust: the landing autopilot's own peak, reset as a landing starts (Rocket.cs:2534).</summary>
    internal float HighestRecordedThrust { get; set; }

    internal bool AutomatedLanding { get; }

    /// <summary>Every engine's Throttle, 0 to 100 (the autopilot sets them all alike).</summary>
    internal float Throttle { get; set; }

    internal bool EnginesOn { get; set; }

    /// <summary>Rocket.GetThrust: the engines' Force from the last tick.</summary>
    internal float Force { get; set; }

    /// <summary>What-if: the share of the thrust the engines really give (1 = as measured).</summary>
    internal float ThrustScale { get; set; } = 1f;

    internal PowerBank Power { get; }

    /// <summary>RocketNetwork._gasMasskg as the last atmospheric tick measured it.</summary>
    internal float GasMassKg { get; set; }

    /// <summary>Rocket.TotalMass.</summary>
    internal float MassKg => StructureMassKg + GasMassKg;

    /// <summary>Rocket.GetMaxExpectedThrust (Rocket.cs:2710-2721): the recorded peak or the first engine's prefab thrust.</summary>
    internal float MaxExpectedThrust => Engines.Count == 0 ? 0f : Math.Max(MaxRecordedThrust, EngineMaxThrust);

    internal double FuelMoles
    {
        get
        {
            double total = 0.0;
            for (int index = 0; index < Lines.Count; index++)
            {
                total += Lines[index].Moles;
            }

            return total;
        }
    }

    /// <summary>No engine has anything left its feed can take.</summary>
    internal bool OutOfFuel
    {
        get
        {
            for (int index = 0; index < Engines.Count; index++)
            {
                if (Engines[index].HasFuel(Lines))
                {
                    return false;
                }
            }

            return true;
        }
    }

    internal float CountedGasKg()
    {
        double total = 0.0;
        for (int index = 0; index < Lines.Count; index++)
        {
            total += Lines[index].CountedMassKg;
        }

        for (int index = 0; index < Engines.Count; index++)
        {
            total += Engines[index].ExhaustKg;
        }

        return (float)total;
    }

    /// <summary>Sets every line's moles in the proportion the lines hold now.</summary>
    internal void SetFuelMoles(double moles)
    {
        double now = FuelMoles;
        for (int index = 0; index < Lines.Count; index++)
        {
            FuelLine line = Lines[index];
            line.SetMoles(now > 0.0 ? line.Moles / now * moles : moles / Lines.Count);
        }

        GasMassKg = CountedGasKg();
    }

    /// <summary>Fuel received (positive) or given (negative) on one line, kept at 0 or more.</summary>
    internal void TransferFuel(int line, double moles)
    {
        FuelLine target = Lines[line];
        target.SetMoles(Math.Max(0.0, target.Moles + moles));
        GasMassKg = CountedGasKg();
    }

    internal RocketCraft Copy()
    {
        List<FuelLine> lines = new List<FuelLine>(Lines.Count);
        for (int index = 0; index < Lines.Count; index++)
        {
            lines.Add(Lines[index].Copy());
        }

        List<EngineUnit> engines = new List<EngineUnit>(Engines.Count);
        for (int index = 0; index < Engines.Count; index++)
        {
            engines.Add(Engines[index].Copy());
        }

        return new RocketCraft(StructureMassKg, lines, engines, EngineMaxThrust, MaxRecordedThrust, AutomatedLanding,
            Throttle, EnginesOn, Force, Power.Copy())
        {
            HighestRecordedThrust = HighestRecordedThrust,
            ThrustScale = ThrustScale,
            GasMassKg = GasMassKg
        };
    }

    private static List<EngineUnit> EnginesOf(List<FuelLine> lines)
    {
        List<EngineUnit> engines = new List<EngineUnit>(lines.Count);
        for (int index = 0; index < lines.Count; index++)
        {
            for (int engine = 0; engine < lines[index].Engines; engine++)
            {
                engines.Add(new EngineUnit("Pumped Gas Engine", PumpedGasFeed.Instance, index, null,
                    new ConstantThrust(lines[index].Burn)));
            }
        }

        return engines;
    }
}
