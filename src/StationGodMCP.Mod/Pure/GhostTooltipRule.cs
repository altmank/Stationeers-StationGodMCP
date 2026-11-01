#nullable enable

namespace StationGodMCP.Pure;

/// <summary>
/// Whether the ghost tooltip (the panel under the crosshair for the ghost there) is drawn this frame. It is off unless
/// [Ghosts] ShowTooltip turns it on (LU, 2026-11-01: "Disabled by default for now"), and never while the game's UI is
/// hidden (InventoryManager.ShowUi false: the player's hide-UI key, or Loom's planning). The ghost under the crosshair
/// is still found and drawn brighter either way: only the panel waits on the setting.
/// </summary>
internal static class GhostTooltipRule
{
    /// <summary>[Ghosts] ShowTooltip's default.</summary>
    internal const bool ShownByDefault = false;

    internal static bool Shows(bool setting, bool gameUiShown, bool ghostUnderCrosshair) =>
        setting && gameUiShown && ghostUnderCrosshair;
}
