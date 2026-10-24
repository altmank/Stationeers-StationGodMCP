#nullable enable

using System.IO;
using System.Runtime.CompilerServices;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Serialization;
using HarmonyLib;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// The loaded world's save folder (saves\&lt;station&gt;\), for the files StationGod keeps beside a save (lint-rules.json,
/// stationgod-ghosts.json). XmlSaveLoad.CurrentStationName is not reliable for this: the world reset a load runs
/// (GameManager's clear-all, XmlSaveLoad.ClearAll) sets it to null partway through, and the name a load is given comes
/// from the save's own metadata, not its folder. So the folder is taken from the save file the game loads
/// (LoadHelper.LoadGame's path: the menu, the console's load and loadlatest, workshop saves) and, for a new world,
/// from the folder its first save creates (SaveHelper.CreateSaveDirectory); the station name is the fallback. A
/// clear-all outside a load (back to the menu, a new world) forgets the recorded folder.
/// </summary>
internal static class SaveFolders
{
    private static string? _recorded;
    private static string _recordedHow = "none";
    private static bool _loading;

    /// <summary>The folder, or null when no world is loaded or none can be found.</summary>
    internal static string? Current => Resolve().Folder;

    /// <summary>How Current was found: "loaded save", "new save", "station name" or "none".</summary>
    internal static string How => Resolve().How;

    /// <summary>A file in the folder (it may not exist); null without a folder.</summary>
    internal static string? FileIn(string name) => Current is { } folder ? Path.Combine(folder, name) : null;

    /// <summary>Every frame: a load is over once the world runs.</summary>
    internal static void Tick()
    {
        if (_loading && GameManager.GameState == GameState.Running)
        {
            _loading = false;
        }
    }

    internal static void Loaded(string? savePath)
    {
        _recorded = SaveFolderRule.StationFolderOf(savePath);
        _recordedHow = "loaded save";
        _loading = true;
    }

    internal static void Created(DirectoryInfo? folder)
    {
        GameState state = GameManager.GameState;
        if (folder != null && (state == GameState.Running || state == GameState.Paused))
        {
            _recorded = folder.FullName;
            _recordedHow = "new save";
        }
    }

    internal static void Cleared()
    {
        if (!_loading)
        {
            _recorded = null;
            _recordedHow = "none";
        }
    }

    private static (string? Folder, string How) Resolve()
    {
        if (_recorded != null)
        {
            return (_recorded, _recordedHow);
        }

        XmlSaveLoad? saves = XmlSaveLoad.Instance;
        string? station = saves != null ? saves.CurrentStationName : null;
        return string.IsNullOrEmpty(station)
            ? (null, "none")
            : (Path.Combine(StationSaveUtils.GetSavePathSavesSubDir().FullName, station), "station name");
    }
}

[HarmonyPatch(typeof(LoadHelper), nameof(LoadHelper.LoadGame))]
internal static class LoadGameFolderPatch
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Prefix(string path) => SaveFolders.Loaded(path);
}

[HarmonyPatch(typeof(SaveHelper), nameof(SaveHelper.CreateSaveDirectory))]
internal static class CreateSaveDirectoryFolderPatch
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Postfix(bool __result, ref DirectoryInfo directoryInfo)
    {
        if (__result)
        {
            SaveFolders.Created(directoryInfo);
        }
    }
}

[HarmonyPatch(typeof(XmlSaveLoad), nameof(XmlSaveLoad.ClearAll))]
internal static class ClearAllFolderPatch
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Postfix() => SaveFolders.Cleared();
}
