#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// player_vitals, consumables, atmosphere_contents and water_sources: the old StationApi.Survival shapes against the
/// new views, with the renames each test lists. Each note is compared as the new text: it names renamed
/// fields.
/// </summary>
public sealed class SurvivalWireTests
{
    private static readonly string[] WaterKeys =
    {
        "liquid_moles:liquid_mol", "liquid_litres:liquid_l", "polluted_moles:polluted_mol",
        "polluted_litres:polluted_l", "steam_moles:steam_mol", "steam_litres_if_condensed:steam_if_condensed_l"
    };

    private static object OldWater() => new
    {
        liquid_moles = 55.5, liquid_litres = 1.0, hydration = 277.5, polluted_moles = 0.0, polluted_litres = 0.0,
        steam_moles = 2.0, steam_litres_if_condensed = 0.036
    };

    private static WaterView NewWater() => new WaterView(new WaterAmount(55.5, 1.0), 277.5, new WaterAmount(0.0, 0.0),
        new WaterAmount(2.0, 0.036));

    private static Dictionary<string, string> WaterRenames(string prefix)
    {
        Dictionary<string, string> renames = new Dictionary<string, string>();
        foreach (string pair in WaterKeys)
        {
            string[] parts = pair.Split(':');
            renames[prefix + parts[0]] = parts[1];
        }

        return renames;
    }

    private static object OldHeldIn() => new
    {
        reference_id = "20", prefab_name = "StructureStorageLocker", display_name = "Locker", slot_index = 3,
        slot_name = "Slot 3"
    };

    private static ItemFields NewFields(long id, string prefab, double quantity) => new ItemFields(
        new ThingView(new ThingId(id), prefab, "Thing " + id), quantity, 10.0,
        new ItemPlace("stored", null,
            new List<HeldInView> { new HeldInView(new ThingView(new ThingId(20), "StructureStorageLocker", "Locker"),
                3, "Slot 3") },
            new PositionView(1.0, 2.0, 3.0), 4.5));

    [Fact]
    public void PlayerVitalsSameWireAfterRenames()
    {
        object OldDrain(object factors) => new
        {
            per_second = 0.01, per_hour = 36.0, awake_per_second = 0.02, awake_per_hour = 72.0,
            seconds_left = (double?)5000.0, seconds_left_awake = (double?)2500.0, factors
        };

        var old = new
        {
            reference_id = "1", display_name = "Player", nutrition = 50f, nutrition_capacity = 50f, hydration = 5f,
            hydration_capacity = 5f, food_quality = 0.5f, food_quality_multiplier = 1f, mood = 0.8f,
            sleeping = false, brain_online = true, robot = false, life_suspended = false, dead = false,
            suit_worn = true, helmet_closed = (bool?)true,
            thirst_temperature = new { kelvin = (double?)293.15, celsius = (double?)20.0, source = "suit" },
            difficulty = new { id = "Normal", hunger_rate = 1f, hydration_rate = 1f, offline_metabolism = 0.1f },
            hunger = OldDrain(new { mood = 1f, metabolism = 1f, sleeping = 1f }),
            thirst = OldDrain(new { temperature = 1.2f, metabolism = 1f, sleeping = 1f }),
            warnings = new
            {
                nutrition_warning = 2f, nutrition_critical = 1f, hydration_warning = 2f, hydration_critical = 1f
            },
            game_tick_seconds = 0.5f
        };

        DrainView NewDrain(object factors) => new DrainView(50f, 0.02, 0.01, factors);
        PlayerVitalsView view = new PlayerVitalsView(new ThingView(new ThingId(1), null, "Player"),
            new BodyStores(50f, 50f, 5f, 5f, 0.5f, 1f, 0.8f),
            new BodyState(false, true, false, false, false, true, true),
            new VitalsRates(new ThirstTemperatureView(293.15, "suit"),
                NewDrain(new HungerFactorsView(1f, 1f, 1f)), NewDrain(new ThirstFactorsView(1.2f, 1f, 1f)), 0.5f),
            new DifficultyView("Normal", 1f, 1f, 0.1f), new VitalsWarningsView(2f, 1f, 2f, 1f));
        Dictionary<string, string> renames = new Dictionary<string, string>
        {
            ["thirst_temperature.kelvin"] = "temperature_k",
            ["hunger.seconds_left"] = "time_left_s",
            ["hunger.seconds_left_awake"] = "time_left_awake_s",
            ["thirst.seconds_left"] = "time_left_s",
            ["thirst.seconds_left_awake"] = "time_left_awake_s",
            ["game_tick_seconds"] = "game_tick_s"
        };
        WireCheck.SameAfterDrops(old, view, renames, new[] { "thirst_temperature.celsius" });
    }

    [Fact]
    public void ConsumablesSameWireAfterRenames()
    {
        var old = new
        {
            food = new List<object>
            {
                new
                {
                    reference_id = "30", prefab_name = "ItemCerealBar", display_name = "Thing 30", quantity = 2.0,
                    max_quantity = (double?)10.0, location = "stored", carried_by = (string?)null,
                    held_in = new List<object> { OldHeldIn() }, position = new { x = 1.0, y = 2.0, z = 3.0 },
                    distance_m = (double?)4.5, nutrition_each = 5.0, nutrition = 10.0, food_quality = "Good",
                    decay_seconds_left = (int?)600, in_package = "40", package_name = "Box"
                }
            },
            drinks = new List<object>
            {
                new
                {
                    reference_id = "31", prefab_name = "ItemWaterBottle", display_name = "Thing 31", quantity = 1.5,
                    max_quantity = (double?)10.0, location = "stored", carried_by = (string?)null,
                    held_in = new List<object> { OldHeldIn() }, position = new { x = 1.0, y = 2.0, z = 3.0 },
                    distance_m = (double?)4.5, litres = 1.5, hydration = 7.5, in_package = (string?)null,
                    package_name = (string?)null
                }
            },
            packages = new List<object>
            {
                new
                {
                    reference_id = "40", prefab_name = "ItemCardboardBox", display_name = "Box", location = "stored",
                    carried_by = (string?)null, held_in = new List<object> { OldHeldIn() },
                    position = new { x = 1.0, y = 2.0, z = 3.0 }, distance_m = (double?)4.5,
                    runtime_type = "CardboardBox",
                    contents = new List<object>
                    {
                        new { prefab_name = "ItemCerealBar", display_name = "Thing 30", items = 1, quantity = 2.0 }
                    },
                    nutrition = 10.0, litres = 0.0, hydration = 0.0
                }
            },
            not_counted = new List<object>
            {
                new
                {
                    prefab_name = "SeedBag_Corn", display_name = "Corn Seeds", reason = "seed", items = 1,
                    quantity = 3.0
                }
            },
            totals = new
            {
                nutrition = 10.0, nutrition_in_packages = 10.0, litres = 1.5, litres_in_packages = 0.0,
                hydration = 7.5, hydration_in_packages = 0.0, food_items = 1, drink_items = 1
            },
            local_player = (object?)null
        };
        Dictionary<string, string> renames = new Dictionary<string, string>
        {
            ["food[].decay_seconds_left"] = "decay_time_left_s",
            ["drinks[].litres"] = "liquid_l",
            ["packages[].litres"] = "liquid_l",
            ["totals.litres"] = "liquid_l",
            ["totals.litres_in_packages"] = "liquid_in_packages_l"
        };
        WireCheck.SameAfterRenames(old, NewConsumables(), renames);
    }

    private static ConsumablesView NewConsumables()
    {
        ItemPlace place = NewFields(40, "ItemCardboardBox", 1).Place;
        return new ConsumablesView(
            new List<FoodView>
            {
                new FoodView(NewFields(30, "ItemCerealBar", 2.0), new FoodValue(5.0, 10.0, "Good", 600),
                    new PackageRef(new ThingId(40), "Box"))
            },
            new List<DrinkView> { new DrinkView(NewFields(31, "ItemWaterBottle", 1.5), 1.5, 7.5, PackageRef.None) },
            new List<PackageView>
            {
                new PackageView(new ThingView(new ThingId(40), "ItemCardboardBox", "Box"), place, "CardboardBox",
                    new List<PackedView> { new PackedView("ItemCerealBar", "Thing 30", 1, 2.0) },
                    new ConsumableAmounts(10.0, 0.0, 0.0))
            },
            new List<NotCountedView> { new NotCountedView("SeedBag_Corn", "Corn Seeds", "seed", 1, 3.0) },
            new ConsumableTotalsView(new ConsumableAmounts(10.0, 1.5, 7.5), new ConsumableAmounts(10.0, 0.0, 0.0), 1,
                1),
            null);
    }

    [Fact]
    public void AtmosphereContentsSameWireAfterRenames()
    {
        object owner = new
        {
            kind = "structure", reference_id = "50", prefab_name = "StructureTankSmall", display_name = "Tank",
            runtime_type = "Tank", position = new { x = 0.0, y = 0.0, z = 0.0 }, distance_m = (double?)null
        };
        var old = new
        {
            reference_id = "50",
            subject = owner,
            atmospheres = new List<object>
            {
                new
                {
                    source = "internal", owner, slot_index = (int?)null, slot_name = (string?)null,
                    atmosphere = new
                    {
                        reference_id = "51", volume_l = 100.0, pressure_kpa = 101.3, temperature_k = 293.15,
                        total_moles = 57.5, liquid_volume_l = 1.0,
                        contents = new List<object>
                        {
                            new
                            {
                                gas = "Water", display_name = "Water", state = "liquid", moles = 55.5,
                                litres = (double?)1.0
                            },
                            new
                            {
                                gas = "Steam", display_name = "Steam", state = "gas", moles = 2.0,
                                litres = (double?)null
                            }
                        },
                        water = OldWater()
                    }
                }
            },
            count = 1
        };
        Dictionary<string, string> renames = WaterRenames("atmospheres[].atmosphere.water.");
        renames["atmospheres[].atmosphere.total_moles"] = "total_mol";
        renames["atmospheres[].atmosphere.contents[].moles"] = "amount_mol";
        renames["atmospheres[].atmosphere.contents[].litres"] = "liquid_l";
        WireCheck.SameAfterRenames(old, NewContents(), renames);
    }

    private static AtmosphereContentsView NewContents()
    {
        StructureOwnerView owner = new StructureOwnerView("structure",
            new ThingView(new ThingId(50), "StructureTankSmall", "Tank"), "Tank", new PositionView(0.0, 0.0, 0.0),
            null);
        HeldAtmosphereView atmosphere = new HeldAtmosphereView(new ThingId(51),
            new AtmosphereState(100.0, 101.3, 293.15, 57.5, 1.0),
            new List<HeldGasView>
            {
                new HeldGasView("Water", "Water", true, 55.5, 1.0), new HeldGasView("Steam", "Steam", false, 2.0, null)
            },
            NewWater());
        return new AtmosphereContentsView(new ThingId(50), owner,
            new List<HeldAtmosphereEntryView> { new HeldAtmosphereEntryView("internal", owner, null, atmosphere) });
    }

    [Fact]
    public void ItemOwnerAndNetworkOwnerSameWire()
    {
        var oldItem = new
        {
            reference_id = "30", prefab_name = "ItemGasCanisterWater", display_name = "Thing 30", quantity = 1.0,
            max_quantity = (double?)10.0, location = "stored", carried_by = (string?)null,
            held_in = new List<object> { OldHeldIn() }, position = new { x = 1.0, y = 2.0, z = 3.0 },
            distance_m = (double?)4.5, kind = "item", runtime_type = "GasCanister"
        };
        WireCheck.Same(oldItem, new ItemOwnerView(NewFields(30, "ItemGasCanisterWater", 1.0), "GasCanister"));
        var oldNetwork = new
        {
            kind = "pipe_network", reference_id = "60", network_type = "PipeNetwork", content_type = "Liquid",
            pipe_count = 12,
            devices = new List<object>
            {
                new { reference_id = "61", prefab_name = "StructurePump", display_name = "Pump" }
            },
            position = new { x = 5.0, y = 0.0, z = 5.0 }, distance_m = (double?)7.1
        };
        WireCheck.Same(oldNetwork, new NetworkOwnerView(new ThingId(60), new NetworkKind("PipeNetwork", "Liquid", 12),
            new List<ThingView> { new ThingView(new ThingId(61), "StructurePump", "Pump") },
            new PositionView(5.0, 0.0, 5.0), 7.1));
    }

    [Fact]
    public void WaterSourcesSameWireAfterRenames()
    {
        object owner = new
        {
            kind = "structure", reference_id = "50", prefab_name = "StructureTankSmall", display_name = "Tank",
            runtime_type = "Tank", position = new { x = 0.0, y = 0.0, z = 0.0 }, distance_m = (double?)null
        };
        WaterSourcesView view = new WaterSourcesView(
            new List<WaterSourceView>
            {
                new WaterSourceView(true,
                    new StructureOwnerView("structure",
                        new ThingView(new ThingId(50), "StructureTankSmall", "Tank"), "Tank",
                        new PositionView(0.0, 0.0, 0.0), null),
                    new ThingId(51), new AtmosphereState(100.0, 101.3, 293.15, 0.0, 0.0), NewWater())
            },
            NewWater(), 1.0, null);
        var old = new
        {
            sources = new List<object>
            {
                new
                {
                    kind = "thing", owner, atmosphere_id = "51", volume_l = 100.0, pressure_kpa = 101.3,
                    temperature_k = 293.15, water = OldWater()
                }
            },
            count = 1,
            totals = OldWater(),
            min_moles = 1.0,
            local_player = (object?)null
        };
        Dictionary<string, string> renames = WaterRenames("sources[].water.");
        foreach (KeyValuePair<string, string> total in WaterRenames("totals."))
        {
            renames[total.Key] = total.Value;
        }

        renames["min_moles"] = "min_mol";
        WireCheck.SameAfterRenames(old, view, renames);
    }
}
