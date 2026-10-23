#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Inventory;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.UI;
using Assets.Scripts.Util;
using BepInEx.Configuration;
using HarmonyLib;
using StationGodMCP.Pure;
using StationGodMCP.Pure.RemoteView;
using StationGodMCP.Pure.Verbs;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// [Ghosts]: the player's ghost keys, each a BepInEx keyboard shortcut (a key with modifiers, "G + LeftControl"; empty
/// turns it off), whether the player's ghost tools are on, and the tint new ghosts take. Read once at load.
/// </summary>
internal static class GhostSettings
{
    private const string Section = "Ghosts";

    internal static bool Enabled { get; private set; } = true;

    internal static KeyboardShortcut ModeKey { get; private set; } = new KeyboardShortcut(KeyCode.G, KeyCode.LeftControl);

    internal static KeyboardShortcut MenuKey { get; private set; } =
        new KeyboardShortcut(KeyCode.G, KeyCode.LeftControl, KeyCode.LeftShift);

    internal static KeyboardShortcut VisibleKey { get; private set; } = new KeyboardShortcut(KeyCode.H, KeyCode.LeftControl);

    internal static KeyboardShortcut RemoveKey { get; private set; } = new KeyboardShortcut(KeyCode.Delete);

    internal static string Tint { get; private set; } = "cyan";

    internal static void Load(ConfigFile configuration)
    {
        Enabled = configuration.Bind(Section, "Enabled", true,
            "The player's ghost tools: ghost mode, the ghost menu and taking ghosts off. Ghosts an agent draws show " +
            "either way.").Value;
        ModeKey = configuration.Bind(Section, "GhostModeKey", ModeKey,
            "Turns ghost mode on and off: with a kit in hand, a click places a ghost of the structure instead of " +
            "building it.").Value;
        MenuKey = configuration.Bind(Section, "MenuKey", MenuKey,
            "Opens the ghost menu: pick any structure to place as a ghost without its kit, the tint, and the switches.")
            .Value;
        VisibleKey = configuration.Bind(Section, "ShowGhostsKey", VisibleKey,
            "Shows or hides the world's ghosts in this game.").Value;
        RemoveKey = configuration.Bind(Section, "RemoveGhostKey", RemoveKey,
            "In ghost mode or while placing from the menu: takes off the ghost under the crosshair.").Value;
        Tint = configuration.Bind(Section, "Tint", Tint,
            "The colour new ghosts take: " + string.Join(", ", GhostTints.Names) + ".").Value;
    }
}

/// <summary>
/// The player's ghost tools in this game. Ghost mode (GhostModeKey): with a kit in hand the game's own construction
/// cursor moves, turns and snaps as always, and a click places a ghost where it stands (InventoryManager.PlacementMode
/// is skipped that frame, so nothing is built and nothing paid) even where the game would not let the structure be
/// built yet. The ghost menu (MenuKey): any structure the game has a cursor for, by search; picking one puts its
/// cursor in the player's view without a kit (the game's placement mode runs as for the authoring tool), and each click
/// places a ghost until right click or Escape ends it. In either, the ghost under the crosshair is drawn brighter and
/// RemoveGhostKey takes it off; ShowGhostsKey hides or shows the world's ghosts. Placing and taking off go through
/// GhostBoardStore (the host's board).
/// </summary>
internal static class GhostControls
{
    private const string InputKey = "InputWindow_StationGodGhosts";

    private static readonly KeyboardShortcut Escape = new KeyboardShortcut(KeyCode.Escape);

    private static GameObject? _root;
    private static GhostOverlay? _overlay;
    private static string _note = string.Empty;
    private static float _noteUntil;

    /// <summary>Kit placements become ghosts.</summary>
    internal static bool GhostMode { get; private set; }

    /// <summary>The structure the menu put in the player's view, while its placement lasts; null otherwise.</summary>
    internal static Structure? Picking { get; private set; }

    /// <summary>True only inside InventoryManager.PlacementMode while the menu's placement runs (IsAuthoringMode).</summary>
    internal static bool InPickerPlacement { get; set; }

    internal static bool MenuOpen { get; private set; }

    /// <summary>The tint the next ghost takes.</summary>
    internal static Rgba Tint { get; set; } = GhostTints.Default;

    internal static string TintName { get; set; } = "cyan";

    /// <summary>Whether a click places a ghost now.</summary>
    internal static bool Placing => GhostMode || Picking != null;

    internal static string Note => Time.realtimeSinceStartup < _noteUntil ? _note : string.Empty;

    /// <summary>The ghost under the crosshair (its tooltip shows); null for none.</summary>
    internal static GhostMark? Hovered { get; private set; }

    /// <summary>Every frame on every game (host and client).</summary>
    internal static void Tick()
    {
        Overlay();
        if (GameManager.GameState != GameState.Running || Human.LocalHuman == null)
        {
            Stop();
            return;
        }

        // The ghost under the crosshair, for the tooltip (every ghost, the player's tools on or off).
        Hovered = (Ghosts.Count > 0 || (Ghosts.BoardVisible && Ghosts.BoardCount > 0)) &&
                  CameraController.CurrentCamera != null && !MenuOpen
            ? Ghosts.Hover(InputHelpers.GetCameraRay())
            : null;
        if (!GhostSettings.Enabled)
        {
            Ghosts.Picked = 0;
            return;
        }

        if (Picking != null && InventoryManager.ConstructionCursor == null)
        {
            // The game ended the placement (right click, Escape, a swap of hands).
            Picking = null;
        }

        if (MenuOpen && Escape.IsDown())
        {
            SetMenu(false);
        }
        else if (!Typing())
        {
            Keys();
        }

        Ghosts.Picked = Placing && Hovered != null ? Hovered.BoardId : 0;
    }

    /// <summary>The ghost menu's pick: its cursor in the player's view, placing ghosts.</summary>
    internal static void StartPicking(Structure prefab)
    {
        InventoryManager? inventory = InventoryManager.Instance;
        if (inventory == null)
        {
            return;
        }

        inventory.CancelPlacement();
        InventoryManager.CurrentMode = InventoryManager.Mode.Placement;
        InventoryManager.UpdatePlacement(prefab);
        if (InventoryManager.ConstructionCursor == null)
        {
            InventoryManager.CurrentMode = InventoryManager.Mode.Normal;
            Say($"The game has no cursor for {prefab.DisplayName}.");
            return;
        }

        Picking = prefab;
        Say($"Placing ghosts of {prefab.DisplayName}: click to place, right click to stop.");
    }

    internal static void SetGhostMode(bool on)
    {
        GhostMode = on;
        Say(on ? "Ghost mode on: placing with a kit places a ghost." : "Ghost mode off.");
    }

    internal static void SetMenu(bool open)
    {
        if (MenuOpen == open)
        {
            return;
        }

        MenuOpen = open;
        if (open)
        {
            MouseModeController.AddModal(GhostMenuModal.Instance);
            KeyManager.SetInputState(InputKey, KeyInputState.Typing);
        }
        else
        {
            MouseModeController.RemoveModal(GhostMenuModal.Instance);
            KeyManager.RemoveInputState(InputKey);
            if (CursorManager.Instance != null)
            {
                CursorManager.Instance.OnApplicationFocus(focus: true);
            }
        }
    }

    /// <summary>
    /// The click in placement mode: a ghost where the construction cursor stands, of its structure, in its turn,
    /// finished. True when it placed one (the game's placement is skipped this frame).
    /// </summary>
    internal static bool PlaceFromCursor()
    {
        Structure cursor = InventoryManager.ConstructionCursor;
        if (cursor == null || !cursor.gameObject.activeSelf)
        {
            return false;
        }

        Quaternion rotation = cursor.ThingTransform.rotation;
        Vec3 euler = CubeRotation.EulerDegreesOf(rotation.x, rotation.y, rotation.z, rotation.w) is { } turns
            ? new Vec3(turns.X, turns.Y, turns.Z)
            : Bodies.V(rotation.eulerAngles);
        Structure? found = Prefab.Find(cursor.PrefabHash) as Structure;
        Structure prefab = found != null ? found : cursor;
        int finished = prefab.BuildStates != null && prefab.BuildStates.Count > 0 ? prefab.BuildStates.Count - 1 : 0;
        string? refused = GhostBoardStore.RequestPlace(new GhostPlacement(cursor.PrefabHash, finished,
            Bodies.V(cursor.ThingTransformPosition), euler, Tint, null));
        Say(refused == null ? $"Ghost of {prefab.DisplayName} placed." : $"No ghost: {refused}.");
        return true;
    }

    /// <summary>The cursor in the ghost tint while a click places a ghost (after the game coloured it).</summary>
    internal static void TintCursor()
    {
        Structure cursor = InventoryManager.ConstructionCursor;
        if (cursor == null)
        {
            return;
        }

        Color color = new Color(Tint.Red, Tint.Green, Tint.Blue, 0.45f);
        if (cursor.Wireframe != null && cursor.Wireframe.BlueprintRenderer != null)
        {
            cursor.Wireframe.BlueprintRenderer.material.color = color;
            return;
        }

        foreach (ThingRenderer renderer in cursor.Renderers)
        {
            if (renderer != null && renderer.HasRenderer())
            {
                renderer.SetColor(color);
            }
        }
    }

    /// <summary>The tint [Ghosts] Tint names, at load; an unknown name keeps the default with a warning.</summary>
    internal static void LoadTint()
    {
        if (GhostTints.ByName(GhostSettings.Tint) is { } tint)
        {
            Tint = tint;
            TintName = GhostSettings.Tint.Trim().ToLowerInvariant();
            return;
        }

        StationGodMod.LogWarning($"[Ghosts] Tint '{GhostSettings.Tint}' names no colour; ghosts take cyan.");
    }

    internal static void Say(string note)
    {
        _note = note;
        _noteUntil = Time.realtimeSinceStartup + 4f;
    }

    private static void Keys()
    {
        if (GhostSettings.MenuKey.IsDown())
        {
            SetMenu(!MenuOpen);
            return;
        }

        if (GhostSettings.ModeKey.IsDown())
        {
            SetGhostMode(!GhostMode);
        }

        if (GhostSettings.VisibleKey.IsDown())
        {
            Ghosts.BoardVisible = !Ghosts.BoardVisible;
            Say(Ghosts.BoardVisible ? "Ghosts shown." : "Ghosts hidden.");
        }

        if (Placing && Ghosts.Picked != 0 && GhostSettings.RemoveKey.IsDown())
        {
            long id = Ghosts.Picked;
            string? refused = GhostBoardStore.RequestRemove(id);
            Say(refused == null ? $"Ghost {id} taken off." : $"Ghost {id} stays: {refused}.");
        }
    }

    // The chat, the console or another input window has the keyboard (the menu's own search field included).
    private static bool Typing() =>
        ConsoleWindow.IsOpen || InputWindowBase.IsInputWindow || (MenuOpen && GhostOverlay.SearchFocused);

    private static void Stop()
    {
        Hovered = null;
        Picking = null;
        Ghosts.Picked = 0;
        SetMenu(false);
    }

    // The overlay's game object, made at the first tick; enabled while it has something to show.
    private static void Overlay()
    {
        if (_root == null)
        {
            _root = new GameObject("StationGodGhosts");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _overlay = _root.AddComponent<GhostOverlay>();
        }

        if (_overlay != null)
        {
            _overlay.enabled = MenuOpen || Placing || Note.Length > 0 || Hovered != null;
        }
    }
}

/// <summary>The ghost menu holds the mouse while it is open, as the game's own menus do.</summary>
internal sealed class GhostMenuModal : IModal
{
    internal static readonly GhostMenuModal Instance = new GhostMenuModal();

    public bool UnlockCursor => true;
}

/// <summary>
/// The ghost tools on screen (Unity's immediate GUI): the mode line at the top while a click places ghosts, a short
/// note after each action, and the ghost menu: a search over every structure the game has a cursor for, the tint, and
/// the switches. Enabled only while it has something to show.
/// </summary>
internal sealed class GhostOverlay : MonoBehaviour
{
    private const int MenuWindowId = 0x5347;
    private const int ListLimit = 200;

    private static List<Structure>? _structures;

    private Rect _window = new Rect(120f, 120f, 420f, 560f);
    private string _search = string.Empty;
    private Vector2 _scroll;
    private GUIStyle? _line;
    private GUIStyle? _tip;

    /// <summary>Whether the menu's search field has the keyboard.</summary>
    internal static bool SearchFocused { get; private set; }

    private void Awake()
    {
        useGUILayout = true;
    }

    private void OnGUI()
    {
        _line ??= new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperCenter };
        if (GhostControls.Placing)
        {
            string mode = GhostControls.Picking != null
                ? $"GHOST: {GhostControls.Picking.DisplayName}   click place   right click stop"
                : $"GHOST MODE   click place   {GhostSettings.ModeKey} off";
            if (Ghosts.Picked != 0)
            {
                mode += $"   {GhostSettings.RemoveKey} remove ghost {Ghosts.Picked}";
            }

            Shadowed(new Rect(0f, 40f, Screen.width, 26f), mode,
                new Color(GhostControls.Tint.Red, GhostControls.Tint.Green, GhostControls.Tint.Blue));
        }

        string note = GhostControls.Note;
        if (note.Length > 0)
        {
            Shadowed(new Rect(0f, 66f, Screen.width, 26f), note, Color.white);
        }

        if (GhostControls.Hovered is { } hovered)
        {
            Tooltip(hovered);
        }

        if (GhostControls.MenuOpen)
        {
            _window = GUI.Window(MenuWindowId, _window, Menu, "StationGod ghosts");
        }
        else
        {
            SearchFocused = false;
        }
    }

    private void Menu(int id)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label("Search", GUILayout.Width(52f));
        GUI.SetNextControlName("GhostSearch");
        _search = GUILayout.TextField(_search ?? string.Empty);
        GUILayout.EndHorizontal();
        SearchFocused = GUI.GetNameOfFocusedControl() == "GhostSearch";

        GUILayout.BeginHorizontal();
        bool mode = GUILayout.Toggle(GhostControls.GhostMode, $" Ghost mode ({GhostSettings.ModeKey})");
        if (mode != GhostControls.GhostMode)
        {
            GhostControls.SetGhostMode(mode);
        }

        bool shown = GUILayout.Toggle(Ghosts.BoardVisible, $" Show ghosts ({GhostSettings.VisibleKey})");
        Ghosts.BoardVisible = shown;
        GUILayout.EndHorizontal();

        GUILayout.Label($"Tint: {GhostControls.TintName}   ghosts in the world: {GhostBoardStore.Board.Count}");
        GUILayout.BeginHorizontal();
        int column = 0;
        foreach (string name in GhostTints.Names)
        {
            if (GUILayout.Button(name, GUILayout.Width(62f)) && GhostTints.ByName(name) is { } tint)
            {
                GhostControls.Tint = tint;
                GhostControls.TintName = name;
            }

            if (++column % 6 == 0)
            {
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
            }
        }

        GUILayout.EndHorizontal();
        GUILayout.Space(4f);
        _scroll = GUILayout.BeginScrollView(_scroll);
        int listed = 0;
        string[] words = (_search ?? string.Empty).Trim().ToLowerInvariant()
            .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (Structure structure in Structures())
        {
            if (!Matches(structure, words))
            {
                continue;
            }

            if (++listed > ListLimit)
            {
                GUILayout.Label($"More match: type more of the name.");
                break;
            }

            if (GUILayout.Button(structure.DisplayName))
            {
                GhostControls.SetMenu(false);
                GhostControls.StartPicking(structure);
            }
        }

        GUILayout.EndScrollView();
        GUILayout.BeginHorizontal();
        if (GUILayout.Button($"Close ({GhostSettings.MenuKey})"))
        {
            GhostControls.SetMenu(false);
        }

        GUILayout.EndHorizontal();
        GUI.DragWindow(new Rect(0f, 0f, 10000f, 20f));
    }

    // Every word in the shown name or the prefab name, any case.
    private static bool Matches(Structure structure, string[] words)
    {
        string shown = (structure.DisplayName ?? string.Empty).ToLowerInvariant();
        string name = structure.PrefabName.ToLowerInvariant();
        foreach (string word in words)
        {
            if (!shown.Contains(word) && !name.Contains(word))
            {
                return false;
            }
        }

        return true;
    }

    // The structures the game has a construction cursor for, by shown name; read once.
    private static List<Structure> Structures()
    {
        if (_structures != null)
        {
            return _structures;
        }

        List<Structure> structures = new List<Structure>();
        if (GameMembers.ConstructionCursors.TryResolve() &&
            GameMembers.ConstructionCursors.GetValue(null) is Dictionary<string, Structure> cursors)
        {
            foreach (string name in cursors.Keys)
            {
                if (Prefab.Find(name) is Structure prefab && prefab != null)
                {
                    structures.Add(prefab);
                }
            }
        }

        structures.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        if (structures.Count > 0)
        {
            _structures = structures;
        }

        return structures;
    }

    // The hovered ghost's label (or its structure when it has none) under the crosshair, then what it is and whose.
    private void Tooltip(GhostMark ghost)
    {
        _tip ??= new GUIStyle(GUI.skin.box) { fontSize = 14, alignment = TextAnchor.MiddleCenter, wordWrap = true };
        string first = string.IsNullOrEmpty(ghost.Label) ? ghost.Title : ghost.Label!;
        string second = string.IsNullOrEmpty(ghost.Label) ? string.Empty : ghost.Title;
        string whose = ghost.BoardId != 0
            ? $"ghost {ghost.BoardId}" + (string.IsNullOrEmpty(ghost.Owner) ? string.Empty : $", placed by {ghost.Owner}")
            : "preview";
        string text = second.Length > 0 ? $"{first}\n{second} ({whose})" : $"{first}\n({whose})";
        Vector2 size = _tip.CalcSize(new GUIContent(text));
        float width = Mathf.Min(Mathf.Max(size.x + 24f, 160f), 460f);
        float height = _tip.CalcHeight(new GUIContent(text), width) + 10f;
        Rect rect = new Rect(Screen.width / 2f - width / 2f, Screen.height / 2f + 36f, width, height);
        Color before = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, 0.92f);
        GUI.Box(rect, text, _tip);
        GUI.color = before;
    }

    private void Shadowed(Rect rect, string text, Color color)
    {
        GUI.color = Color.black;
        GUI.Label(new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height), text, _line);
        GUI.color = color;
        GUI.Label(rect, text, _line);
        GUI.color = Color.white;
    }
}

/// <summary>
/// The click of the game's placement mode while a click places a ghost: the ghost goes on the board and the game's
/// placement is skipped that frame (nothing is built or paid). While the ghost menu's placement runs, the game is told
/// it is in authoring mode for the length of the call, so it keeps the cursor without a kit in hand.
/// </summary>
[HarmonyPatch(typeof(InventoryManager), "PlacementMode")]
internal static class GhostPlacementModePatch
{
    private static bool Prefix()
    {
        GhostControls.InPickerPlacement = GhostControls.Picking != null;
        return !(GhostControls.Placing && KeyManager.GetMouseDown("Primary") && GhostControls.PlaceFromCursor());
    }

    private static void Postfix()
    {
        if (GhostControls.Placing)
        {
            GhostControls.TintCursor();
        }
    }

    private static Exception? Finalizer(Exception? __exception)
    {
        GhostControls.InPickerPlacement = false;
        return __exception;
    }
}

/// <summary>Authoring mode, for PlacementMode's length only, while the ghost menu's placement runs.</summary>
[HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.IsAuthoringMode), MethodType.Getter)]
internal static class GhostAuthoringModePatch
{
    private static bool Prefix(ref bool __result)
    {
        if (!GhostControls.InPickerPlacement)
        {
            return true;
        }

        __result = true;
        return false;
    }
}
