#nullable enable

namespace StationGodMCP.Pure;

/// <summary>When the host reads the save's ghost board: once, by whichever comes first of the tick and a change.</summary>
internal static class GhostLoadRule
{
    internal static bool ShouldLoad(bool keeps, bool running, bool latticeLoaded, bool loaded) =>
        keeps && running && !latticeLoaded && !loaded;
}
