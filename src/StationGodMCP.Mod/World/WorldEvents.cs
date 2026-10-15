#nullable enable

using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Pipes;
using HarmonyLib;
using Rooms;
using StationGodMCP.Pure.Facts;
using TerrainSystem;
using UnityEngine;

namespace StationGodMCP.WorldModel;

/// <summary>
/// The game's change points the followed facts hang on (cost design A2; decompile paths in reference/cost-build-status.md,
/// step 5). Each hook only records: the large cells whose air, gravity, room or terrain changed (Cells: FrameFaces), the
/// things whose damage or wreck state may have flipped (DamagedThings), and a count of data-network changes
/// (GatewayMaps). Threads: GridController.UpdateAirState and the terrain edits run on the main thread; rooms are
/// recomputed on the game's tick thread (RoomEvaluator.ThreadedWork); damage is written on the main thread, the tick
/// thread (decay, pipe pressure) and the atmospherics workers (fire); a cable network's lists are dirtied by placement
/// (main) and read on the tick thread. So every record is thread-safe and the main thread files it.
/// <para>
/// No fallback: a keeper whose hooks are not all applied (a game update) is off, logged once, and its readers refuse
/// game_changed naming the missing hooks.
/// </para>
/// </summary>
internal static class WorldEvents
{
    private static long _dataNetworkChanges;

    /// <summary>The large cells whose air, gravity, room or terrain changed, for FrameFaces.</summary>
    internal static ChangedCells Cells { get; } = new ChangedCells();

    /// <summary>Changes of any cable network's device lists since the mod loaded.</summary>
    internal static long DataNetworkChanges => Interlocked.Read(ref _dataNetworkChanges);

    /// <summary>The hooks FrameFaces needs that are not applied ("" when every one is).</summary>
    internal static string CellsMissing { get; private set; } = "the change points were not enabled";

    /// <summary>The hooks DamagedThings needs that are not applied ("" when every one is).</summary>
    internal static string DamageMissing { get; private set; } = "the change points were not enabled";

    /// <summary>The hooks GatewayMaps needs that are not applied ("" when every one is).</summary>
    internal static string DataNetworksMissing { get; private set; } = "the change points were not enabled";

    /// <summary>At load, after the patch classes: which keepers have every hook they need.</summary>
    internal static void Enable(string harmonyId)
    {
        CellsMissing = Missing(harmonyId, GameMembers.PatchUpdateAirState, GameMembers.PatchRegisterRoom,
            GameMembers.PatchDeleteRoom, GameMembers.PatchRoomAddGrid, GameMembers.PatchRoomRemoveGrid,
            GameMembers.PatchRoomControllerRegister, GameMembers.PatchRoomControllerDeregister,
            GameMembers.PatchSetDensityWorldSpace, GameMembers.PatchApplyVoxelWorldEffects);
        DamageMissing = Missing(harmonyId, GameMembers.PatchDamage, GameMembers.PatchDamageCopyState,
            GameMembers.PatchDamageCopyUpdate, GameMembers.PatchDamageRead, GameMembers.PatchPipeBurst,
            GameMembers.PatchUpdateAirState);
        DataNetworksMissing = Missing(harmonyId, GameMembers.PatchDirtyPowerAndDataDeviceLists,
            GameMembers.PatchDirtyDataDeviceList);
        foreach ((string keeper, string missing) in new[]
                 {
                     ("frame faces", CellsMissing), ("damaged things", DamageMissing),
                     ("gateway device maps", DataNetworksMissing)
                 })
        {
            if (missing.Length > 0)
            {
                StationGodMod.LogWarning($"Followed {keeper} off (its readers refuse game_changed): {missing}.");
            }
        }
    }

    /// <summary>The targets this mod has not hooked (a prefix or postfix of its own), by name; "" when none.</summary>
    internal static string Missing(string harmonyId, params GameMethod[] targets)
    {
        List<string> missing = new List<string>();
        foreach (GameMethod target in targets)
        {
            if (!target.TryResolve() || !Hooked(target.Info, harmonyId))
            {
                missing.Add(target.Name);
            }
        }

        return string.Join(", ", missing);
    }

    private static bool Hooked(MethodBase method, string harmonyId)
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

        foreach (Patch patch in patches.Prefixes)
        {
            if (patch.owner == harmonyId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A world left: nothing recorded stays.</summary>
    internal static void Clear() => Cells.Clear();

    // ---- what the hooks record ----

    // A structure's air or gravity changed (placed, removed, attached, detached, build state, door, loaded): the cells
    // it blocks, as UpdateAirState reads them. A build state below 0 is a wreck: its damage fact may have flipped too.
    internal static void AirChanged(Structure structure)
    {
        if (ReferenceEquals(structure, null))
        {
            return;
        }

        Grid3[]? grids = structure.BlockingGrids;
        if (grids == null)
        {
            Grid3 at = new Grid3(structure.ThingTransformPosition);
            Cells.Add(at.x, at.y, at.z);
        }
        else
        {
            foreach (Grid3 grid in grids)
            {
                Cells.Add(grid.x, grid.y, grid.z);
            }
        }

        DamagedThings.Changed(structure);
    }

    internal static void RoomChanged(Room room)
    {
        List<WorldGrid>? grids = room?.Grids;
        if (grids == null)
        {
            return;
        }

        for (int index = 0; index < grids.Count; index++)
        {
            RoomCellChanged(grids[index]);
        }
    }

    internal static void RoomCellChanged(WorldGrid grid) => Cells.Add(grid.Value.x, grid.Value.y, grid.Value.z);

    internal static void VoxelChanged(Vector3 at) => Cells.AddVoxel(at.x, at.y, at.z);

    internal static void DamageChanged(IndestructableDamageState? state)
    {
        if (!ReferenceEquals(state, null) && !ReferenceEquals(state.Parent, null))
        {
            DamagedThings.Changed(state.Parent);
        }
    }

    internal static void DataNetworkChanged() => Interlocked.Increment(ref _dataNetworkChanges);
}

[HarmonyPatch(typeof(GridController), nameof(GridController.UpdateAirState))]
internal static class UpdateAirStatePatch
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Postfix(Structure structure) => WorldEvents.AirChanged(structure);
}

[HarmonyPatch(typeof(RoomHelper), nameof(RoomHelper.RegisterRoom))]
internal static class RegisterRoomPatch
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Postfix(Room room) => WorldEvents.RoomChanged(room);
}

// A prefix: the room's cells are read before the game empties it.
[HarmonyPatch(typeof(RoomHelper), nameof(RoomHelper.DeleteRoom))]
internal static class DeleteRoomPatch
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Prefix(Room room) => WorldEvents.RoomChanged(room);
}

[HarmonyPatch(typeof(Room), nameof(Room.AddGrid))]
internal static class RoomAddGridPatch
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Postfix(WorldGrid grid) => WorldEvents.RoomCellChanged(grid);
}

[HarmonyPatch(typeof(Room), nameof(Room.RemoveGrid))]
internal static class RoomRemoveGridPatch
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Postfix(WorldGrid grid) => WorldEvents.RoomCellChanged(grid);
}

[HarmonyPatch(typeof(RoomController), nameof(RoomController.Register))]
internal static class RoomControllerRegisterPatch
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Postfix(Room room) => WorldEvents.RoomChanged(room);
}

[HarmonyPatch(typeof(RoomController), nameof(RoomController.Deregister))]
internal static class RoomControllerDeregisterPatch
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Prefix(Room room) => WorldEvents.RoomChanged(room);
}

[HarmonyPatch(typeof(VoxelTerrain), nameof(VoxelTerrain.SetDensityWorldSpace))]
internal static class SetDensityWorldSpacePatch
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Postfix(Vector3 worldPosition) => WorldEvents.VoxelChanged(worldPosition);
}

[HarmonyPatch(typeof(VoxelTerrain), nameof(VoxelTerrain.ApplyVoxelWorldEffects))]
internal static class ApplyVoxelWorldEffectsPatch
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Postfix(Vector3 worldVoxelPosition) => WorldEvents.VoxelChanged(worldVoxelPosition);
}

[HarmonyPatch(typeof(IndestructableDamageState), nameof(IndestructableDamageState.Damage))]
internal static class DamagePatch
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Postfix(IndestructableDamageState __instance) => WorldEvents.DamageChanged(__instance);
}

[HarmonyPatch(typeof(IndestructableDamageState), nameof(IndestructableDamageState.Copy),
    new[] { typeof(IndestructableDamageState) })]
internal static class DamageCopyStatePatch
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Postfix(IndestructableDamageState __instance) => WorldEvents.DamageChanged(__instance);
}

[HarmonyPatch(typeof(IndestructableDamageState), nameof(IndestructableDamageState.Copy), new[] { typeof(DamageUpdate) })]
internal static class DamageCopyUpdatePatch
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Postfix(IndestructableDamageState __instance) => WorldEvents.DamageChanged(__instance);
}

[HarmonyPatch(typeof(IndestructableDamageState), nameof(IndestructableDamageState.Read))]
internal static class DamageReadPatch
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Postfix(IndestructableDamageState damageState) => WorldEvents.DamageChanged(damageState);
}

[HarmonyPatch(typeof(Pipe), nameof(Pipe.IsBurst), MethodType.Setter)]
internal static class PipeBurstPatch
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Postfix(Pipe __instance) => DamagedThings.Changed(__instance);
}

[HarmonyPatch(typeof(CableNetwork), nameof(CableNetwork.DirtyPowerAndDataDeviceLists))]
internal static class DirtyPowerAndDataDeviceListsPatch
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Postfix() => WorldEvents.DataNetworkChanged();
}

[HarmonyPatch(typeof(CableNetwork), nameof(CableNetwork.DirtyDataDeviceList))]
internal static class DirtyDataDeviceListPatch
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Postfix() => WorldEvents.DataNetworkChanged();
}
