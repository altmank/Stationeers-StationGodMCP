#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Net;

/// <summary>
/// Warnings that would repeat with every message (a peer sending what this game cannot read): each key is logged the
/// first time only, so a mismatched peer costs one line, not one per view.
/// </summary>
internal static class OnceLog
{
    private const int MaximumKeys = 256;

    private static readonly HashSet<string> Logged = new HashSet<string>();

    internal static void Warning(string key, string message)
    {
        if (Logged.Count >= MaximumKeys || !Logged.Add(key))
        {
            return;
        }

        StationGodMod.LogWarning(message);
    }

    internal static void Info(string key, string message)
    {
        if (Logged.Count >= MaximumKeys || !Logged.Add(key))
        {
            return;
        }

        StationGodMod.Log(message);
    }
}
