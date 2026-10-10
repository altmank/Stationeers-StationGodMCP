#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// Every store of per-world state, in one list (WorldScope), cleared the first frame after a world is left: loading a
/// save, starting a new game or going to the menu (GameManager.GameState None or Loading, the test GasHold used
/// before). Reference ids repeat across loads of a save, so without this a chip paused with control_ic_execution
/// stayed paused in the reloaded world, undo_job could plan against a job of another world and find_things made_by
/// could name the wrong maker. StationGodMod.Update calls Tick before any request runs. A new store that holds world
/// state registers its Clear below.
/// </summary>
internal static class WorldStores
{
    private static readonly WorldScope Scope = Build();

    /// <summary>Worlds left since the mod loaded.</summary>
    internal static long Epoch => Scope.Epoch;

    /// <summary>The running world's id, new each time a world starts running.</summary>
    internal static string WorldId => Scope.WorldId;

    /// <summary>Whether this frame is the first of a running world.</summary>
    internal static bool Entered => Scope.Entered;

    /// <summary>The registered stores' names, in clearing order.</summary>
    internal static List<string> Names()
    {
        List<string> names = new List<string>(Scope.Stores.Count);
        foreach ((string name, Action _) in Scope.Stores)
        {
            names.Add(name);
        }

        return names;
    }

    private static WorldScope Build()
    {
        WorldScope scope = new WorldScope();
        // LU decision (2026-10-01): a chip paused by control_ic_execution runs again in the next world.
        scope.Register("ic_pauses", IcExecutionController.Clear);
        scope.Register("gas_hold", GasHold.Clear);
        scope.Register("held_jobs", HeldTickJobs.ForgetWorld);
        scope.Register("job_snapshots", JobSnapshots.Clear);
        scope.Register("print_log", Prints.Log.Clear);
        scope.Register("flight_logs", RocketFlightRecorder.ClearAll);
        scope.Register("gas_moves", GasMoves.Clear);
        scope.Register("planet_gas_removals", PlanetGasRemoval.Clear);
        scope.Register("blueprint_paste", PasteBlueprintApi.ForgetWorld);
        scope.Register("highlights", static () => Highlights.Clear());
        scope.Register("previews", static () => Previews.Clear());
        scope.Register("remote_views", Net.RemoteViews.Clear);
        scope.Register("prefab_index", ThingIndex.Reset);
        scope.Register("chip_programs", Pure.Lint.LintChipPrograms.Clear);
        return scope;
    }

    /// <summary>Every frame, before the requests: clears every store the first frame after a world was left.</summary>
    internal static void Tick()
    {
        bool running = GameManager.GameState != GameState.None && GameManager.GameState != GameState.Loading;
        foreach ((string name, Exception failure) in Scope.Observe(running))
        {
            StationGodMod.LogWarning($"Could not clear {name} for the new world: {failure}");
        }
    }
}
