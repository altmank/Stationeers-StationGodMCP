#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using UnityEngine;
using Weather;

namespace StationGodMCP.Api;

/// <summary>
/// planet: the planet's own atmosphere, the one GlobalGasMix per world (PlanetaryAtmosphereSimulation.GetGlobalGasMix)
/// that every outdoor cell relaxes toward, its three reservoirs (PlanetaryAtmosphereSimulation._liquidClouds,
/// _iceClouds and _iceCaps, read through GameMembers) and its temperature. In the unmodded game the tank never changes
/// (PlanetaryAtmosphereSimulation.IsGlobalInteraction is false, so gas vented outdoors is discarded); with a
/// terraforming mod it does. Read only.
///
/// The temperature range is the game's own: GlobalGasMix.GetGlobalGasMixTemperature(data, sun angle, solar energy
/// percent) swept over every sun angle from 0 to 180, as ValueRange does for the new-game menu, on the planet's air as
/// it is now. Today's range uses the orbit's present solar energy; the orbit range adds both ends of the orbit (0 and
/// 100 %). A running storm and a terraforming mod's patch of the formula are in all of them.
///
/// The starting air is the world's own (GlobalAtmosphereData.GlobalGasMixData, moles per outdoor cell, which
/// GlobalGasMix.Create multiplies by the planet's cells) at the planet's present size. Terraforming Reloaded measures
/// "this world's starting air" the same way (per cell, so a resized planet starts with the same air per cell).
/// </summary>
internal static class PlanetApi
{
    private const int LastSunAngle = 180;
    private const float FarthestSolarEnergyPercent = 0f;
    private const float NearestSolarEnergyPercent = 100f;
    private const double TraceMoles = 1e-9;

    // Atmosphere.PartialPressureHumanToxins; GasMixture.TotalFuel and TotalOxidiser.
    private static readonly Chemistry.GasType[] HumanToxins =
    {
        Chemistry.GasType.Pollutant, Chemistry.GasType.Methane, Chemistry.GasType.Hydrazine, Chemistry.GasType.Silanol,
        Chemistry.GasType.HydrochloricAcid
    };

    private static readonly Chemistry.GasType[] Fuels =
    {
        Chemistry.GasType.Methane, Chemistry.GasType.LiquidMethane, Chemistry.GasType.Hydrogen,
        Chemistry.GasType.LiquidHydrogen, Chemistry.GasType.LiquidAlcohol
    };

    private static readonly Chemistry.GasType[] Oxidisers =
    {
        Chemistry.GasType.Oxygen, Chemistry.GasType.LiquidOxygen, Chemistry.GasType.NitrousOxide,
        Chemistry.GasType.LiquidNitrousOxide, Chemistry.GasType.Ozone, Chemistry.GasType.LiquidOzone
    };

    private static Chemistry.GasType[]? _gasTypes;

    internal static object Handle(Args args)
    {
        GlobalGasMix tank = PlanetaryAtmosphereSimulation.GetGlobalGasMix();
        GlobalAtmosphereData? data = WorldSetting.Current?.Data?.GlobalAtmosphereData;
        if (tank == null || data == null)
        {
            return new PlanetNotLoadedView(TerraformingModReader.Read());
        }

        return Read(tank, data);
    }

    private static PlanetView Read(GlobalGasMix tank, GlobalAtmosphereData data)
    {
        PlanetTank tankReading = ReadTank(tank, data);
        double temperature = PlanetaryAtmosphereSimulation.AggregateTemperature.ToDouble();
        OrbitalSimulation orbit = OrbitalSimulation.System;
        float energyNow = orbit.GetSolarEnergyPercentClamped(orbit.GetSolarEnergy(), orbit.CalculateSolarIrradiance());
        TemperatureSweepView today = Sweep(tank, data, energyNow);
        TemperatureSweepView far = Sweep(tank, data, FarthestSolarEnergyPercent);
        TemperatureSweepView near = Sweep(tank, data, NearestSolarEnergyPercent);
        double orbitMin = Math.Min(today.MinK, Math.Min(far.MinK, near.MinK));
        double orbitMax = Math.Max(today.MaxK, Math.Max(far.MaxK, near.MaxK));
        TemperatureRangeView range = new TemperatureRangeView(today, orbitMin, orbitMax);
        PlanetWeather weather = ReadWeather(tankReading, temperature, energyNow, range);
        return new PlanetView(
            ReadClock(),
            tankReading,
            weather,
            ReadAir(tank, tankReading, temperature),
            ReadGases(tank, tankReading, temperature, today.MinK, orbitMin),
            ReadReservoirs(),
            ReadStartingAir(data, tankReading),
            TerraformingModReader.Read());
    }

    private static StartingAirView ReadStartingAir(GlobalAtmosphereData data, PlanetTank tank)
    {
        List<ReservoirGasView> gases = new List<ReservoirGasView>();
        double perCell = 0.0;
        foreach (GlobalMoleData mole in data.GlobalGasMixData.GlobalMoleDatas)
        {
            if (mole != null && mole.Quantity > 0f)
            {
                perCell += mole.Quantity;
                gases.Add(new ReservoirGasView(mole.Type.ToString(), mole.Quantity * tank.Cells));
            }
        }

        return new StartingAirView(perCell, perCell * tank.Cells, gases);
    }

    // World time: whole days and the fraction of today both come from the player body's accumulated rotation
    // (RotatingCelestialBody.DayCount, OrbitalSimulation.GetTimeOfDay) and are saved with the world, so this counts
    // every second played, stops while paused and carries across loads.
    private static PlanetClock ReadClock()
    {
        double dayLength = OrbitalSimulation.GetDayLengthSeconds();
        return new PlanetClock((WorldManager.DaysPast + (double)OrbitalSimulation.TimeOfDay) * dayLength, dayLength);
    }

    private static PlanetTank ReadTank(GlobalGasMix tank, GlobalAtmosphereData data)
    {
        double volume = tank.Volume.ToDouble();
        double shipped = data.Volume?.Value ?? 0.0;
        SeaView sea = new SeaView(
            PlanetaryAtmosphereSimulation.LiquidVolume.ToDouble(),
            GlobalAtmosphereLiquid.RenderThreshold.ToDouble(),
            GlobalAtmosphereLiquid.IsRendered);
        return new PlanetTank(
            volume,
            tank.VolumeForGas().ToDouble(),
            volume / Chemistry.GridVolume.ToDouble(),
            shipped > 0.0 ? volume / shipped : (double?)null,
            tank.TotalQuantityGas().ToDouble(),
            tank.TotalQuantityLiquid().ToDouble(),
            tank.VolumeOfLiquid().ToDouble(),
            sea);
    }

    private static PlanetWeather ReadWeather(PlanetTank tank, double temperature, float energyNow,
        TemperatureRangeView range)
    {
        WeatherEvent? storm = WeatherManager.IsWeatherEventRunning ? WeatherManager.CurrentWeatherEvent : null;
        TemperaturePartsView parts = new TemperaturePartsView(
            PlanetaryAtmosphereSimulation.SolarAngleTemperature.ToDouble(),
            PlanetaryAtmosphereSimulation.SolarDistanceOffsetTemperature.ToDouble(),
            PlanetaryAtmosphereSimulation.GhgIndexOffset.ToDouble(),
            PlanetaryAtmosphereSimulation.DensityOffsetTemperature.ToDouble(),
            storm != null ? PlanetaryAtmosphereSimulation.WeatherOffset.ToDouble() : 0.0,
            PlanetaryAtmosphereSimulation.LatentOffset.ToDouble(),
            PlanetaryAtmosphereSimulation.ExternalInputOffset.ToDouble());
        return new PlanetWeather(
            PlanetMath.PressureKpa(tank.GasMoles, temperature, tank.GasVolumeL),
            PlanetaryAtmosphereSimulation.GlobalPressure.ToDouble(),
            temperature,
            parts,
            Vector3.Angle(Vector3.up, OrbitalSimulation.WorldSunVector),
            energyNow,
            range,
            storm == null ? null : new StormView(storm.Id));
    }

    private static PlanetAir ReadAir(GlobalGasMix tank, PlanetTank reading, double temperature)
    {
        double gasMoles = reading.GasMoles;
        return new PlanetAir(
            PlanetMath.PressureKpa(tank.Get(Chemistry.GasType.Oxygen).ToDouble(), temperature, reading.GasVolumeL),
            PlanetMath.PressureKpa(Sum(tank, HumanToxins), temperature, reading.GasVolumeL),
            Sum(tank, Fuels),
            Sum(tank, Oxidisers),
            gasMoles > 0.0 ? tank.Get(Chemistry.GasType.CarbonDioxide).ToDouble() / gasMoles : 0.0);
    }

    private static double Sum(GlobalGasMix tank, Chemistry.GasType[] types)
    {
        double sum = 0.0;
        foreach (Chemistry.GasType type in types)
        {
            sum += tank.Get(type).ToDouble();
        }

        return sum;
    }

    // Every sun angle from 0 (overhead) to 180 at one solar energy percent.
    private static TemperatureSweepView Sweep(GlobalGasMix tank, GlobalAtmosphereData data, float energyPercent)
    {
        TemperatureSweep sweep = new TemperatureSweep();
        for (int angle = 0; angle <= LastSunAngle; angle++)
        {
            sweep.Add(angle, tank.GetGlobalGasMixTemperature(data, angle, energyPercent).ToDouble());
        }

        return new TemperatureSweepView(sweep.MinK, sweep.MinAngle, sweep.MaxK, sweep.MaxAngle);
    }

    private static List<PlanetGasView> ReadGases(GlobalGasMix tank, PlanetTank reading, double temperature,
        double todayMin, double orbitMin)
    {
        List<PlanetGasView> gases = new List<PlanetGasView>();
        foreach (Chemistry.GasType type in GasTypes())
        {
            double moles = tank.Get(type).ToDouble();
            if (moles > TraceMoles)
            {
                gases.Add(ReadGas(type, moles, reading, temperature, todayMin, orbitMin));
            }
        }

        return gases;
    }

    private static PlanetGasView ReadGas(Chemistry.GasType type, double moles, PlanetTank reading, double temperature,
        double todayMin, double orbitMin)
    {
        bool liquid = Mole.MatterState(type) == AtmosphereHelper.MatterState.Liquid;
        Mole probe = new Mole(type, new MoleQuantity(1.0), MoleEnergy.Zero);
        double offset = GlobalGasMix.GlobalTemperatureStateChangeOffset.ToDouble();
        double minLiquid = Mole.MinLiquidPressure(type).ToDouble();
        PlanetGasAmount amount = new PlanetGasAmount(
            type.ToString(),
            Text.Plain(probe.DisplayName),
            liquid ? "liquid" : "gas",
            moles,
            reading.Cells > 0.0 ? moles / reading.Cells : 0.0,
            liquid ? (double?)null : PlanetMath.PressureKpa(moles, temperature, reading.GasVolumeL));
        PlanetGasPhases phases = new PlanetGasPhases(
            Mole.CanFreeze(type) ? Mole.FreezingTemperature(type).ToDouble() + offset : (double?)null,
            liquid ? (double?)null : minLiquid,
            liquid ? null : CondenseBelow(probe, PlanetPressureAt(reading, todayMin), minLiquid),
            liquid ? null : CondenseBelow(probe, PlanetPressureAt(reading, orbitMin), minLiquid));
        return new PlanetGasView(amount, phases);
    }

    private static double PlanetPressureAt(PlanetTank reading, double kelvin) =>
        PlanetMath.PressureKpa(reading.GasMoles, kelvin, reading.GasVolumeL);

    // Mole.ChangeState, gas branch: a gas condenses when the planet's pressure is at least its minimum liquid
    // pressure and the planet is below its evaporation temperature at that pressure plus the state change offset.
    private static double? CondenseBelow(Mole probe, double pressureKpa, double minLiquidKpa)
    {
        if (!MoleHelper.CanCondense(probe.Type) || pressureKpa < minLiquidKpa)
        {
            return null;
        }

        return probe.EvaporationTemperatureClamped(new PressurekPa(pressureKpa)).ToDouble()
            + GlobalGasMix.GlobalTemperatureStateChangeOffset.ToDouble();
    }

    private static ReservoirsView ReadReservoirs() =>
        new ReservoirsView(
            ReadReservoir(GameMembers.PlanetLiquidClouds),
            ReadReservoir(GameMembers.PlanetIceClouds),
            ReadReservoir(GameMembers.PlanetIceCaps));

    private static ReservoirView? ReadReservoir(GameField field)
    {
        if (!(field.GetValue(null) is GlobalGasMix mix))
        {
            return null;
        }

        List<ReservoirGasView> contents = new List<ReservoirGasView>();
        foreach (Chemistry.GasType type in GasTypes())
        {
            double moles = mix.Get(type).ToDouble();
            if (moles > TraceMoles)
            {
                contents.Add(new ReservoirGasView(type.ToString(), moles));
            }
        }

        return new ReservoirView(
            mix.Volume.ToDouble(),
            mix.VolumeOfLiquid().ToDouble(),
            mix.TotalQuantityGas().ToDouble() + mix.TotalQuantityLiquid().ToDouble(),
            contents);
    }

    // Every gas and liquid a GlobalGasMix holds: the defined, non-composite GasType values, in enum order.
    private static Chemistry.GasType[] GasTypes()
    {
        if (_gasTypes != null)
        {
            return _gasTypes;
        }

        List<Chemistry.GasType> types = new List<Chemistry.GasType>();
        foreach (Chemistry.GasType type in (Chemistry.GasType[])Enum.GetValues(typeof(Chemistry.GasType)))
        {
            if (type != Chemistry.GasType.Undefined && Mole.MatterState(type) != AtmosphereHelper.MatterState.None)
            {
                types.Add(type);
            }
        }

        _gasTypes = types.ToArray();
        return _gasTypes;
    }
}

/// <summary>
/// Terraforming Reloaded, when it is loaded: its version, whether it is live in this world (only then does the planet
/// take and give gas), and the kelvin its temperature response added at the last planet tick. Read by name through
/// GameMembers, so the mod is not a dependency.
/// </summary>
internal static class TerraformingModReader
{
    internal static TerraformingModView? Read()
    {
        if (GameMembers.TerraformingGate.OrNull == null)
        {
            return null;
        }

        string? state = null;
        double? adjustment = null;
        try
        {
            state = Member(GameMembers.TerraformingDescribe).Invoke(null, null) as string;
            adjustment = Member(GameMembers.TerraformingLastAdjustment).Invoke(null, null) as double?;
        }
        catch (Exception error)
        {
            // Gate.Describe and Climate.LastAdjustment belong to another mod; either may throw or be missing.
            state ??= "could not be read: " + (error.InnerException ?? error).Message;
        }

        string? version = GameMembers.TerraformingVersion.TryResolve()
            ? GameMembers.TerraformingVersion.GetValue(null) as string
            : null;
        return new TerraformingModView(version, state, adjustment);
    }

    private static System.Reflection.MethodInfo Member(GameMethod member)
    {
        if (!member.TryResolve())
        {
            throw new InvalidOperationException(member.Name + " is missing (Terraforming Reloaded has changed)");
        }

        return member.Info;
    }
}
