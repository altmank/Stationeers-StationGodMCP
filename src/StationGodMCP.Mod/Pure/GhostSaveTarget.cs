#nullable enable

using System;

namespace StationGodMCP.Pure;

/// <summary>
/// Where the ghost board file is written. The save folder can change inside one world (a new world's first save, a save
/// under a new name), so the target is the folder as it is at write time, not the one captured when the board was read.
/// The captured file is used only for the last write after the world was left, when the live folder already belongs to
/// the next world or is gone.
/// </summary>
internal static class GhostSaveTarget
{
    /// <summary>The file to write, or null when there is no folder yet: the write stays pending.</summary>
    internal static string? Of(string? captured, string? current, bool worldLeft = false) =>
        worldLeft ? captured : current ?? captured;

    /// <summary>Whether the save folder moved since the last read or write, so the whole board is owed to the new one.</summary>
    internal static bool Moved(string? captured, string? current) =>
        current != null && !string.Equals(captured, current, StringComparison.OrdinalIgnoreCase);
}
