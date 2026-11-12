#nullable enable

using System;
using Assets.Scripts;
using Assets.Scripts.UI;
using BepInEx.Configuration;
using StationGodMCP.Pure;
using TMPro;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// The one place StationGod reads a key (a scan test fails on any other file that does). A key fires only when no text
/// field has focus, matching the game's own keys, which bind to KeyInputState.Game and so skip Typing.
/// Decompile facts (Assembly-CSharp 26146789): KeyManager.InputState is a public static KeyInputState
/// (KeyManager.cs:74); Typing is pushed by the Stationpedia search (Stationpedia.cs:368-371, ScriptHelpWindow.cs:100),
/// the console (ConsoleWindow.cs:514), every visible InputWindowBase (InputWindowBase.cs:51-58: labeller, sign, IC10
/// editor), ImguiCreativeSpawnMenu.cs:98, WorldSettingToolsImguiWindow.cs:82 and ImGuiWindowManager:58. Some fields push
/// nothing (the rocket logic value field only checks isFocused, LogicValueDisplay.cs:42), so Unity's selected object is
/// checked too. A game patch that moves any of this must not break the tick: a failed read counts as typing, which fails
/// safe (no key fires).
/// </summary>
internal static class KeyGuard
{
    /// <summary>The key was pressed this frame and nobody is typing.</summary>
    internal static bool Down(KeyboardShortcut key) => key.IsDown() && KeyGuardRule.KeysAllowed(Gather());

    /// <summary>Escape was pressed this frame and closes StationGod's open menu (KeyGuardRule.MenuEscapeAllowed).</summary>
    internal static bool MenuEscapeDown(KeyboardShortcut escape) =>
        GhostControls.MenuOpen && escape.IsDown() && KeyGuardRule.MenuEscapeAllowed(true, Gather());

    private static KeyFacts Gather() => new KeyFacts(
        Read(() => KeyManager.InputState == KeyInputState.Typing),
        Read(() => ConsoleWindow.IsOpen),
        Read(() => InputWindowBase.IsInputWindow),
        Read(FieldFocused),
        Read(() => GhostControls.MenuOpen && GhostOverlay.SearchFocused),
        GhostControls.MenuOpen);

    private static bool FieldFocused()
    {
        UnityEngine.GameObject? selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        if (selected == null)
        {
            return false;
        }

        TMP_InputField? tmp = selected.GetComponent<TMP_InputField>();
        if (tmp != null && tmp.isFocused)
        {
            return true;
        }

        InputField? legacy = selected.GetComponent<InputField>();
        return legacy != null && legacy.isFocused;
    }

    // A fact that cannot be read counts as "typing".
    private static bool Read(Func<bool> fact)
    {
        try
        {
            return fact();
        }
        catch (Exception)
        {
            return true;
        }
    }
}
