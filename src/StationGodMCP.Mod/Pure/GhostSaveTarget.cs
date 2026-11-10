#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// Where the ghost board file is written. The save folder can change inside one world (a new world's first save, a save
/// under a new name), so the target is the folder as it is at write time, not the one captured when the board was read.
/// When the world is left the live folder is already gone (or the next world's), so the last write goes to the folder
/// the last save created (savedAs), or else to the captured file.
/// </summary>
internal static class GhostSaveTarget
{
    /// <summary>The file to write, or null when there is no folder yet: the write stays pending.</summary>
    internal static string? Of(string? captured, string? current) => current ?? captured;

    /// <summary>
    /// The last write when the world is left: the file in the folder a save created since the board was last written
    /// (savedAs), else the captured file when a write was pending, else none. notKept is true when ghosts were owed a
    /// file and there is none (the world was never saved).
    /// </summary>
    internal static List<string> OnLeaving(string? captured, IReadOnlyList<string> savedAs, bool pending, int ghosts,
        out bool notKept)
    {
        List<string> files = new List<string>(savedAs);
        if (files.Count == 0 && pending && captured != null)
        {
            files.Add(captured);
        }

        notKept = files.Count == 0 && (pending || savedAs.Count > 0) && ghosts > 0;
        return files;
    }

    /// <summary>Whether the save folder moved since the last read or write, so the whole board is owed to the new one.</summary>
    internal static bool Moved(string? captured, string? current) =>
        current != null && !string.Equals(captured, current, StringComparison.OrdinalIgnoreCase);
}
