#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Chutes;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Objects.Pipes;
using Assets.Scripts.Util;
using Objects.Electrical;
using Objects.Rockets;
using Objects.Structures;
using Reagents;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Profiling;
using Trading.Waypoints;

namespace StationGodMCP.WorldModel;

/// <summary>
/// The world model (cost design A1): the one module that reads the game's global collections (the list is
/// tests/StationGodMCP.Tests/CostRules/GameCollections.txt). Calls ask it instead of copying the game's lists:
/// <list type="bullet">
/// <item>things by prefab and by class (ThingIndex: one index kept by hooks on the game's add and remove paths; a query
/// costs its matches, not the world, and answers exactly what the game's list for it holds);</item>
/// <item>the small lists (players, rockets, rooms, trader contacts, the space map, waypoints, growing plants, data
/// transmitters) and the load-time ones (prefabs, reagents, ingots), read in place, never copied;</item>
/// <item>the pools no class covers (pipe networks, atmospheres, atmospheric things), walked in place.</item>
/// </list>
/// Static data built once per load is in Prefabs. Main thread, as every caller is.
/// </summary>
internal static class World
{
    // ---- things, by prefab and by class (ThingIndex) ----

    /// <summary>The registered things (AllThings) whose prefab name the match may keep; callers apply the match.</summary>
    internal static List<Thing> Things(PrefabMatch match) => ThingIndex.ByPrefab(match, false);

    /// <summary>
    /// A class's candidates for a scan spread over frames: the index's bucket as it is now, membership unchecked (a
    /// scan checks each one with Holds as its turn comes). Proportional to the class, never the world.
    /// </summary>
    internal static List<Thing> Candidates(WorldClass worldClass) => ThingIndex.CandidatesOf(worldClass);

    /// <summary>Whether a scan's candidate is still a member of its class (in the class's game list).</summary>
    internal static bool Holds(WorldClass worldClass, Thing? thing) => ThingIndex.IsMember(worldClass, thing);

    /// <summary>Every registered thing (AllThings): cursors are never registered.</summary>
    internal static List<Thing> Things() => ThingIndex.OfClass(WorldClass.Thing);

    /// <summary>The dynamic things (AllDynamicThings) whose prefab name the match may keep; callers apply it.</summary>
    internal static List<DynamicThing> DynamicThings(PrefabMatch match) =>
        Typed<DynamicThing>(ThingIndex.ByPrefab(match, true));

    /// <summary>Every dynamic thing (AllDynamicThings).</summary>
    internal static List<DynamicThing> DynamicThings() => OfClass<DynamicThing>(WorldClass.Dynamic);

    /// <summary>Every registered structure (GridController.AllStructuresPool).</summary>
    internal static List<Structure> Structures() => OfClass<Structure>(WorldClass.Structure);

    /// <summary>Every registered frame (the Frame structures of AllStructuresPool).</summary>
    internal static List<Frame> Frames() => OfClass<Frame>(WorldClass.Frame);

    /// <summary>Every registered device (Device.AllDevices: cabled or not, powered or not).</summary>
    internal static List<Device> Devices() => OfClass<Device>(WorldClass.Device);

    /// <summary>Every item (the Items of AllDynamicThings), wherever it is.</summary>
    internal static List<Item> Items() => OfClass<Item>(WorldClass.Item);

    /// <summary>The items whose prefab name the match may keep; callers apply it.</summary>
    internal static List<Item> Items(PrefabMatch match) => Typed<Item>(ThingIndex.ByPrefab(match, true));

    /// <summary>Every plant item of AllDynamicThings, planted or not (GrowingPlants: what the game ticks).</summary>
    internal static List<Plant> Plants() => OfClass<Plant>(WorldClass.Plant);

    /// <summary>Every food and drink, and what rotten food becomes (INutrition, IHydration, DecayedFood items).</summary>
    internal static List<Item> Consumables() => OfClass<Item>(WorldClass.Consumable);

    /// <summary>Every landing pad centre.</summary>
    internal static List<LandingPadCenter> Pads() => OfClass<LandingPadCenter>(WorldClass.Pad);

    /// <summary>Every SDB silo.</summary>
    internal static List<Silo> Silos() => OfClass<Silo>(WorldClass.Silo);

    /// <summary>Every satellite dish.</summary>
    internal static List<SatelliteDish> Dishes() => OfClass<SatelliteDish>(WorldClass.Dish);

    /// <summary>Every structure that keeps a reagent mixture (furnaces, fabricators, recyclers, centrifuges, ...).</summary>
    internal static List<Structure> ReagentHolders() => OfClass<Structure>(WorldClass.ReagentHolder);

    private static List<TThing> OfClass<TThing>(WorldClass worldClass) where TThing : Thing =>
        Typed<TThing>(ThingIndex.OfClass(worldClass));

    // The index's answer as the class's type: a list of the matches, never of the world.
    private static List<TThing> Typed<TThing>(List<Thing> found) where TThing : Thing
    {
        List<TThing> typed = new List<TThing>(found.Count);
        for (int index = 0; index < found.Count; index++)
        {
            if (found[index] is TThing thing)
            {
                typed.Add(thing);
            }
        }

        return typed;
    }

    // ---- small lists, read in place ----

    /// <summary>Every human (players and their bodies): Human.AllHumans.</summary>
    internal static IReadOnlyList<Human> Humans => Human.AllHumans;

    /// <summary>Every rocket: Rocket.AllRockets.</summary>
    internal static IReadOnlyList<Rocket> Rockets => Rocket.AllRockets;

    /// <summary>Every room the game keeps, valid or not: Room.AllRooms.</summary>
    internal static IReadOnlyList<Room> Rooms => Room.AllRooms;

    /// <summary>The trader contacts in the sky: TraderContact.AllStationContacts.</summary>
    internal static IReadOnlyList<TraderContact> TraderContacts => TraderContact.AllStationContacts;

    /// <summary>The space map's nodes: SpaceMapNode.AllSpaceMapNodes.</summary>
    internal static IReadOnlyList<SpaceMapNode> SpaceMapNodes => SpaceMapNode.AllSpaceMapNodes;

    /// <summary>The landing waypoints: WaypointManager.AllWaypoints.</summary>
    internal static IReadOnlyList<IWaypoint> Waypoints => WaypointManager.AllWaypoints;

    /// <summary>The plants the game ticks (planted, until their last stage): Plant.AllPlants.</summary>
    internal static IReadOnlyList<Plant> GrowingPlants => Plant.AllPlants;

    /// <summary>Every built data downlink and transmitter: RocketDataDownLink.AllITransmitDataNetworkDevices.</summary>
    internal static IReadOnlyList<ILogicable> DataTransmitters => RocketDataDownLink.AllITransmitDataNetworkDevices;

    // ---- load-time lists, read in place ----

    /// <summary>Every loaded prefab: Prefab.AllPrefabs (the catalogues built from it are in Prefabs).</summary>
    internal static IReadOnlyList<Thing> AllPrefabs => Prefab.AllPrefabs;

    /// <summary>Whether the game has loaded its prefabs.</summary>
    internal static bool PrefabsLoaded => Prefab.AllPrefabs != null && Prefab.AllPrefabs.Count > 0;

    /// <summary>Every reagent, in the game's order: Reagent.AllReagents.</summary>
    internal static IReadOnlyList<Reagent> Reagents => Reagent.AllReagents;

    /// <summary>Every ingot prefab: Ingot.AllIngotPrefabs.</summary>
    internal static IReadOnlyList<Ingot> IngotPrefabs => Ingot.AllIngotPrefabs;

    /// <summary>The electronic reader's recipe list, which the mod's own prefabs join at load (PrefabRegistrar).</summary>
    internal static List<RecipeReference> ReaderRecipes => ElectronicReader.AllRecipes;

    // ---- pools no class covers, walked in place ----

    /// <summary>Every pipe network the game lists.</summary>
    internal static DensePool<PipeNetwork>.ActiveEnumerable PipeNetworks() => PipeNetwork.AllPipeNetworks.Active();

    /// <summary>Every thing the atmospherics tick holds (things with an atmosphere of their own, and pipes).</summary>
    internal static DensePool<Thing>.ActiveEnumerable AtmosphericThings() =>
        AtmosphericsManager.AtmosphericThings.Active();

    /// <summary>
    /// The atmospheres (AtmosphericsManager.AllAtmospheres) keep takes, read under the pool's own lock (the
    /// atmospherics thread changes it, and locks it for each of its passes), with the milliseconds spent waiting for the
    /// lock. Only what is kept is listed.
    /// </summary>
    internal static List<Atmosphere> Atmospheres(Func<Atmosphere, bool> keep, out double lockWaitMs) =>
        Kept(AtmosphericsManager.AllAtmospheres, keep, out lockWaitMs);

    /// <summary>As Atmospheres, for PipeNetwork.AllPipeNetworks.</summary>
    internal static List<PipeNetwork> PipeNetworks(Func<PipeNetwork, bool> keep, out double lockWaitMs) =>
        Kept(PipeNetwork.AllPipeNetworks, keep, out lockWaitMs);

    /// <summary>
    /// A part of Atmospheres, without waiting (work over frames, A3): the count entries from start in the pool's order
    /// that keep takes, added to kept; ended when the pool has no entry past them. False, nothing added, when the
    /// atmospherics thread holds the pool's lock now (its pass goes on; the caller tries again in a later step). The
    /// pool may change between parts (an entry removed moves its last one into its place), so a reader of several
    /// parts may see an entry twice or miss one that moved: it is a reading of live facts, not a snapshot.
    /// </summary>
    internal static bool TryAtmospheres(int start, int count, Func<Atmosphere, bool> keep, List<Atmosphere> kept,
        out bool ended) =>
        TryKept(AtmosphericsManager.AllAtmospheres, start, count, keep, kept, out ended);

    /// <summary>As TryAtmospheres, for PipeNetwork.AllPipeNetworks.</summary>
    internal static bool TryPipeNetworks(int start, int count, Func<PipeNetwork, bool> keep, List<PipeNetwork> kept,
        out bool ended) =>
        TryKept(PipeNetwork.AllPipeNetworks, start, count, keep, kept, out ended);

    private static bool TryKept<T>(ConcurrentDensePool<T> pool, int start, int count, Func<T, bool> keep, List<T> kept,
        out bool ended)
        where T : class, IDensePoolable
    {
        using ProfScope reading = Prof.Scope(ProfId.PoolSnapshot);
        ended = false;
        if (!Monitor.TryEnter(pool))
        {
            return false;
        }

        try
        {
            int at = 0;
            int until = start + count;
            foreach (T entry in pool.Active())
            {
                if (at >= until)
                {
                    return true;
                }

                if (at++ >= start && entry != null && keep(entry))
                {
                    kept.Add(entry);
                }
            }

            ended = true;
        }
        finally
        {
            Monitor.Exit(pool);
        }

        return true;
    }

    private static List<T> Kept<T>(ConcurrentDensePool<T> pool, Func<T, bool> keep, out double lockWaitMs)
        where T : class, IDensePoolable
    {
        using ProfScope reading = Prof.Scope(ProfId.PoolSnapshot);
        List<T> kept = new List<T>();
        long asked = Stopwatch.GetTimestamp();
        lock (pool)
        {
            lockWaitMs = (Stopwatch.GetTimestamp() - asked) * 1000.0 / Stopwatch.Frequency;
            foreach (T entry in pool.Active())
            {
                if (entry != null && keep(entry))
                {
                    kept.Add(entry);
                }
            }
        }

        return kept;
    }
}
