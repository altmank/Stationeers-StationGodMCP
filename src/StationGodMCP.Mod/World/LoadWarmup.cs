#nullable enable

using System;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using StationGodMCP.Api.Shared.Game.Lint;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Pure.Profiling;

namespace StationGodMCP.WorldModel;

/// <summary>
/// The static data built once per load (cost design A1 and A3), built at load rather than in the first call that needs
/// it: once the world runs and the game's placement cursors exist, each server frame builds one piece of it (the build
/// catalogue, the swap targets, each run family's kits, prefabs by name, ingots by reagent, the lint rule set), so no
/// call's frame pays for a whole catalogue. A rebuild of the prefab data (Prefabs.Generation) starts the list again.
/// Main thread.
/// </summary>
internal static class LoadWarmup
{
    private static readonly Action[] Pieces =
    {
        static () => _ = Prefabs.Build,
        static () => _ = Prefabs.Targets,
        static () => _ = Prefabs.KitsOf(new CableFamily()),
        static () => _ = Prefabs.KitsOf(new PipeFamily()),
        static () => _ = Prefabs.KitsOf(new ChuteFamily()),
        static () => _ = Prefabs.NamedIgnoringCase(string.Empty),
        static () => _ = Prefabs.IngotsByReagent,
        static () => _ = LintRuleFiles.Current()
    };

    private static long _generation = -1;
    private static int _next;

    /// <summary>Each server frame: the next piece of the static data, while any is left for this load.</summary>
    internal static void Tick()
    {
        if (GameManager.GameState != GameState.Running || !World.PrefabsLoaded || !Prefabs.CursorsMade)
        {
            return;
        }

        long generation = Prefabs.Generation;
        if (generation != _generation)
        {
            _generation = generation;
            _next = 0;
        }

        if (_next >= Pieces.Length)
        {
            return;
        }

        using ProfScope scope = Prof.Scope(ProfId.WorldIndex);
        try
        {
            Pieces[_next]();
        }
        catch (Exception exception)
        {
            // A piece that fails here fails the same way in the call that needs it, which answers for it.
            StationGodMod.LogWarning($"Building the load's static data (piece {_next}) failed: {exception.Message}");
        }

        _next++;
    }
}
