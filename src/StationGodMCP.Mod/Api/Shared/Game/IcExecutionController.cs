#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects.Clothing;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// Pause state keyed by the reference id of the circuit holder (IC Housing or worn item). The Harmony prefixes below
/// read it on the game's electricity worker thread (ElectricityManager.ElectricityTick runs every holder's Execute)
/// while Pause and Resume change it on the main thread, so the set is never changed in place: a writer copies it,
/// changes the copy and publishes it with one volatile reference write, and a reader always sees a whole set.
/// </summary>
internal static class IcExecutionController
{
    private static readonly object WriteLock = new object();

    private static volatile HashSet<long> _pausedHolderIds = new HashSet<long>();

    internal static bool IsPaused(long holderId) => _pausedHolderIds.Contains(holderId);

    internal static void Pause(long holderId)
    {
        lock (WriteLock)
        {
            HashSet<long> copy = new HashSet<long>(_pausedHolderIds);
            if (copy.Add(holderId))
            {
                _pausedHolderIds = copy;
            }
        }
    }

    internal static void Resume(long holderId)
    {
        lock (WriteLock)
        {
            HashSet<long> copy = new HashSet<long>(_pausedHolderIds);
            if (copy.Remove(holderId))
            {
                _pausedHolderIds = copy;
            }
        }
    }

    internal static void Clear()
    {
        lock (WriteLock)
        {
            _pausedHolderIds = new HashSet<long>();
        }
    }
}

[HarmonyPatch(typeof(CircuitHousing), nameof(CircuitHousing.Execute))]
internal static class CircuitHousingExecutionPatch
{
    private static bool Prefix(CircuitHousing __instance) => !IcExecutionController.IsPaused(__instance.ReferenceId);
}

[HarmonyPatch(typeof(SuitBase), nameof(SuitBase.Execute))]
internal static class SuitBaseExecutionPatch
{
    private static bool Prefix(SuitBase __instance) => !IcExecutionController.IsPaused(__instance.ReferenceId);
}

[HarmonyPatch(typeof(AdvancedSuit), nameof(AdvancedSuit.Execute))]
internal static class AdvancedSuitExecutionPatch
{
    private static bool Prefix(AdvancedSuit __instance) => !IcExecutionController.IsPaused(__instance.ReferenceId);
}
