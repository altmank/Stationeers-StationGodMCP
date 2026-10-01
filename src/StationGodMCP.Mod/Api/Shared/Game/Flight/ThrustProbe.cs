#nullable enable

using System;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Pure.Rockets;

namespace StationGodMCP.Api.Shared.Game.Flight;

/// <summary>A fuel's make-up: moles of each single gas type (GasTypes.All order) and its temperature.</summary>
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

    internal FuelSample WithTemperature(double kelvin) => new FuelSample(Moles, kelvin);

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
}

/// <summary>
/// The engine's own combustion run on a copy of the fuel, as RocketEngineBase.CalculateMaxThrust does at prefab load
/// (RocketEngineBase.cs:264-286): a fresh Atmosphere (Mode Thing: no fire is registered, Atmosphere.cs:545), the
/// drawn moles added at the fuel's temperature (energy set as GovernedGasEngine.PrepareThrustSimulation sets it,
/// GovernedGasEngine.cs:37-47), Atmosphere.TryCombust(rate, force: true) at the engines' combustion rate
/// (CombustionRates: the game's 0.96, or Terraforming Reloaded's when it is loaded), then CombustEngine's numbers
/// (RocketEngineBase.cs:518-526): FlowRate = molar mass x gas moles / tick / 1000, Force = FlowRate x ExitVelocity
/// (the engine's own ExitVelocity, 586-591). Nothing in the world is touched; new Moles are not cached, so the main
/// thread reads them live.
/// </summary>
internal static class ThrustProbe
{
    internal static ProbeResult Burn(RocketEngineBase engine, FuelSample fuel, double drawnMol)
    {
        Atmosphere chamber = new Atmosphere
        {
            Volume = engine.InternalVolume,
            Mode = AtmosphereHelper.AtmosphereMode.Thing
        };
        double total = fuel.Total;
        GasMixture drawn = GasMixtureHelper.Create();
        if (total > 0.0)
        {
            for (int index = 0; index < fuel.Moles.Length; index++)
            {
                double share = fuel.Moles[index] / total * drawnMol;
                if (share > 0.0)
                {
                    drawn.Add(new Mole(GasTypes.All[index], new MoleQuantity(share), MoleEnergy.Zero));
                }
            }
        }

        drawn.TotalEnergy = IdealGas.Energy(drawn.HeatCapacity, new TemperatureKelvin(fuel.TemperatureK));
        double propellantKg = drawn.TotalMassGassesAndLiquidsGrams() / 1000.0;
        double fuelMoles = drawn.TotalFuel.ToDouble() + drawn.TotalOxidiser.ToDouble() +
                           drawn.TotalHypergolics.ToDouble();
        chamber.Add(drawn);
        chamber.TryCombust(CombustionRates.Current().Rate, force: true);
        float exitVelocity = engine.ExitVelocity(chamber);
        float flowRate = chamber.GasMixture.MolarMassGassesGrams() * chamber.TotalMolesGases.ToFloat() /
                         GameManager.GameTickSpeedSeconds / 1000f;
        double exhaustKg = chamber.GasMixture.TotalMassGassesAndLiquidsGrams() / 1000.0;
        return new ProbeResult(flowRate * exitVelocity, exitVelocity, chamber.Temperature.ToDouble(),
            drawnMol > 0.0 ? propellantKg / drawnMol : 0.0, drawnMol > 0.0 ? exhaustKg / drawnMol : 0.0,
            drawnMol > 0.0 ? fuelMoles / drawnMol : 0.0);
    }

    /// <summary>The FuelBurn the simulator needs: force per mole at a full-throttle draw of the line's engines.</summary>
    internal static FuelBurn BurnFor(RocketEngineBase engine, FuelSample fuel, double kpaLitresPerMol)
    {
        ProbeResult probe = Burn(engine, fuel, FuelLine.MaxMolesPerTick);
        return new FuelBurn((float)(probe.ForceN / FuelLine.MaxMolesPerTick), probe.PropellantKgPerMol,
            probe.ExhaustKgPerMol, probe.FuelShare, fuel.TemperatureK, kpaLitresPerMol);
    }
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
