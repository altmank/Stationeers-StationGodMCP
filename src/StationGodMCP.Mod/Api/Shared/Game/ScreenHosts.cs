#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Assets.Scripts.Objects.Electrical;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Screens;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// A ScriptedScreens host's UI model as the host keeps it (CODE, ScriptedScreens 1.0 ScriptedScreensScriptableUiSystem):
/// a BoardState per board (States, keyed by the Console's or Computer's Motherboard), cartridge (CartridgeStates) or
/// visor (VisorStates), holding Surfaces by name, each with its Elements by id (UiElement: Id, Type, Rect {Unit, X, Y,
/// W, H}, Props and Style as UiProp {Key, Value}), written under the surface's PendingOpsLock as the script calls
/// ui:element, so the model holds what the script built, committed or not. ActiveSurfaces maps a screen index to the
/// surface it shows; VirtualResolutions the size a script set. The model lives on the host, a dedicated server
/// included; drawing it is the clients' work.
/// </summary>
internal sealed class ScreenHost
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Dictionary<(Type, string), FieldInfo> Fields = new Dictionary<(Type, string), FieldInfo>();

    private readonly object _state;

    private ScreenHost(IcTarget ic, ProgrammableChip chip, object state)
    {
        Ic = ic;
        Chip = chip;
        _state = state;
    }

    internal IcTarget Ic { get; }

    internal ProgrammableChip Chip { get; }

    /// <summary>The host's ScriptedScreens state; refuses when the mod is absent or the holder shows no screen.</summary>
    internal static ScreenHost Require(IcTarget ic)
    {
        if (GameMembers.ScreensSystem.OrNull == null)
        {
            throw ApiErrors.Refused("screens_mod_required",
                "The screen tools need ScriptedScreens (Steam Workshop 3666779631), which is not loaded.");
        }

        RequireMembers();
        ProgrammableChip chip = ic.RequireChip();
        foreach (GameField table in new[]
                 {
                     GameMembers.ScreensBoardStates, GameMembers.ScreensCartridgeStates, GameMembers.ScreensVisorStates
                 })
        {
            object? state = StateIn(table.GetValue(null), ic.HolderThing);
            if (state != null)
            {
                return new ScreenHost(ic, chip, state);
            }
        }

        throw ApiErrors.Refused("no_screen",
            $"{Names.Of(ic.HolderThing)} {ic.HolderThing.ReferenceId} has no ScriptedScreens UI: its chip has drawn " +
            "nothing yet, or the holder is not a ScriptedScreens board, cartridge or visor.");
    }

    /// <summary>Every surface's name, in name order.</summary>
    internal List<string> SurfaceNames()
    {
        List<string> names = new List<string>();
        foreach (DictionaryEntry entry in Dictionary(_state, "Surfaces"))
        {
            names.Add((string)entry.Key);
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    /// <summary>Each screen index and the surface it shows.</summary>
    internal SortedDictionary<int, string> ActiveSurfaces()
    {
        SortedDictionary<int, string> active = new SortedDictionary<int, string>();
        foreach (DictionaryEntry entry in Dictionary(_state, "ActiveSurfaces"))
        {
            active[(int)entry.Key] = (string)entry.Value;
        }

        return active;
    }

    /// <summary>The surface's virtual resolution the script set; null when none.</summary>
    internal Vector2? SizeOf(string surface)
    {
        IDictionary sizes = Dictionary(_state, "VirtualResolutions");
        return sizes.Contains(surface) && sizes[surface] is Vector2 size && size.x > 0f && size.y > 0f
            ? size
            : (Vector2?)null;
    }

    /// <summary>The surface's elements in id order, copied under its lock; null when there is no such surface.</summary>
    internal List<ScreenElementData>? ElementsOf(string surface)
    {
        IDictionary surfaces = Dictionary(_state, "Surfaces");
        object? state = surfaces.Contains(surface) ? surfaces[surface] : null;
        if (state == null)
        {
            return null;
        }

        object gate = Field(state, "PendingOpsLock") ?? state;
        List<object> raw = new List<object>();
        Monitor.Enter(gate);
        try
        {
            foreach (DictionaryEntry entry in Dictionary(state, "Elements"))
            {
                if (entry.Value != null)
                {
                    raw.Add(entry.Value);
                }
            }
        }
        finally
        {
            Monitor.Exit(gate);
        }

        List<ScreenElementData> elements = raw.ConvertAll(ElementOf);
        elements.Sort(static (a, b) => string.CompareOrdinal(a.Element.Id, b.Element.Id));
        return elements;
    }

    /// <summary>
    /// A touch as a player's would arrive (DispatchInputEvent: the frame-callback input queue, then the script's
    /// click, change or toggle handler through StationeersLua's event bus).
    /// </summary>
    internal bool Press(string surface, string elementId, string eventName, string value, string player)
    {
        object input = Activator.CreateInstance(GameMembers.ScreensUiInput.Info, true);
        Set(input, "Surface", surface);
        Set(input, "Id", elementId);
        Set(input, "Event", eventName);
        Set(input, "Value", value);
        return GameMembers.ScreensDispatchInput.Invoke(null, Chip, surface, input, player) is true;
    }

    /// <summary>ScriptedScreens' own capture of a drawn surface to a PNG; only where the game draws.</summary>
    internal bool TryCapture(string? surface, out string path, out string captured, out int width, out int height,
        out string error)
    {
        object?[] arguments = { Chip, surface, null, null, 0, 0, null };
        bool ok = GameMembers.ScreensCapture.Invoke(null, arguments) is true;
        path = arguments[2] as string ?? string.Empty;
        captured = arguments[3] as string ?? string.Empty;
        width = arguments[4] is int w ? w : 0;
        height = arguments[5] is int h ? h : 0;
        error = arguments[6] as string ?? string.Empty;
        return ok;
    }

    private static void RequireMembers()
    {
        List<string> missing = new List<string>();
        foreach (GameMember member in new GameMember[]
                 {
                     GameMembers.ScreensBoardStates, GameMembers.ScreensCartridgeStates, GameMembers.ScreensVisorStates,
                     GameMembers.ScreensDispatchInput, GameMembers.ScreensCapture, GameMembers.ScreensUiInput
                 })
        {
            if (!member.TryResolve())
            {
                missing.Add(member.Name);
            }
        }

        if (missing.Count > 0)
        {
            throw Changed(string.Join(", ", missing));
        }
    }

    // ConditionalWeakTable<TKey, BoardState>.TryGetValue for a holder of the table's key type; null otherwise.
    private static object? StateIn(object? table, object holder)
    {
        if (table == null || !(table.GetType().GetGenericArguments() is { Length: 2 } types) ||
            !types[0].IsInstanceOfType(holder))
        {
            return null;
        }

        object?[] arguments = { holder, null };
        bool found = table.GetType().GetMethod("TryGetValue")?.Invoke(table, arguments) is true;
        return found ? arguments[1] : null;
    }

    private static ScreenElementData ElementOf(object element)
    {
        string id = Field(element, "Id") as string ?? string.Empty;
        string type = Field(element, "Type") as string ?? string.Empty;
        object rect = Field(element, "Rect") ?? throw Changed("UiElement.Rect");
        JObject props = PropsOf(Field(element, "Props"));
        JObject style = PropsOf(Field(element, "Style"));
        bool normalized = Convert.ToInt32(Field(rect, "Unit")) != 0;
        ScreenElement model = new ScreenElement(id, type, normalized, Number(rect, "X"), Number(rect, "Y"),
            Number(rect, "W"), Number(rect, "H"), Text(props, "parent_id"),
            ScreenElement.ZOf(Text(props, "z_index") ?? Text(props, "zIndex")), ScreenElement.VisibleOf(Text(props, "visible")));
        return new ScreenElementData(model, props, style);
    }

    private static string? Text(JObject props, string key) =>
        props.TryGetValue(key, out JToken? token) && token.Type != JTokenType.Null ? token.ToString() : null;

    private static double Number(object owner, string name) => Convert.ToDouble(Field(owner, name));

    // UiProp[] as an object: each Key with its UiValue (Nil, Number, Bool, String, Array, Map).
    private static JObject PropsOf(object? props)
    {
        JObject result = new JObject();
        if (props is Array array)
        {
            foreach (object? prop in array)
            {
                if (prop != null && Field(prop, "Key") is string key)
                {
                    result[key] = ValueOf(Field(prop, "Value"), 0);
                }
            }
        }

        return result;
    }

    private static JToken ValueOf(object? value, int depth)
    {
        if (value == null || depth > 16)
        {
            return JValue.CreateNull();
        }

        switch (Convert.ToInt32(Field(value, "Type")))
        {
            case 1:
                return new JValue(Math.Round(Convert.ToDouble(Field(value, "Number")), 4));
            case 2:
                return new JValue(Field(value, "Bool") is true);
            case 3:
                return new JValue(Field(value, "String") as string ?? string.Empty);
            case 4:
                JArray items = new JArray();
                if (Field(value, "Array") is Array array)
                {
                    foreach (object? item in array)
                    {
                        items.Add(ValueOf(item, depth + 1));
                    }
                }

                return items;
            case 5:
                return PropsOf(Field(value, "Map"));
            default:
                return JValue.CreateNull();
        }
    }

    private static IDictionary Dictionary(object owner, string name) =>
        Field(owner, name) as IDictionary ?? throw Changed($"{owner.GetType().Name}.{name}");

    private static object? Field(object owner, string name) => FieldOf(owner.GetType(), name).GetValue(owner);

    private static void Set(object owner, string name, object value) => FieldOf(owner.GetType(), name).SetValue(owner, value);

    private static FieldInfo FieldOf(Type type, string name)
    {
        lock (Fields)
        {
            if (!Fields.TryGetValue((type, name), out FieldInfo field))
            {
                field = type.GetField(name, Instance) ?? throw Changed($"{type.Name}.{name}");
                Fields[(type, name)] = field;
            }

            return field;
        }
    }

    private static ApiException Changed(string member) =>
        ApiErrors.Refused("screens_changed",
            $"ScriptedScreens has changed: {member} is gone, so the screen tools cannot read it. StationGod needs an " +
            "update for this ScriptedScreens version.");
}

/// <summary>An element's place in the layout and its props and style as the script set them.</summary>
internal sealed class ScreenElementData
{
    internal ScreenElementData(ScreenElement element, JObject props, JObject style)
    {
        Element = element;
        Props = props;
        Style = style;
    }

    internal ScreenElement Element { get; }

    internal JObject Props { get; }

    internal JObject Style { get; }
}
