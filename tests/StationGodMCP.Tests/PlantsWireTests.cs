#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// plants: the old StationApi.Plants shape against the new views, with the renames each test lists.
/// health.ratio (1 unharmed) becomes damage_ratio (0 unharmed), a change of meaning as well as name: the old shape
/// here carries the damage value under the old key, so the test proves the key's place, and health_percent is added.
/// The temperatures move from Celsius to kelvin the same way: the old shape carries the kelvin values.
/// </summary>
public sealed class PlantsWireTests
{
    private static readonly Dictionary<string, string> Renames = new Dictionary<string, string>
    {
        ["plants[].health.ratio"] = "damage_ratio",
        ["plants[].conditions[].seconds"] = "time_s",
        ["plants[].atmosphere.total_moles"] = "total_mol",
        ["plants[].water.liquid_moles"] = "liquid_mol",
        ["plants[].water.water_moles"] = "water_mol",
        ["plants[].needs.takes_in[].moles_per_tick"] = "mol_per_tick",
        ["plants[].needs.gives_out[].moles_per_tick"] = "mol_per_tick",
        ["plants[].needs.water_moles_per_tick"] = "water_mol_per_tick",
        ["game_time"] = "game_time_s",
        ["plants[].efficiency.growth"] = "growth_factor",
        ["plants[].efficiency.breathing"] = "breathing_factor",
        ["plants[].efficiency.temperature"] = "temperature_factor",
        ["plants[].efficiency.hydration"] = "hydration_factor",
        ["plants[].efficiency.pressure"] = "pressure_factor",
        ["plants[].efficiency.light"] = "light_factor",
        ["plants[].efficiency.gene_speed"] = "gene_speed_factor",
        ["plants[].atmosphere.temperature_c"] = "temperature_k",
        ["plants[].water.temperature_c"] = "temperature_k",
        ["plants[].needs.temperature_c"] = "temperature_k"
    };

    [Fact]
    public void PlantsSameWireAfterRenames()
    {
        WireCheck.SameAfterRenames(OldShape(), NewShape(), Renames, "plants[].health.health_percent");
    }

    private static object OldShape() => new
    {
        game_time = 1234.5f,
        day_length_s = 1200,
        debug_fast_growth = false,
        plants = new List<object>
        {
            new
            {
                reference_id = "70", prefab_name = "SeedBag_Tomato", name = "Tomato", display_name = "Tomato_Tray 1",
                planted = true,
                tray = new
                {
                    reference_id = "71", prefab_name = "StructureHydroponicsTray", display_name = "Tray 1",
                    lit_by_grow_light = true
                },
                stage = new
                {
                    index = 2, count = 5, first_mature_index = 3, first_seeding_index = 4, dead_index = -1,
                    mature_index = 3, seeding_index = 4, progress_s = (float?)12.5f, length_s = (float?)60f
                },
                stages = new List<object>
                {
                    new { index = 0, length_s = 1f, mature = false, seeding = false, dead = false }
                },
                maturity_ratio = 0.5, seeding_ratio = 0.0, mature = false, seeding = false, dead = false,
                perennial = (bool?)false, ready_to_harvest = false,
                efficiency = new
                {
                    growth = (float?)0.9f, growth_percent = (ushort)90, breathing = 1f, temperature = 1f,
                    hydration = 0.9f, pressure = 1f, light = 1f, random_factor = 1.01f, gene_speed = (float?)1f
                },
                problems = new List<string> { "dry" },
                active_states = new List<string> { "dry", "in light" },
                conditions = new List<object>
                {
                    new
                    {
                        state = "Dehydrated", words = "dry", active = true, seconds = (float?)30f,
                        damage_after_s = (float?)120f, damaging = false, damage_in_s = (double?)90.0
                    }
                },
                can_heal = (bool?)false,
                health = new
                {
                    ratio = 0.25, total = 25f, max = 100f, brute = 0f, burn = 0f, oxygen = 0f, hydration = 25f,
                    toxic = 0f, starvation = 0f, radiation = 0f, stun = 0f, decay = 0f
                },
                harvest = new
                {
                    quantity = 0, max = 3, seeds = 1, fertiliser_bonus = 0f,
                    forecast = new { expected = 2.8, stress = 1.0 }, nutrition_each = 12f
                },
                fertiliser = new
                {
                    fertilised = false, growth_boost = (float?)1f, harvest_bonus = 0f,
                    waiting = new
                    {
                        reference_id = "72", prefab_name = "Fertilizer", growth_speed = 1.5f, harvest_boost = 1f,
                        cycles = 2f
                    }
                },
                light = new
                {
                    exposure = 0.8f, lit = true, lit_ratio = (float?)0.5f, dark_ratio = (float?)0.5f,
                    stress = (float?)0f
                },
                atmosphere = new
                {
                    pressure_kpa = 101f, temperature_c = 22f, total_moles = 40.0,
                    ratios = new Dictionary<string, float?> { ["oxygen"] = 0.2f, ["carbon_dioxide"] = null }
                },
                water = new { liquid_moles = 100.0, water_moles = 100.0, temperature_c = 20f },
                needs = new
                {
                    profile = "Tomato",
                    temperature_c = new { ideal = new[] { 15f, 30f }, survivable = new[] { 5f, 40f } },
                    pressure_kpa = new { ideal = new[] { 50f, 150f }, survivable = new[] { 20f, 200f } },
                    takes_in = new List<object>
                    {
                        new
                        {
                            gas = "carbon dioxide", moles_per_tick = 0.01f, needs_ratio = 0.01f,
                            ratio_now = (float?)0.02f
                        }
                    },
                    gives_out = new List<object> { new { gas = "oxygen", moles_per_tick = 0.01f } },
                    water_moles_per_tick = (float?)0.002f,
                    light_per_day_s = 300f,
                    dark_per_day_s = 100f,
                    harmful = new List<object> { new { gas = "pollutant", limit_kpa = 1f, now_kpa = (float?)0f } }
                },
                record = new
                {
                    age_s = 500f, dry_s = 30f, cold_s = 0f, hot_s = 0f, suffocated_s = 0f, low_pressure_s = 0f,
                    high_pressure_s = 0f, polluted_s = 0f
                },
                genes = new SortedDictionary<string, float> { ["DarkPerDay"] = 0.1f, ["WaterUsage"] = -0.2f },
                forecast = new
                {
                    growth_rate = (float?)0.9f, next_stage_s = (double?)52.8, harvest_in_s = (double?)100.0,
                    seeds_in_s = (double?)200.0, regrow_s = (double?)null, regrow_full_speed_s = (double?)null
                }
            }
        },
        count = 1
    };

    private static PlantsView NewShape()
    {
        PlantIdentity identity = new PlantIdentity(new ThingView(new ThingId(70), "SeedBag_Tomato", "Tomato_Tray 1"),
            "Tomato", true,
            new PlantTrayView(new ThingView(new ThingId(71), "StructureHydroponicsTray", "Tray 1"), true));
        PlantGrowth growth = new PlantGrowth(
            new PlantStageView(2, 5, new StageMarks(3, 4, -1, 3, 4), 12.5f, 60f),
            new List<StageView> { new StageView(0, 1f, false, false, false) }, 0.5, 0.0,
            new PlantPhase(false, false, false, false, false));
        PlantCare care = new PlantCare(
            new PlantEfficiencyView(0.9f, 90, new PlantFactors(1f, 1f, 0.9f, 1f, 1f, 1.01f), 1f),
            new PlantStates(new List<string> { "dry" }, new List<string> { "dry", "in light" }),
            new List<PlantConditionView> { new PlantConditionView("Dehydrated", "dry", true, 30f, 120f, false) },
            false,
            new PlantHealthView(0.25, 75, 25f, 100f, new DamagePartsView(0f, 0f, 0f, 25f, 0f, 0f, 0f, 0f, 0f)));
        PlantSupply supply = new PlantSupply(
            new PlantHarvestView(new HarvestCounts(0, 3, 1, 0f), new HarvestForecastView(2.8, 1.0), 12f),
            new PlantFertiliserView(false, 1f, 0f,
                new WaitingFertiliserView(new ThingId(72), "Fertilizer", 1.5f, 1f, 2f)),
            new PlantLightView(0.8f, true, 0.5f, 0.5f, 0f),
            new PlantAirView(101f, 22f, 40.0,
                new Dictionary<string, float?> { ["oxygen"] = 0.2f, ["carbon_dioxide"] = null }),
            new PlantWaterView(100.0, 100.0, 20f),
            new PlantNeedsView("Tomato",
                new PlantBands(new BandView(new[] { 15f, 30f }, new[] { 5f, 40f }),
                    new BandView(new[] { 50f, 150f }, new[] { 20f, 200f })),
                new PlantGases(
                    new List<InhaledGasView> { new InhaledGasView("carbon dioxide", 0.01f, 0.01f, 0.02f) },
                    new List<ExhaledGasView> { new ExhaledGasView("oxygen", 0.01f) },
                    new List<HarmfulGasView> { new HarmfulGasView("pollutant", 1f, 0f) }),
                new PlantDaily(0.002f, 300f, 100f)));
        PlantHistory history = new PlantHistory(
            new PlantRecordView(500f, 30f, 0f, 0f, new PressureTimes(0f, 0f, 0f, 0f)),
            new SortedDictionary<string, float> { ["DarkPerDay"] = 0.1f, ["WaterUsage"] = -0.2f },
            new PlantForecastView(0.9f, 52.8, 100.0, 200.0, new RegrowTimes(null, null)));
        return new PlantsView(1234.5f, 1200, false,
            new List<PlantView> { new PlantView(identity, growth, care, supply, history) });
    }
}
