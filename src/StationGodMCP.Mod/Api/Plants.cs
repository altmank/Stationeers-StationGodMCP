#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Genetics;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api;

/// <summary>
/// plants: every plant growing in a tray, planter or hydroponics station, read straight from the Plant objects: a
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
/// .AllDynamicThings), planted ones only unless include_unplanted. Every plant is listed in short (PlantBriefView)
/// unless verbose; one plant (reference_id) is always given whole.
/// </summary>
internal static class PlantsApi
{
    internal static PlantsView Handle(Args args)
    {
        bool includeUnplanted = args.OptionalBool("include_unplanted") ?? false;
        ThingId? only = args.OptionalThingId("reference_id");
        bool whole = only.HasValue || (args.OptionalBool("verbose") ?? false);
        List<Plant> plants = only.HasValue ? One(only.Value) : All(includeUnplanted);
        plants.Sort(static (a, b) => a.ReferenceId.CompareTo(b.ReferenceId));
        List<object> views = new List<object>(plants.Count);
        foreach (Plant plant in plants)
        {
            PlantView view = PlantReader.Read(plant);
            views.Add(whole ? view : new PlantBriefView(view));
        }

        return new PlantsView(GameManager.GameTime, OrbitalSimulation.GetDayLengthSeconds(),
            Plant.CustomPlantGrowthSpeed, views);
    }

    private static List<Plant> One(ThingId id)
    {
        if (!(Thing.Find(id.Value) is Plant plant) || plant == null)
        {
            throw ApiErrors.Refused("plant_not_found", $"Nothing with reference id {id} is a plant.");
        }

        return new List<Plant> { plant };
    }

    private static List<Plant> All(bool includeUnplanted)
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

        return plants;
    }
}
