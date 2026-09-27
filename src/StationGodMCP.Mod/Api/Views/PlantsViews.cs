#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Api.Views;

/// <summary>
/// plants: every plant in a tray, planter or hydroponics station, and the clock its forecasts run on.
/// </summary>
internal sealed class PlantsView
{
    internal PlantsView(float gameTime, int dayLengthS, bool debugFastGrowth, List<PlantView> plants)
    {
        GameTimeS = gameTime;
        DayLengthS = dayLengthS;
        DebugFastGrowth = debugFastGrowth;
        Plants = plants;
        Count = plants.Count;
    }

    public float GameTimeS { get; }

    public int DayLengthS { get; }

    /// <summary>A debug switch (SyncCustomGrowSpeed): while on, every plant jumps a stage each tick.</summary>
    public bool DebugFastGrowth { get; }

    public List<PlantView> Plants { get; }

    public int Count { get; }
}

/// <summary>One plant: where it grows, its stage, what slows it, what it needs, and when it will be ready.</summary>
internal sealed class PlantView
{
    internal PlantView(PlantIdentity identity, PlantGrowth growth, PlantCare care, PlantSupply supply,
        PlantHistory history)
    {
        ReferenceId = identity.Thing.ReferenceId;
        PrefabName = identity.Thing.PrefabName;
        Name = identity.Name;
        DisplayName = identity.Thing.DisplayName;
        Planted = identity.Planted;
        Tray = identity.Tray;
        Stage = growth.Stage;
        Stages = growth.Stages;
        MaturityRatio = growth.MaturityRatio;
        SeedingRatio = growth.SeedingRatio;
        Mature = growth.Mature;
        Seeding = growth.Seeding;
        Dead = growth.Dead;
        Perennial = growth.Perennial;
        ReadyToHarvest = growth.ReadyToHarvest;
        Efficiency = care.Efficiency;
        Problems = care.Problems;
        ActiveStates = care.ActiveStates;
        Conditions = care.Conditions;
        CanHeal = care.CanHeal;
        Health = care.Health;
        Harvest = supply.Harvest;
        Fertiliser = supply.Fertiliser;
        Light = supply.Light;
        Atmosphere = supply.Atmosphere;
        Water = supply.Water;
        Needs = supply.Needs;
        Record = history.Record;
        Genes = history.Genes;
        Forecast = history.Forecast;
    }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public string? Name { get; }

    /// <summary>The game appends the tray's label: "Pumpkin_Tray 3" (Plant.DisplayName).</summary>
    public string? DisplayName { get; }

    public bool Planted { get; }

    public PlantTrayView? Tray { get; }

    public PlantStageView Stage { get; }

    public List<StageView> Stages { get; }

    public double MaturityRatio { get; }

    public double SeedingRatio { get; }

    public bool Mature { get; }

    public bool Seeding { get; }

    public bool Dead { get; }

    public bool? Perennial { get; }

    public bool ReadyToHarvest { get; }

    public PlantEfficiencyView Efficiency { get; }

    public List<string> Problems { get; }

    public List<string> ActiveStates { get; }

    public List<PlantConditionView>? Conditions { get; }

    public bool? CanHeal { get; }

    public PlantHealthView? Health { get; }

    public PlantHarvestView Harvest { get; }

    public PlantFertiliserView Fertiliser { get; }

    public PlantLightView Light { get; }

    public PlantAirView? Atmosphere { get; }

    public PlantWaterView? Water { get; }

    public PlantNeedsView? Needs { get; }

    public PlantRecordView? Record { get; }

    /// <summary>The top gene set's values by gene name; null for a plant with no gene set.</summary>
    public SortedDictionary<string, float>? Genes { get; }

    public PlantForecastView Forecast { get; }
}

internal sealed class PlantIdentity
{
    internal PlantIdentity(ThingView thing, string? name, bool planted, PlantTrayView? tray)
    {
        Thing = thing;
        Name = name;
        Planted = planted;
        Tray = tray;
    }

    internal ThingView Thing { get; }

    internal string? Name { get; }

    internal bool Planted { get; }

    internal PlantTrayView? Tray { get; }
}

internal sealed class PlantGrowth
{
    internal PlantGrowth(PlantStageView stage, List<StageView> stages, double maturityRatio, double seedingRatio,
        PlantPhase phase)
    {
        Stage = stage;
        Stages = stages;
        MaturityRatio = maturityRatio;
        SeedingRatio = seedingRatio;
        Mature = phase.Mature;
        Seeding = phase.Seeding;
        Dead = phase.Dead;
        Perennial = phase.Perennial;
        ReadyToHarvest = phase.ReadyToHarvest;
    }

    internal PlantStageView Stage { get; }

    internal List<StageView> Stages { get; }

    internal double MaturityRatio { get; }

    internal double SeedingRatio { get; }

    internal bool Mature { get; }

    internal bool Seeding { get; }

    internal bool Dead { get; }

    internal bool? Perennial { get; }

    internal bool ReadyToHarvest { get; }
}

/// <summary>Where a plant is in its life.</summary>
internal sealed class PlantPhase
{
    internal PlantPhase(bool mature, bool seeding, bool dead, bool? perennial, bool readyToHarvest)
    {
        Mature = mature;
        Seeding = seeding;
        Dead = dead;
        Perennial = perennial;
        ReadyToHarvest = readyToHarvest;
    }

    internal bool Mature { get; }

    internal bool Seeding { get; }

    internal bool Dead { get; }

    internal bool? Perennial { get; }

    internal bool ReadyToHarvest { get; }
}

internal sealed class PlantCare
{
    internal PlantCare(PlantEfficiencyView efficiency, PlantStates states, List<PlantConditionView>? conditions,
        bool? canHeal, PlantHealthView? health)
    {
        Efficiency = efficiency;
        Problems = states.Problems;
        ActiveStates = states.Active;
        Conditions = conditions;
        CanHeal = canHeal;
        Health = health;
    }

    internal PlantEfficiencyView Efficiency { get; }

    internal List<string> Problems { get; }

    internal List<string> ActiveStates { get; }

    internal List<PlantConditionView>? Conditions { get; }

    internal bool? CanHeal { get; }

    internal PlantHealthView? Health { get; }
}

/// <summary>The plant's states in words: the problems, and every state that is on.</summary>
internal sealed class PlantStates
{
    internal PlantStates(List<string> problems, List<string> active)
    {
        Problems = problems;
        Active = active;
    }

    internal List<string> Problems { get; }

    internal List<string> Active { get; }
}

internal sealed class PlantSupply
{
    internal PlantSupply(PlantHarvestView harvest, PlantFertiliserView fertiliser, PlantLightView light,
        PlantAirView? atmosphere, PlantWaterView? water, PlantNeedsView? needs)
    {
        Harvest = harvest;
        Fertiliser = fertiliser;
        Light = light;
        Atmosphere = atmosphere;
        Water = water;
        Needs = needs;
    }

    internal PlantHarvestView Harvest { get; }

    internal PlantFertiliserView Fertiliser { get; }

    internal PlantLightView Light { get; }

    internal PlantAirView? Atmosphere { get; }

    internal PlantWaterView? Water { get; }

    internal PlantNeedsView? Needs { get; }
}

internal sealed class PlantHistory
{
    internal PlantHistory(PlantRecordView? record, SortedDictionary<string, float>? genes, PlantForecastView forecast)
    {
        Record = record;
        Genes = genes;
        Forecast = forecast;
    }

    internal PlantRecordView? Record { get; }

    internal SortedDictionary<string, float>? Genes { get; }

    internal PlantForecastView Forecast { get; }
}

internal sealed class PlantTrayView
{
    internal PlantTrayView(ThingView tray, bool litByGrowLight)
    {
        ReferenceId = tray.ReferenceId;
        PrefabName = tray.PrefabName;
        DisplayName = tray.DisplayName;
        LitByGrowLight = litByGrowLight;
    }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public string? DisplayName { get; }

    public bool LitByGrowLight { get; }
}

internal sealed class PlantStageView
{
    internal PlantStageView(int index, int count, StageMarks marks, float? progressS, float? lengthS)
    {
        Index = index;
        Count = count;
        FirstMatureIndex = marks.FirstMature;
        FirstSeedingIndex = marks.FirstSeeding;
        DeadIndex = marks.Dead;
        MatureIndex = marks.LastMature;
        SeedingIndex = marks.LastSeeding;
        ProgressS = progressS;
        LengthS = lengthS;
    }

    public int Index { get; }

    public int Count { get; }

    /// <summary>The first stage with fruit to harvest.</summary>
    public int FirstMatureIndex { get; }

    public int FirstSeedingIndex { get; }

    public int DeadIndex { get; }

    /// <summary>The game's MatureIndex: the LAST mature stage, used only for maturity_ratio.</summary>
    public int MatureIndex { get; }

    /// <summary>The game's SeedingIndex: the LAST seeding stage, used only for seeding_ratio.</summary>
    public int SeedingIndex { get; }

    /// <summary>Growth-seconds: advances at efficiency x fertiliser boost per real second.</summary>
    public float? ProgressS { get; }

    public float? LengthS { get; }
}

/// <summary>
/// Stage indexes that matter: first mature, first seeding, dead, and the game's last mature and seeding.
/// </summary>
internal sealed class StageMarks
{
    internal StageMarks(int firstMature, int firstSeeding, int dead, int lastMature, int lastSeeding)
    {
        FirstMature = firstMature;
        FirstSeeding = firstSeeding;
        Dead = dead;
        LastMature = lastMature;
        LastSeeding = lastSeeding;
    }

    internal int FirstMature { get; }

    internal int FirstSeeding { get; }

    internal int Dead { get; }

    internal int LastMature { get; }

    internal int LastSeeding { get; }
}

internal sealed class StageView
{
    internal StageView(int index, float lengthS, bool mature, bool seeding, bool dead)
    {
        Index = index;
        LengthS = lengthS;
        Mature = mature;
        Seeding = seeding;
        Dead = dead;
    }

    public int Index { get; }

    /// <summary>Below 0: the stage never ends.</summary>
    public float LengthS { get; }

    public bool Mature { get; }

    public bool Seeding { get; }

    public bool Dead { get; }
}

internal sealed class PlantEfficiencyView
{
    internal PlantEfficiencyView(float? growth, ushort growthPercent, PlantFactors factors, float? geneSpeed)
    {
        GrowthFactor = growth;
        GrowthPercent = growthPercent;
        BreathingFactor = factors.Breathing;
        TemperatureFactor = factors.Temperature;
        HydrationFactor = factors.Hydration;
        PressureFactor = factors.Pressure;
        LightFactor = factors.Light;
        RandomFactor = factors.Random;
        GeneSpeedFactor = geneSpeed;
    }

    /// <summary>What growth runs at now, 1 = full speed (PlantLifeRequirements.GrowthEfficiency).</summary>
    public float? GrowthFactor { get; }

    /// <summary>The game's own rounded figure, as the tooltip shows it.</summary>
    public ushort GrowthPercent { get; }

    public float BreathingFactor { get; }

    public float TemperatureFactor { get; }

    public float HydrationFactor { get; }

    public float PressureFactor { get; }

    public float LightFactor { get; }

    public float RandomFactor { get; }

    public float? GeneSpeedFactor { get; }
}

/// <summary>PlantStatus's efficiencies and the plant's fixed random factor.</summary>
internal sealed class PlantFactors
{
    internal PlantFactors(float breathing, float temperature, float hydration, float pressure, float light,
        float random)
    {
        Breathing = breathing;
        Temperature = temperature;
        Hydration = hydration;
        Pressure = pressure;
        Light = light;
        Random = random;
    }

    internal float Breathing { get; }

    internal float Temperature { get; }

    internal float Hydration { get; }

    internal float Pressure { get; }

    internal float Light { get; }

    internal float Random { get; }
}

/// <summary>One condition's running time against its damage limit.</summary>
internal sealed class PlantConditionView
{
    internal PlantConditionView(string state, string words, bool active, float? timeS, float? damageAfterS,
        bool damaging)
    {
        State = state;
        Words = words;
        Active = active;
        TimeS = timeS;
        DamageAfterS = damageAfterS > 0f ? damageAfterS : null;
        Damaging = damaging;
        DamageInS = active && !damaging && timeS != null && damageAfterS > 0f
            ? System.Math.Round(System.Math.Max(0.0, damageAfterS.Value - timeS.Value), 1)
            : null;
    }

    public string State { get; }

    public string Words { get; }

    public bool Active { get; }

    /// <summary>
    /// The condition's running time: a second in it adds a second, a second out of it takes five off.
    /// </summary>
    public float? TimeS { get; }

    public float? DamageAfterS { get; }

    public bool Damaging { get; }

    public double? DamageInS { get; }
}

/// <summary>
/// A plant's damage: 0 unharmed, 1 dead (a mature plant turns to its dead stage, any other is destroyed).
/// </summary>
internal sealed class PlantHealthView
{
    internal PlantHealthView(double damageRatio, int healthPercent, float total, float max, DamagePartsView parts)
    {
        DamageRatio = damageRatio;
        HealthPercent = healthPercent;
        Total = total;
        Max = max;
        Brute = parts.Brute;
        Burn = parts.Burn;
        Oxygen = parts.Oxygen;
        Hydration = parts.Hydration;
        Toxic = parts.Toxic;
        Starvation = parts.Starvation;
        Radiation = parts.Radiation;
        Stun = parts.Stun;
        Decay = parts.Decay;
    }

    public double DamageRatio { get; }

    public int HealthPercent { get; }

    public float Total { get; }

    public float Max { get; }

    public float Brute { get; }

    public float Burn { get; }

    public float Oxygen { get; }

    public float Hydration { get; }

    public float Toxic { get; }

    public float Starvation { get; }

    public float Radiation { get; }

    public float Stun { get; }

    public float Decay { get; }
}

internal sealed class PlantHarvestView
{
    internal PlantHarvestView(HarvestCounts counts, HarvestForecastView? forecast, float nutritionEach)
    {
        Quantity = counts.Quantity;
        Max = counts.Max;
        Seeds = counts.Seeds;
        FertiliserBonus = counts.FertiliserBonus;
        Forecast = forecast;
        NutritionEach = nutritionEach;
    }

    public int Quantity { get; }

    public int Max { get; }

    public int Seeds { get; }

    public float FertiliserBonus { get; }

    /// <summary>What the next harvest will be when the plant first matures, from the stress recorded so far.</summary>
    public HarvestForecastView? Forecast { get; }

    public float NutritionEach { get; }
}

internal sealed class HarvestCounts
{
    internal HarvestCounts(int quantity, int max, int seeds, float fertiliserBonus)
    {
        Quantity = quantity;
        Max = max;
        Seeds = seeds;
        FertiliserBonus = fertiliserBonus;
    }

    internal int Quantity { get; }

    internal int Max { get; }

    internal int Seeds { get; }

    internal float FertiliserBonus { get; }
}

internal sealed class HarvestForecastView
{
    internal HarvestForecastView(double expected, double stress)
    {
        Expected = expected;
        Stress = stress;
    }

    /// <summary>The fraction is a chance of one more.</summary>
    public double Expected { get; }

    /// <summary>
    /// Each point costs resilience (0.2) of one item: time in a bad condition over its limit, plus light.
    /// </summary>
    public double Stress { get; }
}

internal sealed class PlantFertiliserView
{
    internal PlantFertiliserView(bool fertilised, float? growthBoost, float harvestBonus,
        WaitingFertiliserView? waiting)
    {
        Fertilised = fertilised;
        GrowthBoost = growthBoost;
        HarvestBonus = harvestBonus;
        Waiting = waiting;
    }

    public bool Fertilised { get; }

    public float? GrowthBoost { get; }

    public float HarvestBonus { get; }

    /// <summary>In the tray's fertiliser slot: a perennial takes one cycle of it after each harvest.</summary>
    public WaitingFertiliserView? Waiting { get; }
}

internal sealed class WaitingFertiliserView
{
    internal WaitingFertiliserView(ThingId referenceId, string? prefabName, float growthSpeed, float harvestBoost,
        float cycles)
    {
        ReferenceId = referenceId;
        PrefabName = prefabName;
        GrowthSpeed = growthSpeed;
        HarvestBoost = harvestBoost;
        Cycles = cycles;
    }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public float GrowthSpeed { get; }

    public float HarvestBoost { get; }

    public float Cycles { get; }
}

internal sealed class PlantLightView
{
    internal PlantLightView(float exposure, bool lit, float? litRatio, float? darkRatio, float? stress)
    {
        Exposure = exposure;
        Lit = lit;
        LitRatio = litRatio;
        DarkRatio = darkRatio;
        Stress = stress;
    }

    /// <summary>
    /// A Hydroponics Tray: 0.8 under a working grow light, plus the sun's share. Above 0.01 counts as lit.
    /// </summary>
    public float Exposure { get; }

    public bool Lit { get; }

    public float? LitRatio { get; }

    public float? DarkRatio { get; }

    /// <summary>Light efficiency is 1 / (1 + stress).</summary>
    public float? Stress { get; }
}

/// <summary>The air the plant breathes; pressure is gases only, as the plant reads it.</summary>
internal sealed class PlantAirView
{
    internal PlantAirView(float pressureKpa, float temperatureK, double totalMol, Dictionary<string, float?> ratios)
    {
        PressureKpa = pressureKpa;
        TemperatureK = temperatureK;
        TotalMol = totalMol;
        Ratios = ratios;
    }

    public float PressureKpa { get; }

    public float TemperatureK { get; }

    public double TotalMol { get; }

    /// <summary>Share of all moles, gases and liquids, as the plant computes it (GasMixture.GetGasTypeRatio).</summary>
    public Dictionary<string, float?> Ratios { get; }
}

/// <summary>The tray's water: the plant drinks only while the network holds 1 mol of liquid, and only water.</summary>
internal sealed class PlantWaterView
{
    internal PlantWaterView(double liquidMol, double waterMol, float temperatureK)
    {
        LiquidMol = liquidMol;
        WaterMol = waterMol;
        TemperatureK = temperatureK;
    }

    public double LiquidMol { get; }

    public double WaterMol { get; }

    public float TemperatureK { get; }
}

internal sealed class PlantNeedsView
{
    internal PlantNeedsView(string? profile, PlantBands bands, PlantGases gases, PlantDaily daily)
    {
        Profile = profile;
        TemperatureK = bands.TemperatureK;
        PressureKpa = bands.PressureKpa;
        TakesIn = gases.TakesIn;
        GivesOut = gases.GivesOut;
        WaterMolPerTick = daily.WaterMolPerTick;
        LightPerDayS = daily.LightPerDayS;
        DarkPerDayS = daily.DarkPerDayS;
        Harmful = gases.Harmful;
    }

    public string? Profile { get; }

    public BandView TemperatureK { get; }

    public BandView PressureKpa { get; }

    public List<InhaledGasView> TakesIn { get; }

    public List<ExhaledGasView> GivesOut { get; }

    public float? WaterMolPerTick { get; }

    public float LightPerDayS { get; }

    public float DarkPerDayS { get; }

    public List<HarmfulGasView> Harmful { get; }
}

internal sealed class PlantBands
{
    internal PlantBands(BandView temperatureK, BandView pressureKpa)
    {
        TemperatureK = temperatureK;
        PressureKpa = pressureKpa;
    }

    internal BandView TemperatureK { get; }

    internal BandView PressureKpa { get; }
}

internal sealed class PlantGases
{
    internal PlantGases(List<InhaledGasView> takesIn, List<ExhaledGasView> givesOut, List<HarmfulGasView> harmful)
    {
        TakesIn = takesIn;
        GivesOut = givesOut;
        Harmful = harmful;
    }

    internal List<InhaledGasView> TakesIn { get; }

    internal List<ExhaledGasView> GivesOut { get; }

    internal List<HarmfulGasView> Harmful { get; }
}

internal sealed class PlantDaily
{
    internal PlantDaily(float? waterMolPerTick, float lightPerDayS, float darkPerDayS)
    {
        WaterMolPerTick = waterMolPerTick;
        LightPerDayS = lightPerDayS;
        DarkPerDayS = darkPerDayS;
    }

    internal float? WaterMolPerTick { get; }

    internal float LightPerDayS { get; }

    internal float DarkPerDayS { get; }
}

/// <summary>Ideal: full speed. Survivable: efficiency falls to 0 at its edges.</summary>
internal sealed class BandView
{
    internal BandView(float[] ideal, float[] survivable)
    {
        Ideal = ideal;
        Survivable = survivable;
    }

    public float[] Ideal { get; }

    public float[] Survivable { get; }
}

internal sealed class InhaledGasView
{
    internal InhaledGasView(string gas, float molPerTick, float needsRatio, float? ratioNow)
    {
        Gas = gas;
        MolPerTick = molPerTick;
        NeedsRatio = needsRatio;
        RatioNow = ratioNow;
    }

    public string Gas { get; }

    public float MolPerTick { get; }

    /// <summary>It takes the gas in full once the gas is at least this share of the air, less below.</summary>
    public float NeedsRatio { get; }

    public float? RatioNow { get; }
}

internal sealed class ExhaledGasView
{
    internal ExhaledGasView(string gas, float molPerTick)
    {
        Gas = gas;
        MolPerTick = molPerTick;
    }

    public string Gas { get; }

    public float MolPerTick { get; }
}

internal sealed class HarmfulGasView
{
    internal HarmfulGasView(string gas, float limitKpa, float? nowKpa)
    {
        Gas = gas;
        LimitKpa = limitKpa;
        NowKpa = nowKpa;
    }

    public string Gas { get; }

    /// <summary>Above this partial pressure the harmful gas timer runs.</summary>
    public float LimitKpa { get; }

    public float? NowKpa { get; }
}

internal sealed class PlantRecordView
{
    internal PlantRecordView(float ageS, float dryS, float coldS, float hotS, PressureTimes pressure)
    {
        AgeS = ageS;
        DryS = dryS;
        ColdS = coldS;
        HotS = hotS;
        SuffocatedS = pressure.SuffocatedS;
        LowPressureS = pressure.LowPressureS;
        HighPressureS = pressure.HighPressureS;
        PollutedS = pressure.PollutedS;
    }

    public float AgeS { get; }

    public float DryS { get; }

    public float ColdS { get; }

    public float HotS { get; }

    public float SuffocatedS { get; }

    public float LowPressureS { get; }

    public float HighPressureS { get; }

    public float PollutedS { get; }
}

/// <summary>A plant's recorded seconds without air, at low or high pressure, and in harmful gas.</summary>
internal sealed class PressureTimes
{
    internal PressureTimes(float suffocatedS, float lowPressureS, float highPressureS, float pollutedS)
    {
        SuffocatedS = suffocatedS;
        LowPressureS = lowPressureS;
        HighPressureS = highPressureS;
        PollutedS = pollutedS;
    }

    internal float SuffocatedS { get; }

    internal float LowPressureS { get; }

    internal float HighPressureS { get; }

    internal float PollutedS { get; }
}

internal sealed class PlantForecastView
{
    internal PlantForecastView(float? growthRate, double? nextStageS, double? harvestInS, double? seedsInS,
        RegrowTimes regrow)
    {
        GrowthRate = growthRate;
        NextStageS = nextStageS;
        HarvestInS = harvestInS;
        SeedsInS = seedsInS;
        RegrowS = regrow.AtRate;
        RegrowFullSpeedS = regrow.FullSpeed;
    }

    public float? GrowthRate { get; }

    public double? NextStageS { get; }

    public double? HarvestInS { get; }

    public double? SeedsInS { get; }

    /// <summary>
    /// A perennial's time back to fruit after a harvest, at today's efficiency and waiting fertiliser.
    /// </summary>
    public double? RegrowS { get; }

    public double? RegrowFullSpeedS { get; }
}

internal sealed class RegrowTimes
{
    internal RegrowTimes(double? atRate, double? fullSpeed)
    {
        AtRate = atRate;
        FullSpeed = fullSpeed;
    }

    internal double? AtRate { get; }

    internal double? FullSpeed { get; }
}
