#nullable enable

using System;
using System.IO;

namespace StationGodMCP.Pure;

/// <summary>
/// A world's save folder (saves\&lt;station&gt;\, where lint-rules.json and the ghost board live) from the save file the
/// game loaded: the file's folder, or that folder's parent when the file sits in one of the game's save subfolders
/// (autosave, quicksave, manualsave) or a backup folder.
/// </summary>
internal static class SaveFolderRule
{
    private static readonly string[] SubFolders = { "autosave", "quicksave", "manualsave" };

    /// <summary>The station folder of a loaded save file; null for a path with no folder.</summary>
    internal static string? StationFolderOf(string? savePath)
    {
        if (string.IsNullOrWhiteSpace(savePath))
        {
            return null;
        }

        string? folder = Path.GetDirectoryName(savePath!.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(folder))
        {
            return null;
        }

        string name = Path.GetFileName(folder);
        bool sub = Array.Exists(SubFolders, word => string.Equals(word, name, StringComparison.OrdinalIgnoreCase)) ||
                   name.IndexOf("Backup", StringComparison.OrdinalIgnoreCase) >= 0;
        return sub ? Path.GetDirectoryName(folder) : folder;
    }
}
