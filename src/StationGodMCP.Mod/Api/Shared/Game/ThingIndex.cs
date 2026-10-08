#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Util;
using HarmonyLib;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Profiling;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// The prefab index (Pure/PrefabIndex) over the game's two master lists, OcclusionManager.AllThings and
/// AllDynamicThings, for find_things, find_items, item_totals, every other WorldItems walk filtered by prefab, and the
/// SDB Silos (Silos.All, by the names of the Silo prefabs).
/// <para>
/// In step with the lists by the game's own add and remove points (decompile Assets.Scripts/OcclusionManager.cs):
/// AllThings.Add only in OcclusionManager.Register(Thing) (which Thing.Create calls for every thing that is not a
/// cursor) and AllThings.Remove only in Deregister(Thing) (Thing.OnDestroy); AllDynamicThings.Add only in
/// Register(DynamicThing) and Remove only in Deregister(DynamicThing), both one-line methods Mono may inline, so their
/// callers are hooked too: DynamicThing.Awake (the only Register(DynamicThing) call) and Thing.OnDestroy's
/// Deregister(Thing) (DynamicThing.OnDestroy calls Deregister(DynamicThing), then base.OnDestroy); both lists are
/// cleared only in OcclusionManager.ClearAll (world unload, Thing.ClearAll). A world left (WorldStores) resets the
/// index too. Membership itself is read from the lists (each thing's DensePoolReference slot and the pool's entry in
/// it), so the index answers exactly what the lists hold.
/// </para>
/// <para>
/// When a target or field is missing (a game update), Ready stays false and every list walks the master lists as
/// before. [Performance] VerifyPrefabIndex runs the full walk beside every indexed query and logs any difference.
/// </para>
/// </summary>
internal static class ThingIndex
{
    private static readonly PrefabIndex<Thing> Index = new PrefabIndex<Thing>(static thing => thing.PrefabName);
    private static readonly Func<IEnumerable<Thing>> Seed = SeedThings;
    private static readonly Func<Thing, bool> InAnyList = static thing => InAllThings(thing) || InAllDynamicThings(thing);
    private static readonly Func<Thing, bool> InThings = InAllThings;
    private static readonly Func<Thing, bool> InDynamicThings = InAllDynamicThings;

    private static AccessTools.FieldRef<Thing, DensePoolReference<Thing>>? _thingSlot;
    private static AccessTools.FieldRef<DynamicThing, DensePoolReference<DynamicThing>>? _dynamicSlot;
    private static AccessTools.FieldRef<DensePool<Thing>, Thing[]>? _thingEntries;
    private static AccessTools.FieldRef<DensePool<DynamicThing>, DynamicThing[]>? _dynamicEntries;
    private static long _verified;
    private static long _differences;

    /// <summary>Every hook is in place and the lists' membership can be read: queries use the index.</summary>
    internal static bool Ready { get; private set; }

    /// <summary>[Performance] VerifyPrefabIndex: every indexed query also walks the list and logs a difference.</summary>
    internal static bool Verify { get; set; }

    /// <summary>
    /// At load, after the patch classes: ready when every hook below was applied by this mod and every membership field
    /// is found. Not ready: logged once, lists walk everything.
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
                         GameMembers.ThingAllThingsSlot, GameMembers.DynamicThingPoolSlot, GameMembers.ThingPoolEntries,
                         GameMembers.DynamicThingPoolEntries,
                     })
            {
                if (!field.TryResolve())
                {
                    missing.Add(field.Name);
                }
            }

            if (missing.Count > 0)
            {
                StationGodMod.LogWarning(
                    $"Prefab index off (lists filtered by prefab walk every thing): {string.Join(", ", missing)}.");
                return;
            }

            _thingSlot = AccessTools.FieldRefAccess<Thing, DensePoolReference<Thing>>(GameMembers.ThingAllThingsSlot.Info);
            _dynamicSlot = AccessTools.FieldRefAccess<DynamicThing, DensePoolReference<DynamicThing>>(
                GameMembers.DynamicThingPoolSlot.Info);
            _thingEntries = AccessTools.FieldRefAccess<DensePool<Thing>, Thing[]>(GameMembers.ThingPoolEntries.Info);
            _dynamicEntries =
                AccessTools.FieldRefAccess<DensePool<DynamicThing>, DynamicThing[]>(GameMembers.DynamicThingPoolEntries.Info);
            Ready = true;
        }
        catch (Exception exception)
        {
            // Harmony's patch info or a field accessor this game build refuses: the lists walk everything.
            Ready = false;
            StationGodMod.LogWarning($"Prefab index off: {exception.Message}");
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

    /// <summary>mod_info runtime.prefab_index.</summary>
    internal static PrefabIndexView View() =>
        new PrefabIndexView(Ready, Verify, Index.Count, Index.NameCount, _verified, _differences);

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

    // ---- queries ----

    /// <summary>
    /// The things of AllThings whose prefab name the match may keep (callers still apply it), or null when the index is
    /// not ready or the match names no prefab: then walk the list.
    /// </summary>
    internal static List<Thing>? Things(PrefabMatch match)
    {
        if (!Ready || !match.IsActive)
        {
            return null;
        }

        using ProfScope scope = Prof.Scope(ProfId.PrefabIndex);
        List<Thing> found = Index.Find(match, Seed, InAnyList, InThings, out _);
        if (Verify)
        {
            Compare("AllThings", match, found, Pools.Snapshot(OcclusionManager.AllThings));
        }

        return found;
    }

    /// <summary>As Things, for AllDynamicThings.</summary>
    internal static List<DynamicThing>? DynamicThings(PrefabMatch match)
    {
        if (!Ready || !match.IsActive)
        {
            return null;
        }

        using ProfScope scope = Prof.Scope(ProfId.PrefabIndex);
        List<Thing> found = Index.Find(match, Seed, InAnyList, InDynamicThings, out _);
        List<DynamicThing> dynamic = new List<DynamicThing>(found.Count);
        foreach (Thing thing in found)
        {
            dynamic.Add((DynamicThing)thing);
        }

        if (Verify)
        {
            Compare("AllDynamicThings", match, found, Pools.Snapshot(OcclusionManager.AllDynamicThings));
        }

        return dynamic;
    }

    /// <summary>
    /// The things of AllThings of one class, found by the names of the prefabs of that class (a thing's prefab name is
    /// its prefab's: Thing.Create sets PrefabName from the prefab it instantiates); null when the index is not ready.
    /// Callers still test the class.
    /// </summary>
    internal static List<Thing>? ThingsOfClass<TThing>() where TThing : Thing =>
        Ready ? Things(ClassNames<TThing>.Match()) ?? new List<Thing>() : null;

    /// <summary>
    /// With [Performance] VerifyPrefabIndex on: compares what the index found for a class against a walk of another
    /// master list (the structures' pool), logging a difference as for prefab queries.
    /// </summary>
    internal static void VerifyClass<TThing>(List<TThing> indexed, List<TThing> walked) where TThing : Thing
    {
        if (!Verify)
        {
            return;
        }

        List<Thing> found = new List<Thing>(indexed.Count);
        foreach (TThing thing in indexed)
        {
            found.Add(thing);
        }

        Compare(typeof(TThing).Name + " walk", PrefabMatch.Any, found, walked);
    }

    // The names of the loaded prefabs of one class, worked out again when the prefab list grows.
    private static class ClassNames<TThing> where TThing : Thing
    {
        private static int _prefabCount = -1;
        private static PrefabMatch _match = PrefabMatch.OfNames(Array.Empty<string>(), null);

        internal static PrefabMatch Match()
        {
            if (_prefabCount == Prefab.AllPrefabs.Count)
            {
                return _match;
            }

            List<string> names = new List<string>();
            foreach (Thing prefab in Prefab.AllPrefabs)
            {
                if (prefab is TThing && prefab != null)
                {
                    names.Add(prefab.name);
                    if (!string.IsNullOrEmpty(prefab.PrefabName))
                    {
                        names.Add(prefab.PrefabName);
                    }
                }
            }

            _match = PrefabMatch.OfNames(names, null);
            _prefabCount = Prefab.AllPrefabs.Count;
            return _match;
        }
    }

    private static IEnumerable<Thing> SeedThings()
    {
        List<Thing> things = Pools.Snapshot(OcclusionManager.AllThings);
        List<DynamicThing> dynamic = Pools.Snapshot(OcclusionManager.AllDynamicThings);
        List<Thing> all = new List<Thing>(things.Count + dynamic.Count);
        all.AddRange(things);
        foreach (DynamicThing thing in dynamic)
        {
            all.Add(thing);
        }

        return all;
    }

    private static bool InAllThings(Thing thing)
    {
        DensePoolReference<Thing>? slot = _thingSlot!(thing);
        Thing[] entries = _thingEntries!(OcclusionManager.AllThings);
        return slot != null && slot.Slot >= 0 && slot.Slot < entries.Length && ReferenceEquals(entries[slot.Slot], thing);
    }

    private static bool InAllDynamicThings(Thing thing)
    {
        if (!(thing is DynamicThing dynamic))
        {
            return false;
        }

        DensePoolReference<DynamicThing>? slot = _dynamicSlot!(dynamic);
        DynamicThing[] entries = _dynamicEntries!(OcclusionManager.AllDynamicThings);
        return slot != null && slot.Slot >= 0 && slot.Slot < entries.Length &&
               ReferenceEquals(entries[slot.Slot], dynamic);
    }

    // The verification walk: every thing of the list the match keeps, against what the index gave that the match keeps.
    private static void Compare<TListed>(string list, PrefabMatch match, List<Thing> indexed, List<TListed> listed)
        where TListed : Thing
    {
        HashSet<Thing> fromIndex = new HashSet<Thing>(ReferenceComparer.Instance);
        foreach (Thing thing in indexed)
        {
            if (match.Keeps(thing.PrefabName))
            {
                fromIndex.Add(thing);
            }
        }

        List<Thing> missing = new List<Thing>();
        HashSet<Thing> fromList = new HashSet<Thing>(ReferenceComparer.Instance);
        foreach (TListed thing in listed)
        {
            if (!ReferenceEquals(thing, null) && match.Keeps(thing.PrefabName))
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
            $"Prefab index differs from {list} for {Describe(match)}: {fromList.Count} listed, {fromIndex.Count} " +
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
