#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Genetics;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// Plants: every plant growing in a tray, planter or hydroponics station, read straight from the Plant objects: a
/// plain Hydroponics Tray has no data port, so device logic cannot see its plant. Read only.
///
/// How a plant grows (Plant.OnLifeTick, once per 0.5 s game tick with the tick's real duration): the current stage's
/// progress Plant._stageTime gains deltaTime * lifeRequirements.GrowthEfficiency() * FertilizerBoost, and the plant
/// moves to the next stage once that passes the stage's Length. A Length below 0 never ends (the seeding stage).
/// Growth efficiency is breathing * light * temperature * hydration * pressure * a fixed per-plant random factor
/// (0.98 to 1.02) * the GrowthSpeedMultiplier gene (PlantLifeRequirements.GrowthEfficiency). Plants made from
/// plants.xml get an extra 1 s stage 0 in front of the XML stages (ThingImporter.CreatePlant).
///
/// Which plants: Plant.AllPlants is what the game ticks, planted plants until they reach their last (dead) stage. A
/// dead plant is still in its tray, so plants are also found among every dynamic thing (OcclusionManager
/// .AllDynamicThings), planted ones only unless unplanted ones are asked for too.
/// </summary>
internal static class Plants
{
    /// <summary>The game's clock as plants read it: game time, the day's length, and whether growth is sped up.</summary>
    internal static PlantClock Clock() =>
        new PlantClock(GameManager.GameTime, OrbitalSimulation.GetDayLengthSeconds(), Plant.CustomPlantGrowthSpeed);

    /// <summary>The plant with this id, or refused plant_not_found.</summary>
    internal static Plant One(string id)
    {
        if (long.TryParse(id, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture,
                out long referenceId) && Thing.Find(referenceId) is Plant plant && plant != null)
        {
            return plant;
        }

        throw ApiErrors.Refused("plant_not_found", $"find plant: {id} is no plant.");
    }

    /// <summary>Every plant, by reference id.</summary>
    internal static List<Plant> All(bool includeUnplanted)
    {
        HashSet<Plant> growing = new HashSet<Plant>();
        foreach (Plant plant in Plant.AllPlants)
        {
            if (plant != null)
            {
                growing.Add(plant);
            }
        }

        List<Plant> plants = new List<Plant>();
        HashSet<Plant> listed = new HashSet<Plant>();
        List<DynamicThing> things = Pools.Snapshot(OcclusionManager.AllDynamicThings);
        foreach (DynamicThing thing in things)
        {
            if (thing is Plant plant && !plant.IsCursor && !plant.IsBeingDestroyed &&
                (includeUnplanted || plant.IsPlanted || growing.Contains(plant)) && listed.Add(plant))
            {
                plants.Add(plant);
            }
        }

        foreach (Plant plant in growing)
        {
            if (!plant.IsBeingDestroyed && listed.Add(plant))
            {
                plants.Add(plant);
            }
        }

        plants.Sort(static (a, b) => a.ReferenceId.CompareTo(b.ReferenceId));
        return plants;
    }
}

/// <summary>The game's clock as plants read it.</summary>
internal sealed record PlantClock(float GameTimeS, int DayLengthS, bool DebugFastGrowth);
