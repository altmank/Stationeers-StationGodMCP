#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Pure.Rockets;

namespace StationGodMCP.Api.Shared.Game.Flight;

/// <summary>A fuel's make-up: moles of each single gas or liquid type (GasTypes.All order) and its temperature.</summary>
internal sealed class FuelSample
{
    internal FuelSample(double[] moles, double temperatureK)
    {
        Moles = moles;
        TemperatureK = temperatureK;
    }

    internal double[] Moles { get; }

    internal double TemperatureK { get; }

    internal double Total
    {
        get
        {
            double total = 0.0;
            for (int index = 0; index < Moles.Length; index++)
            {
                total += Moles[index];
            }

            return total;
        }
    }

    /// <summary>The make-up of an atmosphere as it stands (the main thread reads last tick's values).</summary>
    internal static FuelSample Of(Atmosphere atmosphere)
    {
        double[] moles = new double[GasTypes.All.Length];
        for (int index = 0; index < moles.Length; index++)
        {
            moles[index] = atmosphere.GasMixture.GetMoleValue(GasTypes.All[index]).Quantity.ToDouble();
        }

        return new FuelSample(moles, atmosphere.Temperature.ToDouble());
    }

    internal static bool IsLiquid(int index) =>
        Chemistry.MatterState(GasTypes.All[index]) == AtmosphereHelper.MatterState.Liquid;

    internal FuelSample WithTemperature(double kelvin) => new FuelSample(Moles, kelvin);

    /// <summary>Both together; the temperature weighted by moles.</summary>
    internal FuelSample Plus(FuelSample other)
    {
        double[] moles = new double[Moles.Length];
        for (int index = 0; index < moles.Length; index++)
        {
            moles[index] = Moles[index] + other.Moles[index];
        }

        double total = Total + other.Total;
        double kelvin = total > 0.0 ? (TemperatureK * Total + other.TemperatureK * other.Total) / total : TemperatureK;
        return new FuelSample(moles, kelvin);
    }

    /// <summary>Only the gas types, or only the liquid types.</summary>
    internal FuelSample Phase(bool liquid)
    {
        double[] moles = new double[Moles.Length];
        for (int index = 0; index < moles.Length; index++)
        {
            moles[index] = IsLiquid(index) == liquid ? Moles[index] : 0.0;
        }

        return new FuelSample(moles, TemperatureK);
    }

    /// <summary>The same total, shared out by the fractions given (gas index to fraction; they are normalised).</summary>
    internal FuelSample WithMix(double[] fractions)
    {
        double sum = 0.0;
        for (int index = 0; index < fractions.Length; index++)
        {
            sum += fractions[index];
        }

        double total = Total > 0.0 ? Total : 1.0;
        double[] moles = new double[Moles.Length];
        for (int index = 0; index < moles.Length; index++)
        {
            moles[index] = sum > 0.0 ? total * fractions[index] / sum : 0.0;
        }

        return new FuelSample(moles, TemperatureK);
    }

    /// <summary>
    /// The phases the forecast needs: kg per mole of the gas and of the liquid, litres per mole of the liquid
    /// (Chemistry.MolarVolumeLiquid by type, the mix GasMixture.GetMolarVolumeLiquids makes, GasMixture.cs:2155-2163),
    /// and R x T per gas mole.
    /// </summary>
    internal LinePhases Phases()
    {
        FuelSample gas = Phase(false);
        FuelSample liquid = Phase(true);
        double liquidMoles = liquid.Total;
        double litres = 0.0;
        for (int index = 0; index < Moles.Length; index++)
        {
            if (IsLiquid(index))
            {
                litres += liquid.Moles[index] * Chemistry.MolarVolumeLiquid(GasTypes.All[index]).ToDouble();
            }
        }

        return new LinePhases(gas.KgPerMol(), liquid.KgPerMol(), liquidMoles > 0.0 ? litres / liquidMoles : 0.0,
            TemperatureK);
    }

    /// <summary>The mass of a mole of this make-up (0 when empty).</summary>
    internal double KgPerMol()
    {
        double total = Total;
        return total > 0.0 ? MixtureOf(1.0).TotalMassGassesAndLiquidsGrams() / 1000.0 : 0.0;
    }

    /// <summary>This make-up scaled to this many moles, its energy set for the sample's temperature.</summary>
    internal GasMixture MixtureOf(double moles)
    {
        GasMixture mixture = GasMixtureHelper.Create();
        double total = Total;
        if (total > 0.0 && moles > 0.0)
        {
            for (int index = 0; index < Moles.Length; index++)
            {
                double share = Moles[index] / total * moles;
                if (share > 0.0)
                {
                    mixture.Add(new Mole(GasTypes.All[index], new MoleQuantity(share), MoleEnergy.Zero));
                }
            }
        }

        mixture.TotalEnergy = IdealGas.Energy(mixture.HeatCapacity, new TemperatureKelvin(TemperatureK));
        return mixture;
    }
}

/// <summary>
/// The engine's own combustion run on a copy of what it draws, as RocketEngineBase.CalculateMaxThrust does at prefab
/// load (RocketEngineBase.cs:264-286): a fresh Atmosphere (Mode Thing: no fire is registered, Atmosphere.cs:545), each
/// drawn part added at its line's temperature (energy set as the engines' PrepareThrustSimulation set it,
/// GovernedGasEngine.cs:37-47), Atmosphere.TryCombust(rate, force: true) at the engines' combustion rate
/// (CombustionRates: the game's 0.96, or Terraforming Reloaded's when it is loaded), then CombustEngine's numbers
/// (RocketEngineBase.cs:518-526): FlowRate = molar mass x gas moles / tick / 1000, Force = FlowRate x ExitVelocity
/// (the engine's own ExitVelocity, with its EngineEfficiency, 586-591). Nothing in the world is touched; new Moles are
/// not cached, so the main thread reads them live.
/// </summary>
internal static class ThrustProbe
{
    internal static ProbeResult Burn(RocketEngineBase engine, FuelSample fuel, double drawnMol) =>
        Burn(engine, new List<DrawnPart> { new DrawnPart(fuel, drawnMol) });

    internal static ProbeResult Burn(RocketEngineBase engine, List<DrawnPart> parts)
    {
        Atmosphere chamber = new Atmosphere
        {
            Volume = engine.InternalVolume,
            Mode = AtmosphereHelper.AtmosphereMode.Thing
        };
        double drawnMol = 0.0;
        double propellantKg = 0.0;
        double fuelMoles = 0.0;
        for (int index = 0; index < parts.Count; index++)
        {
            DrawnPart part = parts[index];
            if (part.Moles <= 0.0 || part.Fuel.Total <= 0.0)
            {
                continue;
            }

            GasMixture drawn = part.Fuel.MixtureOf(part.Moles);
            drawnMol += part.Moles;
            propellantKg += drawn.TotalMassGassesAndLiquidsGrams() / 1000.0;
            fuelMoles += drawn.TotalFuel.ToDouble() + drawn.TotalOxidiser.ToDouble() + drawn.TotalHypergolics.ToDouble();
            chamber.Add(drawn);
        }

        chamber.TryCombust(CombustionRates.Current().Rate, force: true);
        float exitVelocity = engine.ExitVelocity(chamber);
        float flowRate = chamber.GasMixture.MolarMassGassesGrams() * chamber.TotalMolesGases.ToFloat() /
                         GameManager.GameTickSpeedSeconds / 1000f;
        double exhaustKg = chamber.GasMixture.TotalMassGassesAndLiquidsGrams() / 1000.0;
        return new ProbeResult(flowRate * exitVelocity, exitVelocity, chamber.Temperature.ToDouble(),
            drawnMol > 0.0 ? propellantKg / drawnMol : 0.0, drawnMol > 0.0 ? exhaustKg / drawnMol : 0.0,
            drawnMol > 0.0 ? fuelMoles / drawnMol : 0.0);
    }

    /// <summary>The FuelBurn of one make-up: force per mole at an 18 mol draw.</summary>
    internal static FuelBurn BurnFor(RocketEngineBase engine, FuelSample fuel, double kpaLitresPerMol)
    {
        ProbeResult probe = Burn(engine, fuel, FuelLine.MaxMolesPerTick);
        return new FuelBurn((float)(probe.ForceN / FuelLine.MaxMolesPerTick), probe.PropellantKgPerMol,
            probe.ExhaustKgPerMol, probe.FuelShare, fuel.TemperatureK, kpaLitresPerMol);
    }
}

/// <summary>One part of a draw: a make-up and the moles of it.</summary>
internal readonly struct DrawnPart
{
    internal DrawnPart(FuelSample fuel, double moles)
    {
        Fuel = fuel;
        Moles = moles;
    }

    internal FuelSample Fuel { get; }

    internal double Moles { get; }
}

/// <summary>
/// An engine's thrust for any draw from its lines: the draw's shares of input 1 gas, input 1 liquid, input 2 gas and
/// input 2 liquid (each phase keeps its make-up as the lines drain) are rounded to 1/200 and the game's combustion is
/// run once per distinct share on an 18 mol draw; thrust and exhaust then scale with the moles drawn (combustion,
/// exit velocity and molar mass do not depend on the amount). Built on the main thread and only used there.
/// </summary>
internal sealed class ProbeThrust : IThrustModel
{
    private const int Steps = 200;

    private readonly RocketEngineBase _engine;
    private readonly FuelSample[] _phases;
    private readonly Dictionary<long, ThrustReading> _perMole = new Dictionary<long, ThrustReading>(8);

    internal ProbeThrust(RocketEngineBase engine, FuelSample input1, FuelSample? input2)
    {
        _engine = engine;
        _phases = new[]
        {
            input1.Phase(false), input1.Phase(true), input2 != null ? input2.Phase(false) : Empty(input1),
            input2 != null ? input2.Phase(true) : Empty(input1)
        };
    }

    /// <summary>How many distinct mixes were burned (each one game combustion).</summary>
    internal int Probes => _perMole.Count;

    public ThrustReading Burn(EngineDraw draw)
    {
        double total = draw.Total;
        if (total <= 0.0)
        {
            return ThrustReading.None;
        }

        int gas1 = StepOf(draw.Input1.Gas, total);
        int liquid1 = StepOf(draw.Input1.Liquid, total);
        int gas2 = StepOf(draw.Input2.Gas, total);
        int liquid2 = StepOf(draw.Input2.Liquid, total);
        long key = ((gas1 * (long)(Steps + 1) + liquid1) * (Steps + 1) + gas2) * (Steps + 1) + liquid2;
        if (!_perMole.TryGetValue(key, out ThrustReading perMole))
        {
            int sum = gas1 + liquid1 + gas2 + liquid2;
            List<DrawnPart> parts = new List<DrawnPart>(4);
            AddPart(parts, 0, gas1, sum);
            AddPart(parts, 1, liquid1, sum);
            AddPart(parts, 2, gas2, sum);
            AddPart(parts, 3, liquid2, sum);
            ProbeResult probe = ThrustProbe.Burn(_engine, parts);
            perMole = new ThrustReading(probe.ForceN / FuelLine.MaxMolesPerTick, probe.ExhaustKgPerMol);
            _perMole[key] = perMole;
        }

        return new ThrustReading(perMole.ForceN * total, perMole.ExhaustKg * total);
    }

    private static int StepOf(double moles, double total) => (int)Math.Round(moles / total * Steps);

    private void AddPart(List<DrawnPart> parts, int phase, int steps, int sum)
    {
        if (steps > 0 && sum > 0)
        {
            parts.Add(new DrawnPart(_phases[phase], FuelLine.MaxMolesPerTick * steps / sum));
        }
    }

    private static FuelSample Empty(FuelSample like) => new FuelSample(new double[like.Moles.Length], like.TemperatureK);
}

internal readonly struct ProbeResult
{
    internal ProbeResult(double forceN, double exitVelocity, double exhaustK, double propellantKgPerMol,
        double exhaustKgPerMol, double fuelShare)
    {
        ForceN = forceN;
        ExitVelocity = exitVelocity;
        ExhaustK = exhaustK;
        PropellantKgPerMol = propellantKgPerMol;
        ExhaustKgPerMol = exhaustKgPerMol;
        FuelShare = fuelShare;
    }

    internal double ForceN { get; }

    internal double ExitVelocity { get; }

    internal double ExhaustK { get; }

    internal double PropellantKgPerMol { get; }

    internal double ExhaustKgPerMol { get; }

    internal double FuelShare { get; }
}
