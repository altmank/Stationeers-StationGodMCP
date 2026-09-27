#nullable enable

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Genetics;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Objects.Pipes;
using Assets.Scripts.Util;
using Genetics;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api;

/// <summary>
/// A plant's states and conditions. The game sets the states each atmospheric tick: dry under half the water it
/// needs, cannot breathe under half the gas it takes in, too cold or hot and pressure too low or high outside the
/// survivable range, harmful gas above a harmful gas's limit, water too cold or hot from the tray's water
/// (Plant.TakePlantBreath, Plant.TakePlantDrink, Plant.OnAtmosphericTick). Lit and dark are not problems by
/// themselves. Each condition's running time grows a second per second in it and falls five per second out of it
/// (PlantStatus.SetCurrentState); past its limit the plant takes damage every tick (PlantStatus.WillDamage).
/// </summary>
internal static class PlantConditions
{
    private static readonly PlantState[] All =
    {
        new PlantState(PlantStatusType.Dehydrated, "dry", true),
        new PlantState(PlantStatusType.Lit, "in light", false),
        new PlantState(PlantStatusType.Darkness, "in darkness", false),
        new PlantState(PlantStatusType.LowTemperature, "too cold", true),
        new PlantState(PlantStatusType.HighTemperature, "too hot", true),
        new PlantState(PlantStatusType.Suffocated, "cannot breathe", true),
        new PlantState(PlantStatusType.LowPressure, "pressure too low", true),
        new PlantState(PlantStatusType.HighPressure, "pressure too high", true),
        new PlantState(PlantStatusType.UnDesiredGas, "harmful gas", true),
        new PlantState(PlantStatusType.LowWaterTemperature, "water too cold", true),
        new PlantState(PlantStatusType.HighWaterTemperature, "water too hot", true)
    };

    internal static PlantStates States(Plant plant)
    {
        List<string> problems = new List<string>();
        List<string> active = new List<string>();
        foreach (PlantState state in All)
        {
            if (plant.PlantStatus.GetCurrentState(state.Type))
            {
                active.Add(state.Words);
                if (state.Problem)
                {
                    problems.Add(state.Words);
                }
            }
        }

        return new PlantStates(problems, active);
    }

    internal static List<PlantConditionView> Read(Plant plant, bool hasGenes)
    {
        float[]? seconds = GameMembers.PlantAggregateStates.GetValue(plant.PlantStatus) as float[];
        List<PlantConditionView> conditions = new List<PlantConditionView>(All.Length);
        foreach (PlantState state in All)
        {
            int index = (int)state.Type;
            float? limit = DamageLimit(plant.lifeRequirements, state.Type, hasGenes);
            float? time = seconds != null && index < seconds.Length ? seconds[index] : null;
            bool active = plant.PlantStatus.GetCurrentState(state.Type);
            bool damaging = hasGenes && plant.PlantStatus.WillDamage(state.Type, plant);
            conditions.Add(new PlantConditionView(state.Type.ToString(), state.Words, active, time, limit, damaging));
        }

        return conditions;
    }

    private static float? DamageLimit(PlantLifeRequirements needs, PlantStatusType type, bool hasGenes)
    {
        PlantStat? stat = type switch
        {
            PlantStatusType.Dehydrated => needs.TimeUntilDehydrationDamage,
            PlantStatusType.Lit => needs.TimeUntilLightDamage,
            PlantStatusType.Darkness => needs.TimeUntilDarknessDamage,
            PlantStatusType.LowTemperature or PlantStatusType.LowWaterTemperature => needs.TimeUntilFrozenDamage,
            PlantStatusType.HighTemperature or PlantStatusType.HighWaterTemperature => needs.TimeUntilOverHeatedDamage,
            PlantStatusType.Suffocated => needs.TimeUntilSuffocatedDamage,
            PlantStatusType.LowPressure => needs.TimeUntilLowPressureDamage,
            PlantStatusType.HighPressure => needs.TimeUntilHighPressureDamage,
            PlantStatusType.UnDesiredGas => needs.TimeUntilUndesiredGasDamage,
            _ => null
        };
        return stat == null ? null : PlantReader.Stat(stat, hasGenes);
    }

    private sealed class PlantState
    {
        internal PlantState(PlantStatusType type, string words, bool problem)
        {
            Type = type;
            Words = words;
            Problem = problem;
        }

        internal PlantStatusType Type { get; }

        internal string Words { get; }

        internal bool Problem { get; }
    }
}

/// <summary>
/// The air a plant breathes: the tray's own atmosphere when it has one, otherwise the room it stands in
/// (Plant.BreathingAtmosphere; a Hydroponics Tray has none). And the tray's water: its liquid pipe network
/// (IGrower.WaterAtmosphere); the plant drinks only while it holds 1 mol of liquid, and then only water
/// (Plant.TakePlantDrink).
/// </summary>
internal static class PlantAir
{
    private static readonly Regex CamelCaseWord = new Regex("(?<=[a-z])(?=[A-Z])", RegexOptions.Compiled);

    private static readonly Chemistry.GasType[] ReportedGases =
    {
        Chemistry.GasType.Oxygen, Chemistry.GasType.CarbonDioxide, Chemistry.GasType.Nitrogen,
        Chemistry.GasType.Steam, Chemistry.GasType.Methane, Chemistry.GasType.Pollutant, Chemistry.GasType.NitrousOxide
    };

    internal static PlantAirView? Breathing(Atmosphere? atmosphere)
    {
        if (atmosphere == null || atmosphere.BeingDestroyed)
        {
            return null;
        }

        Dictionary<string, float?> ratios = new Dictionary<string, float?>();
        foreach (Chemistry.GasType gas in ReportedGases)
        {
            ratios[GasName(gas).Replace(' ', '_')] = GasRatio(atmosphere, gas);
        }

        return new PlantAirView(atmosphere.PressureGasses.ToFloat(), (float)atmosphere.Temperature.ToDouble(),
            atmosphere.TotalMoles.ToDouble(), ratios);
    }

    internal static PlantWaterView? Water(IGrower? tray)
    {
        Atmosphere? water = tray?.WaterAtmosphere;
        if (water == null || water.BeingDestroyed)
        {
            return null;
        }

        return new PlantWaterView(water.TotalMolesLiquids.ToDouble(), water.GasMixture.Water.Quantity.ToDouble(),
            (float)water.Temperature.ToDouble());
    }

    // The in-game gas names: Methane is Volatiles and Steam is water vapour.
    internal static string GasName(Chemistry.GasType gas) => gas switch
    {
        Chemistry.GasType.Methane => "volatiles",
        Chemistry.GasType.Steam => "steam",
        Chemistry.GasType.Water => "water",
        _ => CamelCaseWord.Replace(gas.ToString(), " ").ToLowerInvariant()
    };

    internal static float? GasRatio(Atmosphere atmosphere, Chemistry.GasType gas)
    {
        try
        {
            return atmosphere.GasMixture.GetGasTypeRatio(gas);
        }
        catch (Exception)
        {
            // GasMixture.GetGasTypeRatio on an empty mixture divides by its zero total.
            return null;
        }
    }

    internal static float? PartialPressure(Atmosphere atmosphere, Chemistry.GasType gas)
    {
        try
        {
            return atmosphere.PartialPressure(gas).ToFloat();
        }
        catch (Exception)
        {
            // Atmosphere.PartialPressure on an empty mixture divides by its zero total.
            return null;
        }
    }
}

/// <summary>A plant's needs: its bands, the gases it takes in and gives out, water, light and harmful gases.</summary>
internal static class PlantNeeds
{
    private const float CelsiusToKelvin = 273.15f;

    internal static PlantNeedsView Read(Plant plant, bool hasGenes)
    {
        PlantLifeRequirements needs = plant.lifeRequirements;
        LifeRequirementsData data = needs.Data;
        PlantBands bands = new PlantBands(Band(needs.GrowTemperatureC, hasGenes, CelsiusToKelvin),
            Band(needs.GrowPressure, hasGenes, 0f));
        float? water = hasGenes
            ? needs.WaterPerTick.ToFloat()
            : data.LiquidPerTick != null ? data.LiquidPerTick.Quantity * needs.WaterUsage.Base : null;
        PlantDaily daily = new PlantDaily(water, PlantReader.Stat(needs.LightPerDay, hasGenes),
            PlantReader.Stat(needs.DarknessPerDay, hasGenes));
        return new PlantNeedsView(data.Id, bands, Gases(plant, needs, hasGenes), daily);
    }

    // It takes each gas in full once the gas is at least its ratio of the air, less below that
    // (Plant.TakePlantBreath). Above a harmful gas's limit (times the resistance gene) the harmful gas timer runs
    // (Plant.OnAtmosphericTick).
    private static PlantGases Gases(Plant plant, PlantLifeRequirements needs, bool hasGenes)
    {
        LifeRequirementsData data = needs.Data;
        Atmosphere? air = plant.BreathingAtmosphere;
        bool airOk = air != null && !air.BeingDestroyed;
        float production = PlantReader.Stat(needs.GasProduction, hasGenes);
        float resistance = PlantReader.Stat(needs.UndesiredGasResistance, hasGenes);
        List<InhaledGasView> takesIn = new List<InhaledGasView>();
        foreach (GasQuantityRatioData gas in data.InhaledGases ?? new List<GasQuantityRatioData>())
        {
            takesIn.Add(new InhaledGasView(PlantAir.GasName(gas.Type), gas.Quantity * production, gas.Ratio,
                airOk ? PlantAir.GasRatio(air!, gas.Type) : null));
        }

        List<ExhaledGasView> givesOut = new List<ExhaledGasView>();
        foreach (GasQuantityData gas in data.ExhaledGases ?? new List<GasQuantityData>())
        {
            givesOut.Add(new ExhaledGasView(PlantAir.GasName(gas.Type), gas.Quantity * production));
        }

        List<HarmfulGasView> harmful = new List<HarmfulGasView>();
        foreach (GasPressureData gas in data.HarmfulGases ?? new List<GasPressureData>())
        {
            harmful.Add(new HarmfulGasView(PlantAir.GasName(gas.Type), gas.PartialPressure * resistance,
                airOk ? PlantAir.PartialPressure(air!, gas.Type) : null));
        }

        return new PlantGases(takesIn, givesOut, harmful);
    }

    // Ideal: full speed. Survivable: efficiency falls to 0 at its edges, and outside it the too hot, too cold or
    // pressure state is set (PlantLifeRequirements.UpdateTemperaturePressureCurves).
    // MultiPlantStat's temperature band is in Celsius (PlantLifeRequirements.GrowTemperatureC); offset shifts it to
    // kelvin.
    private static BandView Band(MultiPlantStat stat, bool hasGenes, float offset) => hasGenes
        ? new BandView(new[] { stat.IdealMin() + offset, stat.IdealMax() + offset },
            new[] { stat.Min() + offset, stat.Max() + offset })
        : new BandView(new[] { stat.IdealMin(0f) + offset, stat.IdealMax(0f) + offset },
            new[] { stat.Min(0f) + offset, stat.Max(0f) + offset });
}

/// <summary>When a plant reaches its next stages, and what its next harvest will be.</summary>
internal static class PlantForecast
{
    internal static PlantForecastView Read(PlantFacts facts)
    {
        List<PlantStage> stages = facts.Stages;
        bool live = !facts.Dead;
        double? next = live ? NextStageSeconds(stages, facts.Stage, facts.Progress, facts.Rate) : null;
        double? harvest = live ? SecondsToStage(stages, facts.Stage, facts.Progress, facts.FirstMature, facts.Rate)
            : null;
        double? seeds = live ? SecondsToStage(stages, facts.Stage, facts.Progress, facts.FirstSeeding, facts.Rate)
            : null;
        return new PlantForecastView(PlantReader.FloatOrNull(facts.Rate), next, harvest, seeds, Regrow(facts));
    }

    // After a harvest a perennial drops to the stage before its first mature one with no progress, and takes a
    // cycle of any fertiliser waiting in the tray (Plant.ResetPerennialStage).
    private static RegrowTimes Regrow(PlantFacts facts)
    {
        if (facts.Perennial != true)
        {
            return new RegrowTimes(null, null);
        }

        int from = Math.Max(facts.FirstMature - 1, 0);
        double? atRate = null;
        if (!facts.Dead)
        {
            float speed = facts.Waiting != null ? facts.Waiting.GrowthSpeed : 1f;
            atRate = SecondsToStage(facts.Stages, from, 0f, facts.FirstMature, facts.Efficiency * speed);
        }

        return new RegrowTimes(atRate, SecondsToStage(facts.Stages, from, 0f, facts.FirstMature, 1.0));
    }

    // Seconds until the plant enters stage target at this rate: 0 when it is there or past it, null when it never
    // gets there (a stage on the way that never ends, no such stage, or no growth).
    private static double? SecondsToStage(List<PlantStage> stages, int stage, float? progress, int target,
        double rate)
    {
        if (target < 0 || progress == null)
        {
            return null;
        }

        if (stage >= target)
        {
            return 0.0;
        }

        if (double.IsNaN(rate) || rate <= 0.0)
        {
            return null;
        }

        double remaining = 0.0;
        for (int index = stage; index < target && index < stages.Count; index++)
        {
            float length = stages[index].Length;
            if (length < 0f)
            {
                return null;
            }

            remaining += index == stage ? Math.Max(0.0, length - progress.Value) : length;
        }

        return Math.Round(remaining / rate, 1);
    }

    private static double? NextStageSeconds(List<PlantStage> stages, int stage, float? progress, double rate)
    {
        if (stage < 0 || stage + 1 >= stages.Count || stages[stage].Length < 0f)
        {
            return null;
        }

        return SecondsToStage(stages, stage, progress, stage + 1, rate);
    }

    // PlantLifeRequirements.SetHarvestQuantityOnMature: each point of stress (time in a bad condition over that
    // condition's damage limit, plus light stress) costs Plant's resilience of one item. Already set once mature.
    internal static HarvestForecastView? Harvest(Plant plant, bool hasGenes)
    {
        PlantRecord? record = plant.PlantRecord;
        if (record == null)
        {
            return null;
        }

        PlantLifeRequirements needs = plant.lifeRequirements;
        double stress = Share(record.TimeDehydrated, needs.TimeUntilDehydrationDamage, hasGenes)
                        + Share(record.TimeFrozen, needs.TimeUntilFrozenDamage, hasGenes)
                        + Share(record.TimeOverHeated, needs.TimeUntilOverHeatedDamage, hasGenes)
                        + Share(record.TimeSuffocated, needs.TimeUntilSuffocatedDamage, hasGenes)
                        + Share(record.TimeLowPressure, needs.TimeUntilLowPressureDamage, hasGenes)
                        + Share(record.TimeHighPressure, needs.TimeUntilHighPressureDamage, hasGenes)
                        + Share(record.TimePolluted, needs.TimeUntilUndesiredGasDamage, hasGenes)
                        + record.LightStress;
        float resilience = (float)GameMembers.PlantResilience.GetValue(null)!;
        double expected = plant.HarvestQuantityMax + plant.FertilizerHarvestQuantityBoost - stress * resilience;
        return new HarvestForecastView(Math.Round(Math.Max(1.0, expected), 2), Math.Round(stress, 3));
    }

    private static double Share(float seconds, PlantStat limit, bool hasGenes)
    {
        float value = PlantReader.Stat(limit, hasGenes);
        return value > 0f ? seconds / value : 0.0;
    }
}
