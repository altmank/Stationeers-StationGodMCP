#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects.Entities;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Screens;
using UnityEngine;

namespace StationGodMCP.Api;

/// <summary>
/// screen_read: what a ScriptedScreens screen (a Console or Computer board, a tablet cartridge, a visor) shows, from the
/// host's UI model (ScreenHost): its surfaces, the screens showing each, and one surface's elements with their boxes
/// on the surface, props and, on request, style; image true adds ScriptedScreens' own PNG of the drawn surface where the
/// game draws (not on a dedicated server). Read only.
/// </summary>
internal static class ScreenReadApi
{
    private const int DefaultLimit = ReplyDefaults.ScreenElements;
    private const int MaximumLimit = 500;

    internal static ScreenReadView Handle(Args args)
    {
        DeviceScope scope = Devices.Scope(args);
        ScreenHost host = ScreenHost.Require(Devices.RequireCircuitHolder(scope, args));
        string surface = Screens.SurfaceOf(host, args.OptionalString("surface"));
        List<ScreenElementData> elements = Screens.ElementsOf(host, surface);
        Vector2? size = host.SizeOf(surface);
        ScreenLayout layout = Screens.LayoutOf(elements, size);
        string? type = args.OptionalString("type");
        string? text = args.OptionalString("text_contains");
        bool shownOnly = args.OptionalBool("shown_only") ?? true;
        bool style = args.OptionalBool("include_style") ?? false;
        List<ScreenElementView> kept = new List<ScreenElementView>();
        foreach (ScreenElementData element in elements)
        {
            if ((type == null || string.Equals(element.Element.Type, type, StringComparison.OrdinalIgnoreCase)) &&
                (!shownOnly || layout.IsShown(element.Element.Id)) && Screens.HoldsText(element.Props, text))
            {
                kept.Add(Screens.ViewOf(element, layout, style));
            }
        }

        PageRequest page = PageRequest.From(args, DefaultLimit, MaximumLimit);
        Slice<ScreenElementView> slice = Slice<ScreenElementView>.Of(kept, page);
        page.Note("elements", slice.Items.Count, kept.Count);
        return new ScreenReadView(host.Ic.HolderView, new ThingId(host.Chip.ReferenceId), Surfaces(host), surface,
            size?.x, size?.y, slice, (args.OptionalBool("image") ?? false) ? Image(host, surface) : null);
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

/// <summary>
/// screen_press: touch a control on a ScriptedScreens screen as a player's touch does: the event goes to the host's
/// input path (ScreenHost.Press), so the script's handler runs on its next tick and sees the player's name. A control
/// by element_id, or the one at a point on the surface (ScreenLayout.ControlAt). Writes; host only.
/// </summary>
internal static class ScreenPressApi
{
    private static readonly string[] Events = { "click", "change", "toggle" };

    internal static ScreenPressView Handle(Args args)
    {
        if (NetworkManager.IsClient && !NetworkManager.IsServer)
        {
            throw ApiErrors.Refused("not_host", "This game is a multiplayer client; screens run their scripts on the host.");
        }

        DeviceScope scope = Devices.Scope(args);
        ScreenHost host = ScreenHost.Require(Devices.RequireCircuitHolder(scope, args));
        string surface = Screens.SurfaceOf(host, args.OptionalString("surface"));
        string eventName = (args.OptionalString("event") ?? "click").Trim().ToLowerInvariant();
        if (Array.IndexOf(Events, eventName) < 0)
        {
            throw ApiErrors.InvalidArgument("Argument 'event' must be click, change or toggle.");
        }

        string? value = args.OptionalString("value");
        if (eventName == "change" && value == null)
        {
            throw ApiErrors.InvalidArgument("event change needs value: the control's new value as text.");
        }

        List<ScreenElementData> elements = Screens.ElementsOf(host, surface);
        ScreenLayout layout = Screens.LayoutOf(elements, host.SizeOf(surface));
        ScreenElementData target = Target(args, elements, layout, surface);
        string player = args.OptionalString("player") ?? LocalPlayerName();
        bool accepted = host.Press(surface, target.Element.Id, eventName, value ?? string.Empty, player);
        return new ScreenPressView(host.Ic.HolderView, surface, Screens.ViewOf(target, layout, false), eventName, value,
            player, accepted);
    }

    private static string LocalPlayerName()
    {
        Human? human = PlayerOrigin.Current().Player;
        return human != null ? human.DisplayName ?? "StationGod" : "StationGod";
    }

    private static ScreenElementData Target(Args args, List<ScreenElementData> elements, ScreenLayout layout,
        string surface)
    {
        bool byId = args.Has("element_id");
        if (byId == args.Has("at"))
        {
            throw ApiErrors.InvalidArgument("Pass element_id or at (a point [x, y] on the surface), one of them.");
        }

        if (byId)
        {
            string id = args.String("element_id");
            return elements.Find(element => element.Element.Id == id) ?? throw ApiErrors.Refused(
                "element_not_found", $"Surface '{surface}' has no element '{id}'; screen_read lists them.");
        }

        JArray at = args.Array("at", 2);
        if (at.Count != 2 || !double.TryParse(at[0].ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double x) ||
            !double.TryParse(at[1].ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double y))
        {
            throw ApiErrors.InvalidArgument("Argument 'at' must be [x, y]: surface pixels from the top-left corner.");
        }

        ScreenElement? hit = layout.ControlAt(elements.ConvertAll(static element => element.Element), x, y);
        return hit != null
            ? elements.Find(element => element.Element.Id == hit.Id)!
            : throw ApiErrors.Refused("no_control_at",
                $"No shown control stands at ({x}, {y}) on surface '{surface}'; screen_read gives each element's box.");
    }
}

/// <summary>What screen_read and screen_press share: the surface asked for, its elements, their layout and views.</summary>
internal static class Screens
{
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
