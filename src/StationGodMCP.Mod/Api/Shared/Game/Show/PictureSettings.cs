#nullable enable

using System.IO;
using BepInEx;
using BepInEx.Configuration;

namespace StationGodMCP.Api.Shared.Game.Show;

/// <summary>
/// [Pictures]: the one folder show picture writes under (PicturePaths keeps every picture inside it). The default is
/// BepInEx\StationGodMCP\pictures, beside the profiler's CSVs: it exists whether or not a world is loaded or saved, it
/// is the mod's own data folder rather than the player's saves, and a caller can never move it, only name a subfolder.
/// Read once at load; a blank or relative value falls back to the default with a warning.
/// </summary>
internal static class PictureSettings
{
    private const string Section = "Pictures";

    internal static string DefaultFolder => Path.Combine(Paths.BepInExRootPath, "StationGodMCP", "pictures");

    internal static string Folder { get; private set; } = DefaultFolder;

    internal static void Load(ConfigFile configuration)
    {
        string given = configuration.Bind(Section, "Folder", string.Empty,
            "The folder show picture writes PNGs under (a caller names only a file and a subfolder inside it). Blank: " +
            "BepInEx\\StationGodMCP\\pictures. Must be an absolute path. Restart the game to apply.").Value.Trim();
        if (given.Length == 0)
        {
            Folder = DefaultFolder;
            return;
        }

        if (!Path.IsPathRooted(given))
        {
            StationGodMod.LogWarning($"Ignoring [Pictures] Folder '{given}': not an absolute path; using {DefaultFolder}.");
            Folder = DefaultFolder;
            return;
        }

        Folder = Path.GetFullPath(given);
    }
}
