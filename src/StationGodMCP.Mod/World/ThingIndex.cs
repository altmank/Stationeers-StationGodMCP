#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Chutes;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Objects.Pipes;
using Assets.Scripts.Util;
using HarmonyLib;
using Objects.Electrical;
using Objects.Structures;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Profiling;

namespace StationGodMCP.WorldModel;

/// <summary>
/// The classes the world index files things under (cost design A1), each answered from its own game list: a class's
/// number is its bit in the index (Pure/WorldIndex). Thing: every thing of OcclusionManager.AllThings; Dynamic: of
/// AllDynamicThings; the structure classes from GridController.AllStructuresPool; the device classes from
/// Device.AllDevices; the item classes from AllDynamicThings.
/// </summary>
internal enum WorldClass
{
    Thing = 0,
    Dynamic,
    Structure,
    Frame,
    Device,
    Item,
    Plant,
    Pad,
    Silo,
    Dish,
    Consumable,
    ReagentHolder
}

/// <summary>
/// The world index over the game's lists (Pure/WorldIndex): things by prefab name and by class (WorldClass).
/// <para>
/// In step with the lists by the game's own add and remove points (decompile Assets.Scripts/OcclusionManager.cs):
/// AllThings.Add only in OcclusionManager.Register(Thing) (which Thing.Create calls for every thing that is not a
/// cursor) and AllThings.Remove only in Deregister(Thing) (Thing.OnDestroy); AllDynamicThings.Add only in
/// Register(DynamicThing) and Remove only in Deregister(DynamicThing), both one-line methods Mono may inline, so their
/// callers are hooked too: DynamicThing.Awake (the only Register(DynamicThing) call) and Thing.OnDestroy's
/// Deregister(Thing) (DynamicThing.OnDestroy calls Deregister(DynamicThing), then base.OnDestroy); both lists are
/// cleared only in OcclusionManager.ClearAll (world unload, Thing.ClearAll). All of these run on the main thread (Unity
/// callbacks); other threads only read the lists. A world left (WorldStores) resets the index too.
/// </para>
/// <para>
/// Every structure and device is a thing those hooks report, so the structure and device pools need no hook of their
/// own: a class query checks each filed thing's slot in its class's pool (Structure._structuresDensePool,
/// Device._deviceDensePool, as AllThings' slot is checked), so the index answers exactly what the pools hold.
/// </para>
/// <para>
/// When a hook or slot field is missing (a game update), Ready stays false and every query refuses game_changed. With
/// [Performance] VerifyWorldIndex every query also walks its game list and logs any difference.
/// </para>
/// </summary>
internal static class ThingIndex
{
    private static readonly int ClassCount = Enum.GetValues(typeof(WorldClass)).Length;

    // Things filed a frame ahead of the queries (WorldIndex.Warm): about 0.7 ms of a frame at most, so a world's load
    // (some 76,000 seeded and told things on the test world, three times that on the large one) is filed in a few
    // seconds of frames instead of on the first query's frame.
    private const int WarmBudget = 1024;

    private static readonly WorldIndex<Thing> Index = new WorldIndex<Thing>(static thing => thing.PrefabName, ClassesOf);
    private static readonly Func<IEnumerable<Thing>> Seed = SeedThings;
    private static readonly Func<Thing, bool> InAnyList = static thing => InAllThings(thing) || InAllDynamicThings(thing);
    private static readonly Func<Thing, bool> InThings = InAllThings;
    private static readonly Func<Thing, bool> InDynamicThings = InAllDynamicThings;
    private static readonly Func<Thing, bool> InStructures = InAllStructures;
    private static readonly Func<Thing, bool> InDevices = InAllDevices;

    private static AccessTools.FieldRef<Thing, DensePoolReference<Thing>>? _thingSlot;
    private static AccessTools.FieldRef<DynamicThing, DensePoolReference<DynamicThing>>? _dynamicSlot;
    private static AccessTools.FieldRef<Structure, DensePoolReference<Structure>>? _structureSlot;
    private static AccessTools.FieldRef<Device, DensePoolReference<Device>>? _deviceSlot;
    private static AccessTools.FieldRef<DensePool<Thing>, Thing[]>? _thingEntries;
    private static AccessTools.FieldRef<DensePool<DynamicThing>, DynamicThing[]>? _dynamicEntries;
    private static AccessTools.FieldRef<DensePool<Structure>, Structure[]>? _structureEntries;
    private static AccessTools.FieldRef<DensePool<Device>, Device[]>? _deviceEntries;
    private static string _missing = "the world index was not enabled";
    private static long _verified;
    private static long _differences;

    /// <summary>Every hook is in place and every list's membership can be read.</summary>
    internal static bool Ready { get; private set; }

    /// <summary>[Performance] VerifyWorldIndex: every query also walks its list and logs a difference.</summary>
    internal static bool Verify { get; set; }

    /// <summary>
    /// At load, after the patch classes: ready when every hook below was applied by this mod and every membership field
    /// is found. Not ready: logged once, and every query refuses game_changed.
    /// </summary>
    internal static void Enable(string harmonyId)
    {
        try
        {
            List<string> missing = new List<string>();
            foreach (GameMethod target in new[]
                     {
                         GameMembers.PatchRegisterThing, GameMembers.PatchDeregisterThing,
                         GameMembers.PatchRegisterDynamicThing, GameMembers.PatchDeregisterDynamicThing,
                         GameMembers.PatchDynamicThingAwake, GameMembers.PatchOcclusionClearAll,
                     })
            {
                if (!target.TryResolve() || !HasPostfix(target.Info, harmonyId))
                {
                    missing.Add(target.Name);
                }
            }

            foreach (GameField field in new[]
                     {
                         GameMembers.ThingAllThingsSlot, GameMembers.DynamicThingPoolSlot, GameMembers.StructurePoolSlot,
                         GameMembers.DeviceDensePool, GameMembers.ThingPoolEntries, GameMembers.DynamicThingPoolEntries,
                         GameMembers.StructurePoolEntries, GameMembers.DevicePoolEntries,
                     })
            {
                if (!field.TryResolve())
                {
                    missing.Add(field.Name);
                }
            }

            if (missing.Count > 0)
            {
                _missing = string.Join(", ", missing);
                StationGodMod.LogWarning($"World index off (every world query refuses game_changed): {_missing}.");
                return;
            }

            _thingSlot = AccessTools.FieldRefAccess<Thing, DensePoolReference<Thing>>(GameMembers.ThingAllThingsSlot.Info);
            _dynamicSlot = AccessTools.FieldRefAccess<DynamicThing, DensePoolReference<DynamicThing>>(
                GameMembers.DynamicThingPoolSlot.Info);
            _structureSlot = AccessTools.FieldRefAccess<Structure, DensePoolReference<Structure>>(
                GameMembers.StructurePoolSlot.Info);
            _deviceSlot = AccessTools.FieldRefAccess<Device, DensePoolReference<Device>>(GameMembers.DeviceDensePool.Info);
            _thingEntries = AccessTools.FieldRefAccess<DensePool<Thing>, Thing[]>(GameMembers.ThingPoolEntries.Info);
            _dynamicEntries =
                AccessTools.FieldRefAccess<DensePool<DynamicThing>, DynamicThing[]>(GameMembers.DynamicThingPoolEntries.Info);
            _structureEntries =
                AccessTools.FieldRefAccess<DensePool<Structure>, Structure[]>(GameMembers.StructurePoolEntries.Info);
            _deviceEntries = AccessTools.FieldRefAccess<DensePool<Device>, Device[]>(GameMembers.DevicePoolEntries.Info);
            Ready = true;
        }
        catch (Exception exception)
        {
            // Harmony's patch info or a field accessor this game build refuses.
            Ready = false;
            _missing = exception.Message;
            StationGodMod.LogWarning($"World index off (every world query refuses game_changed): {exception.Message}");
        }
    }

    private static bool HasPostfix(MethodBase method, string harmonyId)
    {
        Patches? patches = Harmony.GetPatchInfo(method);
        if (patches == null)
        {
            return false;
        }

        foreach (Patch patch in patches.Postfixes)
        {
            if (patch.owner == harmonyId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>server info runtime.world_index; arrived and left only with include_counters.</summary>
    internal static WorldIndexView View(bool counters)
    {
        SortedDictionary<string, int> classes = new SortedDictionary<string, int>(StringComparer.Ordinal);
        for (int number = 0; number < ClassCount; number++)
        {
            classes[ClassName((WorldClass)number)] = Index.ClassCount(number);
        }

        return counters
            ? new WorldIndexView(Ready, Verify, Index.Count, Index.NameCount, classes, _verified, _differences,
                Index.ArrivedTotal, Index.LeftTotal)
            : new WorldIndexView(Ready, Verify, Index.Count, Index.NameCount, classes, _verified, _differences);
    }

    private static string ClassName(WorldClass worldClass) => worldClass switch
    {
        WorldClass.ReagentHolder => "reagent_holder",
        _ => worldClass.ToString().ToLowerInvariant()
    };

    // ---- hooks ----

    internal static void Arrived(Thing thing)
    {
        if (!ReferenceEquals(thing, null))
        {
            Index.Arrived(thing);
        }
    }

    internal static void Left(Thing thing)
    {
        if (!ReferenceEquals(thing, null))
        {
            Index.Left(thing);
        }
    }

    internal static void Reset() => Index.Reset();

    /// <summary>Each server frame: files a slice of the seed and of the arrivals ahead of the queries.</summary>
    internal static void Tick()
    {
        if (!Ready)
        {
            return;
        }

        using ProfScope scope = Prof.Scope(ProfId.WorldIndex);
        Index.Warm(Seed, InAnyList, WarmBudget);
    }

    // ---- queries (World's) ----

    /// <summary>The things of AllThings, or of AllDynamicThings, whose prefab name the match may keep (callers apply it).</summary>
    internal static List<Thing> ByPrefab(PrefabMatch match, bool dynamic)
    {
        RequireReady();
        using ProfScope scope = Prof.Scope(ProfId.WorldIndex);
        List<Thing> found = Index.Find(match, Seed, InAnyList, dynamic ? InDynamicThings : InThings, out _);
        if (Verify)
        {
            List<Thing> walked = dynamic ? Walk(OcclusionManager.AllDynamicThings) : Walk(OcclusionManager.AllThings);
            Compare(dynamic ? "AllDynamicThings" : "AllThings", Describe(match), found, walked,
                thing => match.Keeps(thing.PrefabName));
        }

        return found;
    }

    /// <summary>The things of one class its list holds.</summary>
    internal static List<Thing> OfClass(WorldClass worldClass)
    {
        RequireReady();
        using ProfScope scope = Prof.Scope(ProfId.WorldIndex);
        List<Thing> found = Index.OfClass((int)worldClass, Seed, InAnyList, MemberOf(worldClass));
        if (Verify)
        {
            int bit = 1 << (int)worldClass;
            Compare(ListName(worldClass), ClassName(worldClass), found, WalkOf(worldClass),
                thing => (ClassesOf(thing) & bit) != 0);
        }

        return found;
    }

    /// <summary>
    /// A class's candidates for a scan over frames (WorldIndex.Candidates), membership unchecked: a scan checks each
    /// with IsMember as its turn comes. In verify mode the class's members are also compared with the game's list
    /// (OfClass), as a query of the class would be.
    /// </summary>
    internal static List<Thing> CandidatesOf(WorldClass worldClass)
    {
        RequireReady();
        if (Verify)
        {
            OfClass(worldClass);
        }

        using ProfScope scope = Prof.Scope(ProfId.WorldIndex);
        return Index.Candidates((int)worldClass, Seed, InAnyList);
    }

    /// <summary>Whether a candidate is still in its class's game list (its pool slot), as OfClass checks.</summary>
    internal static bool IsMember(WorldClass worldClass, Thing? thing) => thing != null && MemberOf(worldClass)(thing);

    /// <summary>Refuses game_changed when the index is off.</summary>
    internal static void RequireReady()
    {
        if (!Ready)
        {
            throw new GameChangedException(_missing);
        }
    }

    // A thing's classes, read once when the index first files it (a thing's class never changes; a machine's reagent
    // mixture is made in its Awake, before the thing is filed).
    private static int ClassesOf(Thing thing)
    {
        int classes = 1 << (int)WorldClass.Thing;
        if (thing is DynamicThing)
        {
            classes |= 1 << (int)WorldClass.Dynamic;
        }

        if (thing is Structure structure)
        {
            classes |= 1 << (int)WorldClass.Structure;
            classes |= thing is Frame ? 1 << (int)WorldClass.Frame : 0;
            classes |= thing is Device ? 1 << (int)WorldClass.Device : 0;
            classes |= thing is LandingPadCenter ? 1 << (int)WorldClass.Pad : 0;
            classes |= thing is Silo ? 1 << (int)WorldClass.Silo : 0;
            classes |= thing is SatelliteDish ? 1 << (int)WorldClass.Dish : 0;
            classes |= structure.HasReadableReagentMixture || structure.ReagentMixture != null
                ? 1 << (int)WorldClass.ReagentHolder
                : 0;
        }

        if (thing is Item item)
        {
            classes |= 1 << (int)WorldClass.Item;
            classes |= item is Plant ? 1 << (int)WorldClass.Plant : 0;
            classes |= item is INutrition || item is IHydration || item is global::Objects.Items.DecayedFood
                ? 1 << (int)WorldClass.Consumable
                : 0;
        }

        return classes;
    }

    private static Func<Thing, bool> MemberOf(WorldClass worldClass) => worldClass switch
    {
        WorldClass.Thing => InThings,
        WorldClass.Dynamic or WorldClass.Item or WorldClass.Plant or WorldClass.Consumable => InDynamicThings,
        WorldClass.Device or WorldClass.Dish => InDevices,
        _ => InStructures
    };

    private static string ListName(WorldClass worldClass) => worldClass switch
    {
        WorldClass.Thing => "AllThings",
        WorldClass.Dynamic or WorldClass.Item or WorldClass.Plant or WorldClass.Consumable => "AllDynamicThings",
        WorldClass.Device or WorldClass.Dish => "AllDevices",
        _ => "AllStructuresPool"
    };

    // The verification walk of a class's own list (inside the world module: only it reads the game's lists).
    private static List<Thing> WalkOf(WorldClass worldClass) => worldClass switch
    {
        WorldClass.Thing => Walk(OcclusionManager.AllThings),
        WorldClass.Dynamic or WorldClass.Item or WorldClass.Plant or WorldClass.Consumable =>
            Walk(OcclusionManager.AllDynamicThings),
        WorldClass.Device or WorldClass.Dish => Walk(Device.AllDevices),
        _ => Walk(GridController.AllStructuresPool)
    };

    // A pool's members, read under its lock (ConcurrentDensePool locks on itself for every change; a plain DensePool's
    // Add locks too): the seed and the verification walk only.
    private static List<Thing> Walk<TListed>(DensePool<TListed> pool) where TListed : Thing, IDensePoolable
    {
        List<Thing> things = new List<Thing>(pool.ActiveCount);
        lock (pool)
        {
            foreach (TListed thing in pool.Active())
            {
                things.Add(thing);
            }
        }

        return things;
    }

    private static IEnumerable<Thing> SeedThings()
    {
        List<Thing> all = Walk(OcclusionManager.AllThings);
        all.AddRange(Walk(OcclusionManager.AllDynamicThings));
        return all;
    }

    private static bool InAllThings(Thing thing) =>
        In(_thingSlot!(thing), _thingEntries!(OcclusionManager.AllThings), thing);

    private static bool InAllDynamicThings(Thing thing) =>
        thing is DynamicThing dynamic &&
        In(_dynamicSlot!(dynamic), _dynamicEntries!(OcclusionManager.AllDynamicThings), dynamic);

    private static bool InAllStructures(Thing thing) =>
        thing is Structure structure &&
        In(_structureSlot!(structure), _structureEntries!(GridController.AllStructuresPool), structure);

    private static bool InAllDevices(Thing thing) =>
        thing is Device device && In(_deviceSlot!(device), _deviceEntries!(Device.AllDevices), device);

    private static bool In<TListed>(DensePoolReference<TListed>? slot, TListed[] entries, TListed thing)
        where TListed : class, IDensePoolable =>
        slot != null && slot.Slot >= 0 && slot.Slot < entries.Length && ReferenceEquals(entries[slot.Slot], thing);

    // The verification: every thing of the list the test keeps, against what the index gave that the test keeps.
    private static void Compare(string list, string query, List<Thing> indexed, List<Thing> listed,
        Func<Thing, bool> keeps)
    {
        HashSet<Thing> fromIndex = new HashSet<Thing>(ReferenceComparer.Instance);
        foreach (Thing thing in indexed)
        {
            if (keeps(thing))
            {
                fromIndex.Add(thing);
            }
        }

        List<Thing> missing = new List<Thing>();
        HashSet<Thing> fromList = new HashSet<Thing>(ReferenceComparer.Instance);
        foreach (Thing thing in listed)
        {
            if (!ReferenceEquals(thing, null) && keeps(thing))
            {
                fromList.Add(thing);
                if (!fromIndex.Contains(thing))
                {
                    missing.Add(thing);
                }
            }
        }

        List<Thing> extra = new List<Thing>();
        foreach (Thing thing in fromIndex)
        {
            if (!fromList.Contains(thing))
            {
                extra.Add(thing);
            }
        }

        _verified++;
        if (missing.Count == 0 && extra.Count == 0)
        {
            return;
        }

        _differences++;
        StationGodMod.LogWarning(
            $"World index differs from {list} for {query}: {fromList.Count} listed, {fromIndex.Count} " +
            $"indexed; missing {Sample(missing)}; extra {Sample(extra)}.");
    }

    private static string Describe(PrefabMatch match) =>
        (match.Exact != null ? "prefabs [" + string.Join(", ", match.Exact) + "]" : "any prefab") +
        (match.Contains != null ? $" containing '{match.Contains}'" : string.Empty);

    private static string Sample(List<Thing> things)
    {
        if (things.Count == 0)
        {
            return "none";
        }

        StringBuilder text = new StringBuilder().Append(things.Count).Append(" (");
        for (int index = 0; index < things.Count && index < 5; index++)
        {
            Thing thing = things[index];
            text.Append(index > 0 ? ", " : string.Empty).Append(thing.ReferenceId).Append(' ').Append(thing.PrefabName);
        }

        return text.Append(things.Count > 5 ? ", ...)" : ")").ToString();
    }

    private sealed class ReferenceComparer : IEqualityComparer<Thing>
    {
        internal static readonly ReferenceComparer Instance = new ReferenceComparer();

        public bool Equals(Thing? x, Thing? y) => ReferenceEquals(x, y);

        public int GetHashCode(Thing obj) => RuntimeHelpers.GetHashCode(obj);
    }
}

[HarmonyPatch(typeof(OcclusionManager), nameof(OcclusionManager.Register), new[] { typeof(Thing) })]
internal static class RegisterThingPatch
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Postfix(Thing thing) => ThingIndex.Arrived(thing);
}

[HarmonyPatch(typeof(OcclusionManager), nameof(OcclusionManager.Deregister), new[] { typeof(Thing) })]
internal static class DeregisterThingPatch
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Postfix(Thing thing) => ThingIndex.Left(thing);
}

[HarmonyPatch(typeof(OcclusionManager), nameof(OcclusionManager.Register), new[] { typeof(DynamicThing) })]
internal static class RegisterDynamicThingPatch
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Postfix(DynamicThing dynamicThing) => ThingIndex.Arrived(dynamicThing);
}

[HarmonyPatch(typeof(OcclusionManager), nameof(OcclusionManager.Deregister), new[] { typeof(DynamicThing) })]
internal static class DeregisterDynamicThingPatch
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Postfix(DynamicThing dynamicThing) => ThingIndex.Left(dynamicThing);
}

[HarmonyPatch(typeof(DynamicThing), nameof(DynamicThing.Awake))]
internal static class DynamicThingAwakePatch
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Postfix(DynamicThing __instance) => ThingIndex.Arrived(__instance);
}

[HarmonyPatch(typeof(OcclusionManager), nameof(OcclusionManager.ClearAll))]
internal static class OcclusionClearAllPatch
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Postfix() => ThingIndex.Reset();
}
