#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure.Rockets;

/// <summary>
/// One tank on an engine's fuel line: a Tank (DeviceInternal) or a GasTankStorage canister, by its volume and the moles
/// it holds. Each atmospheric tick it mixes with the pipe network by volume (DeviceInternal.OnAtmosphericTick ->
/// AtmosphereHelper.Mix, DeviceInternal.cs:48-55, AtmosphereHelper.cs Mix; GasTankStorage.OnAtmosphericTick,
/// GasTankStorage.cs:108-143). Only an atmosphere the rocket network lists counts for its mass
/// (RocketNetwork.CalculateGasMass over RocketAtmospheres, RocketNetwork.cs:67-75, filled at 320-332): a Tank's
/// internal atmosphere does; a canister in a GasTankStorage slot does not.
/// </summary>
internal sealed class FuelTank
{
    internal FuelTank(double volumeL, double moles, bool countsForMass)
    {
        VolumeL = volumeL;
        Moles = moles;
        CountsForMass = countsForMass;
    }

    internal double VolumeL { get; }

    internal double Moles { get; set; }

    internal bool CountsForMass { get; }

    internal FuelTank Copy() => new FuelTank(VolumeL, Moles, CountsForMass);
}

/// <summary>
/// What one mole of this fuel does in the engine, measured with the game's own combustion on a copy of the fuel
/// (Atmosphere.TryCombust(0.96, force) then RocketEngineBase.ExitVelocity and CombustEngine's flow rate,
/// RocketEngineBase.cs:518-526, 586-591). Draining a tank leaves its make-up and temperature as they were
/// (GasMixture.Remove takes every gas in proportion), so these hold for the whole flight unless heat moves in or out.
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
/// One engine fuel line: the pipe network the engines draw from (RocketEngineBase._inputNetwork1), the tanks on it and
/// the engines that draw from it. Only the Pumped Gas Engine (GovernedGasEngine) is modelled: it takes
/// Lerp(0, 18, Throttle / 100) mol a tick from its input (GovernedGasEngine.cs:49-55), whatever the pressure.
/// </summary>
internal sealed class FuelLine
{
    /// <summary>GovernedGasEngine.MAX_MOLAR_INPUT (GovernedGasEngine.cs:10).</summary>
    internal const double MaxMolesPerTick = 18.0;

    internal FuelLine(double pipeVolumeL, double pipeMoles, List<FuelTank> tanks, int engines, FuelBurn burn)
    {
        PipeVolumeL = pipeVolumeL;
        PipeMoles = pipeMoles;
        Tanks = tanks;
        Engines = engines;
        Burn = burn;
    }

    internal double PipeVolumeL { get; }

    internal double PipeMoles { get; set; }

    internal List<FuelTank> Tanks { get; }

    internal int Engines { get; }

    internal FuelBurn Burn { get; set; }

    /// <summary>Moles the engines burnt last tick, still in their internal atmospheres as exhaust.</summary>
    internal double ExhaustMoles { get; set; }

    internal double Moles
    {
        get
        {
            double total = PipeMoles;
            for (int index = 0; index < Tanks.Count; index++)
            {
                total += Tanks[index].Moles;
            }

            return total;
        }
    }

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

    /// <summary>The kg RocketNetwork.CalculateGasMass counts for this line (the pipe network, listed tanks, engine exhaust).</summary>
    internal double CountedMassKg
    {
        get
        {
            double moles = PipeMoles;
            for (int index = 0; index < Tanks.Count; index++)
            {
                if (Tanks[index].CountsForMass)
                {
                    moles += Tanks[index].Moles;
                }
            }

            return moles * Burn.PropellantKgPerMol + ExhaustMoles * Burn.ExhaustKgPerMol;
        }
    }

    /// <summary>Pressure of the first tank (else the pipe), kPa.</summary>
    internal double PressureKpa =>
        Tanks.Count > 0
            ? Burn.KpaLitresPerMol * Tanks[0].Moles / Tanks[0].VolumeL
            : PipeVolumeL > 0.0 ? Burn.KpaLitresPerMol * PipeMoles / PipeVolumeL : 0.0;

    /// <summary>
    /// Every engine on the line draws in turn (GovernedGasEngine.MovePropellant, GovernedGasEngine.cs:49-55: GasMixture
    /// .Remove gives at most what the pipe holds). Returns the moles drawn; they become this tick's exhaust.
    /// </summary>
    internal double Draw(float throttle)
    {
        double demand = MaxMolesPerTick * throttle / 100.0;
        double drawn = 0.0;
        for (int engine = 0; engine < Engines; engine++)
        {
            double take = Math.Min(demand, PipeMoles);
            PipeMoles -= take;
            drawn += take;
        }

        ExhaustMoles = drawn;
        return drawn;
    }

    /// <summary>Each tank mixes with the pipe by volume, one after another (AtmosphereHelper.Mix).</summary>
    internal void Mix()
    {
        for (int index = 0; index < Tanks.Count; index++)
        {
            FuelTank tank = Tanks[index];
            double moles = tank.Moles + PipeMoles;
            double volume = tank.VolumeL + PipeVolumeL;
            if (volume <= 0.0)
            {
                continue;
            }

            tank.Moles = moles * tank.VolumeL / volume;
            PipeMoles = moles * PipeVolumeL / volume;
        }
    }

    /// <summary>Scales every tank and the pipe to hold this many moles in all, in the proportions they hold now (by volume when empty).</summary>
    internal void SetMoles(double moles)
    {
        double now = Moles;
        double volume = VolumeL;
        PipeMoles = now > 0.0 ? PipeMoles / now * moles : volume > 0.0 ? moles * PipeVolumeL / volume : moles;
        for (int index = 0; index < Tanks.Count; index++)
        {
            FuelTank tank = Tanks[index];
            tank.Moles = now > 0.0 ? tank.Moles / now * moles : volume > 0.0 ? moles * tank.VolumeL / volume : 0.0;
        }
    }

    internal FuelLine Copy()
    {
        List<FuelTank> tanks = new List<FuelTank>(Tanks.Count);
        for (int index = 0; index < Tanks.Count; index++)
        {
            tanks.Add(Tanks[index].Copy());
        }

        return new FuelLine(PipeVolumeL, PipeMoles, tanks, Engines, Burn) { ExhaustMoles = ExhaustMoles };
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
        Powered = ChargeJ >= load;
        ChargeJ = Math.Max(0.0, ChargeJ - load);
    }

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
/// fuel lines, engine state and power. Mutable: the simulator flies a copy.
/// </summary>
internal sealed class RocketCraft
{
    internal RocketCraft(float structureMassKg, List<FuelLine> lines, float engineMaxThrust, float maxRecordedThrust,
        bool automatedLanding, float throttle, bool enginesOn, float force, PowerBank power)
    {
        StructureMassKg = structureMassKg;
        Lines = lines;
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

    /// <summary>The first engine's MaxThrust: its prefab thrust at 215 K (RocketEngineBase.CalculateMaxThrust, 264-286).</summary>
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
    internal float MaxExpectedThrust => Lines.Count == 0 ? 0f : Math.Max(MaxRecordedThrust, EngineMaxThrust);

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

    /// <summary>Moles a full-throttle tick of every engine takes.</summary>
    internal double FullThrottleMolesPerTick
    {
        get
        {
            double total = 0.0;
            for (int index = 0; index < Lines.Count; index++)
            {
                total += Lines[index].Engines * FuelLine.MaxMolesPerTick;
            }

            return total;
        }
    }

    internal float CountedGasKg()
    {
        double total = 0.0;
        for (int index = 0; index < Lines.Count; index++)
        {
            total += Lines[index].CountedMassKg;
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

    internal RocketCraft Copy()
    {
        List<FuelLine> lines = new List<FuelLine>(Lines.Count);
        for (int index = 0; index < Lines.Count; index++)
        {
            lines.Add(Lines[index].Copy());
        }

        return new RocketCraft(StructureMassKg, lines, EngineMaxThrust, MaxRecordedThrust, AutomatedLanding, Throttle,
            EnginesOn, Force, Power.Copy())
        {
            HighestRecordedThrust = HighestRecordedThrust,
            ThrustScale = ThrustScale,
            GasMassKg = GasMassKg
        };
    }
}
