#nullable enable

using StationGodMCP.Pure;

namespace StationGodMCP.Net;

/// <summary>
/// Warnings that would repeat with every message (a peer sending what this game cannot read): each key is logged the
/// first time only, so a mismatched peer costs one line, not one per view. The keys are forgotten when the world
/// changes (WorldStores), and past the cap one line says further once-warnings are suppressed.
/// </summary>
internal static class OnceLog
{
    private const int MaximumKeys = 256;

    private static readonly OnceSet Logged = new OnceSet(MaximumKeys);

    internal static void Warning(string key, string message)
    {
        switch (Logged.Offer(key))
        {
            case OnceSet.Verdict.Log:
                StationGodMod.LogWarning(message);
                break;
            case OnceSet.Verdict.CapReached:
                StationGodMod.LogWarning(SuppressedLine);
                break;
        }
    }

    internal static void Info(string key, string message)
    {
        switch (Logged.Offer(key))
        {
            case OnceSet.Verdict.Log:
                StationGodMod.Log(message);
                break;
            case OnceSet.Verdict.CapReached:
                StationGodMod.LogWarning(SuppressedLine);
                break;
        }
    }

    /// <summary>The keys are forgotten: the next world logs its warnings again.</summary>
    internal static void Clear() => Logged.Clear();

    private const string SuppressedLine =
        "More than 256 once-only warnings were logged: further new ones are suppressed until the world changes.";
}
