#nullable enable

namespace StationGodMCP.Pure;

/// <summary>
/// What the game says about the keyboard this frame, gathered once, so the rule never sees a half-gathered state.
/// </summary>
/// <param name="GameTyping">The game's own input state is Typing (KeyManager.InputState).</param>
/// <param name="ConsoleOpen">The console window is open.</param>
/// <param name="InputWindowOpen">An InputWindowBase is open (labeller, sign, IC10 editor).</param>
/// <param name="UiFieldFocused">Unity's selected object is a focused text field (TMP_InputField or InputField).</param>
/// <param name="OwnSearchFocused">StationGod's own ghost menu search field has focus (an IMGUI field, not an EventSystem one).</param>
/// <param name="OwnMenuOpen">StationGod's ghost menu is open. It pushes KeyInputState.Typing itself, so GameTyping says nothing while it is open.</param>
internal readonly record struct KeyFacts(
    bool GameTyping,
    bool ConsoleOpen,
    bool InputWindowOpen,
    bool UiFieldFocused,
    bool OwnSearchFocused,
    bool OwnMenuOpen = false);

/// <summary>
/// The promise: while any text field has keyboard focus, no StationGod key fires (LU, 2026-11-11: "mod key commands
/// should NEVER trigger when typing into game UI with text fields, like the encylcopedia"). The one exception is the
/// Escape that closes StationGod's own ghost menu: Escape is never text, so it closes the menu even with its own
/// search focused.
/// </summary>
internal static class KeyGuardRule
{
    /// <summary>
    /// True when no fact says someone is typing. GameTyping is ignored while our own menu is open: the menu pushed that
    /// state itself, and every other text UI that matters is still caught (the console, input windows, and Unity's
    /// focused fields, which include the Stationpedia search). Our own search focus still blocks.
    /// </summary>
    internal static bool KeysAllowed(KeyFacts facts) =>
        !((facts.GameTyping && !facts.OwnMenuOpen) || facts.ConsoleOpen || facts.InputWindowOpen || facts.UiFieldFocused ||
          facts.OwnSearchFocused);

    /// <summary>
    /// Whether Escape closes StationGod's menu: it is open and no other window is typing. Our own search field and our
    /// own Typing state (the menu pushes one when it opens) don't count, so this reads neither GameTyping nor
    /// OwnSearchFocused.
    /// </summary>
    internal static bool MenuEscapeAllowed(bool menuOpen, KeyFacts facts) =>
        menuOpen && !facts.ConsoleOpen && !facts.InputWindowOpen && !facts.UiFieldFocused;
}
