#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects.Entities;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Screens;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// What a ScriptedScreens screen (a Console or Computer board, a tablet cartridge, a visor) shows, as inspect screen reads
/// it, from the host's UI model (ScreenHost): its surfaces, the screens showing each, and one surface's elements with
/// their boxes on the surface, props and, on request, style; image adds ScriptedScreens' own PNG of the drawn surface
/// where the game draws (not on a dedicated server). Read only.
/// </summary>
internal static class ScreenReads
{
    /// <summary>
    /// The screen of the circuit holder, one surface (null: the main screen's), its elements kept by type, text and
    /// shownOnly, one page of them (offset, at most limit) built for the page alone (KeyedPage: an element's key is its
    /// place in the surface's list); a walk's later page describes the elements at its kept places as they are now.
    /// </summary>
    internal static ScreenReadView Read(IcTarget holder, ScreenQuery query, WalkRequest walk, int offset, int limit)
    {
        ScreenHost host = ScreenHost.Require(holder);
        string surface = Screens.SurfaceOf(host, query.Surface);
        List<ScreenElementData> elements = Screens.ElementsOf(host, surface);
        Vector2? size = host.SizeOf(surface);
        ScreenLayout layout = Screens.LayoutOf(elements, size);
        string? type = query.Type;
        string? text = query.Text;
        bool shownOnly = query.ShownOnly;
        bool style = query.Style;
        Func<long, ScreenElementView?> describe = key =>
            key >= 0 && key < elements.Count ? Screens.ViewOf(elements[(int)key], layout, style) : null;
        Slice<ScreenElementView> slice;
        if (walk.Continues)
        {
            slice = KeyedPage.Later(walk, limit, describe);
        }
        else
        {
            List<long> kept = new List<long>();
            for (int index = 0; index < elements.Count; index++)
            {
                ScreenElementData element = elements[index];
                if ((type == null || string.Equals(element.Element.Type, type, StringComparison.OrdinalIgnoreCase)) &&
                    (!shownOnly || layout.IsShown(element.Element.Id)) && Screens.HoldsText(element.Props, text))
                {
                    kept.Add(index);
                }
            }

            slice = KeyedPage.First(walk, kept.ToArray(), offset, limit, describe);
        }
        return new ScreenReadView(host.Ic.HolderView, new ThingId(host.Chip.ReferenceId), Surfaces(host), surface,
            size?.x, size?.y, slice, query.Image ? Image(host, surface) : null);
    }

    private static List<ScreenSurfaceView> Surfaces(ScreenHost host)
    {
        SortedDictionary<int, string> active = host.ActiveSurfaces();
        return host.SurfaceNames().ConvertAll(name =>
        {
            List<int> shownOn = new List<int>();
            foreach (KeyValuePair<int, string> screen in active)
            {
                if (screen.Value == name)
                {
                    shownOn.Add(screen.Key);
                }
            }

            return new ScreenSurfaceView(name, host.ElementsOf(name)?.Count ?? 0, shownOn);
        });
    }

    private static ScreenImageView Image(ScreenHost host, string surface)
    {
        if (Application.isBatchMode)
        {
            throw ApiErrors.Refused("not_rendered",
                "This game draws nothing (a dedicated server), so there is no picture of the screen; read the " +
                "elements instead, or ask from a game that shows the screen.");
        }

        if (!host.TryCapture(surface, out string path, out string captured, out int width, out int height,
                out string error))
        {
            throw ApiErrors.Refused("capture_failed", $"ScriptedScreens could not picture surface '{surface}': {error}");
        }

        return new ScreenImageView(path, captured, width, height);
    }
}

/// <summary>What inspect screen asks of one surface: which, the elements kept, style and the picture.</summary>
internal sealed class ScreenQuery
{
    internal ScreenQuery(string? surface, string? type, string? text, bool shownOnly, bool style, bool image)
    {
        Surface = surface;
        Type = type;
        Text = text;
        ShownOnly = shownOnly;
        Style = style;
        Image = image;
    }

    public string? Surface { get; }

    public string? Type { get; }

    public string? Text { get; }

    public bool ShownOnly { get; }

    public bool Style { get; }

    public bool Image { get; }
}

/// <summary>
/// What reading and pressing a screen share: the surface asked for, its elements, their layout and views; and the
/// press itself: a touch as a player's touch does, through the host's input path (ScreenHost.Press), so the script's
/// handler runs on its next tick and sees the player's name. Host only.
/// </summary>
internal static class Screens
{
    private const string LocalName = "StationGod";

    /// <summary>The touch on the circuit holder's screen: the control it reached and whether the screen took it.</summary>
    internal static ScreenPressView Press(IcTarget holder, ScreenPress press)
    {
        if (NetworkManager.IsClient && !NetworkManager.IsServer)
        {
            throw ApiErrors.Refused("not_host", "This game is a multiplayer client; screens run their scripts on the host.");
        }

        ScreenHost host = ScreenHost.Require(holder);
        string surface = SurfaceOf(host, press.Surface);
        List<ScreenElementData> elements = ElementsOf(host, surface);
        ScreenLayout layout = LayoutOf(elements, host.SizeOf(surface));
        ScreenElementData target = Touched(press.On, elements, layout, surface);
        string player = press.AsPlayer ?? LocalPlayerName();
        bool accepted = host.Press(surface, target.Element.Id, press.Event, press.Text ?? string.Empty, player);
        return new ScreenPressView(host.Ic.HolderView, surface, ViewOf(target, layout, false), press.Event, press.Text,
            player, accepted);
    }

    private static string LocalPlayerName()
    {
        Human? human = PlayerOrigin.Current().Player;
        return human != null ? human.DisplayName ?? LocalName : LocalName;
    }

    private static ScreenElementData Touched(ScreenPress.Target on, List<ScreenElementData> elements,
        ScreenLayout layout, string surface)
    {
        switch (on)
        {
            case ScreenPress.Target.Element element:
                return elements.Find(found => found.Element.Id == element.Id) ?? throw ApiErrors.Refused(
                    "element_not_found",
                    $"element: surface '{surface}' has no element '{element.Id}'; inspect screen lists them.");
            case ScreenPress.Target.Pixel pixel:
                ScreenElement? hit = layout.ControlAt(elements.ConvertAll(static element => element.Element), pixel.X,
                    pixel.Y);
                return hit != null
                    ? elements.Find(found => found.Element.Id == hit.Id)!
                    : throw ApiErrors.Refused("no_control_at",
                        $"pixel: no shown control stands at ({pixel.X}, {pixel.Y}) on surface '{surface}'; inspect " +
                        "screen gives each element's box.");
            default:
                throw new InvalidOperationException("A touch is on an element or a pixel.");
        }
    }

    private static readonly string[] TextProps = { "text", "label", "title", "placeholder", "value" };

    /// <summary>The surface named, else the one the main screen shows, else the first by name.</summary>
    internal static string SurfaceOf(ScreenHost host, string? asked)
    {
        List<string> names = host.SurfaceNames();
        if (asked != null)
        {
            return names.Contains(asked)
                ? asked
                : throw ApiErrors.Refused("surface_not_found",
                    $"No surface '{asked}'; this screen has {(names.Count == 0 ? "none" : string.Join(", ", names))}.");
        }

        return host.ActiveSurfaces().TryGetValue(0, out string? shown) && names.Contains(shown)
            ? shown
            : names.Count > 0
                ? names[0]
                : throw ApiErrors.Refused("no_screen", "The chip has made no surface yet.");
    }

    internal static List<ScreenElementData> ElementsOf(ScreenHost host, string surface) =>
        host.ElementsOf(surface) ?? new List<ScreenElementData>();

    internal static ScreenLayout LayoutOf(List<ScreenElementData> elements, Vector2? size) =>
        new ScreenLayout(elements.ConvertAll(static element => element.Element), size?.x, size?.y);

    internal static bool HoldsText(JObject props, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        foreach (string key in TextProps)
        {
            if (props.TryGetValue(key, out JToken? value) &&
                value.ToString().IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    internal static ScreenElementView ViewOf(ScreenElementData element, ScreenLayout layout, bool style) =>
        new ScreenElementView(element.Element.Id, element.Element.Type,
            layout.BoxOf(element.Element.Id) is ScreenBox box ? new ScreenBoxView(box.X, box.Y, box.W, box.H) : null,
            layout.IsShown(element.Element.Id), element.Element.TakesTouch, element.Props,
            style ? element.Style : null);
}
