#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Assets.Scripts.Inventory;
using Assets.Scripts.Objects;
using HarmonyLib;
using StationGodMCP.Pure.Profiling;

namespace StationGodMCP;

/// <summary>
/// Gives the gateway structure a placement cursor. The game builds one per structure prefab when the inventory manager
/// sets up (InventoryManager.SetupConstructionCursors, through the private HandleStructurePrefab), which can run before
/// the gateway is registered, so the cursor is added then, and again before placement if it is still missing.
/// </summary>
internal static class ConstructionCursorRegistration
{
    internal static void EnsureRegistered(InventoryManager? inventoryManager)
    {
        InventoryManager? manager = inventoryManager != null ? inventoryManager : InventoryManager.Instance;

        // Missing members were logged once at load (GameMembers.CheckAll); the cursor just is not registered.
        if (!GameMembers.ConstructionCursors.TryResolve() || !GameMembers.ConstructionCursorParent.TryResolve() ||
            !GameMembers.HandleStructurePrefab.TryResolve())
        {
            return;
        }

        if (!(GameMembers.ConstructionCursors.GetValue(null) is Dictionary<string, Structure> cursors) ||
            cursors.ContainsKey(PrefabRegistrar.StructurePrefabName))
        {
            return;
        }

        Structure? structure = Prefab.Find(PrefabRegistrar.StructurePrefabName) as Structure;
        if (manager == null || structure == null || GameMembers.ConstructionCursorParent.GetValue(manager) == null)
        {
            return;
        }

        MethodInfo handler = GameMembers.HandleStructurePrefab.Info;
        try
        {
            handler.Invoke(handler.IsStatic ? null : manager, new object[] { structure });
            StationGodMod.Log("Registered the construction placement cursor.");
        }
        catch (Exception exception)
        {
            // InventoryManager.HandleStructurePrefab before the manager is ready: tried again at the next placement.
            StationGodMod.LogWarning($"Could not register the construction cursor yet: {exception.Message}");
        }
    }
}

[HarmonyPatch(typeof(InventoryManager), "SetupConstructionCursors")]
internal static class SetupConstructionCursorsPatch
{
    [HarmonyPostfix]
    [Profiled]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Postfix(InventoryManager __instance)
    {
        ConstructionCursorRegistration.EnsureRegistered(__instance);
    }
}

[HarmonyPatch(typeof(InventoryManager), "UpdatePlacement", new Type[] { typeof(Structure) })]
internal static class UpdateStructurePlacementPatch
{
    [HarmonyPrefix]
    [Profiled]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Prefix()
    {
        ConstructionCursorRegistration.EnsureRegistered(InventoryManager.Instance);
    }
}

[HarmonyPatch(typeof(InventoryManager), "UpdatePlacement", new Type[] { typeof(Constructor) })]
internal static class UpdateConstructorPlacementPatch
{
    [HarmonyPrefix]
    [Profiled]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Prefix()
    {
        ConstructionCursorRegistration.EnsureRegistered(InventoryManager.Instance);
    }
}
