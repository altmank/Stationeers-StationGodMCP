#nullable enable

using System;
using System.Runtime.CompilerServices;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using HarmonyLib;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Profiling;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// Print provenance: every item a machine makes, recorded as it is made (DynamicThing.ItemManufactured, which both
/// fabricator families call once the new item sits in their export slot, so its slot's parent is the maker), and every
/// stack split off a recorded one (Stackable.OnSplitStack, both split paths) inheriting its record. Host only; the log
/// lives in memory since the game started (PrintLog), so an item printed before is not in it.
/// </summary>
internal static class Prints
{
    internal static PrintLog Log { get; } = new PrintLog();

    internal static void Manufactured(DynamicThing item, int quantity)
    {
        if (Log.Stopped || item == null || !GameManager.RunSimulation)
        {
            return;
        }

        try
        {
            Thing? maker = item.ParentSlot != null ? item.ParentSlot.Parent : null;
            Log.Record(new PrintRecord(item.ReferenceId, item.PrefabName, maker != null ? maker.ReferenceId : 0L,
                maker != null ? maker.PrefabName : null, maker != null ? maker.DisplayName : null,
                GameManager.GameTime, item is Stackable stack ? stack.Quantity : quantity, null));
        }
        catch (Exception exception)
        {
            // A game tick path: logged once, then provenance stays off until the world is left.
            Log.Stop();
            StationGodMod.LogWarning($"Print provenance stopped: {exception.Message}");
        }
    }

    internal static void Split(Stackable source, Stackable created)
    {
        if (Log.Stopped || source == null || created == null || !GameManager.RunSimulation)
        {
            return;
        }

        try
        {
            Log.Split(source.ReferenceId, created.ReferenceId, created.Quantity);
        }
        catch (Exception exception)
        {
            Log.Stop();
            StationGodMod.LogWarning($"Print provenance stopped: {exception.Message}");
        }
    }
}

[HarmonyPatch(typeof(DynamicThing), nameof(DynamicThing.ItemManufactured))]
internal static class ItemManufacturedPatch
{
    [Profiled]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Postfix(DynamicThing item, int quantity)
    {
        Prints.Manufactured(item, quantity);
    }
}

[HarmonyPatch(typeof(Stackable), "OnSplitStack")]
internal static class StackSplitPatch
{
    [Profiled]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Postfix(Stackable __instance, Stackable newStack)
    {
        Prints.Split(__instance, newStack);
    }
}
