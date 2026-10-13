#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects.Entities;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Screens;
using UnityEngine;

namespace StationGodMCP.Api;

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
            throw ApiErrors.InvalidArgument(Vocabulary.Text("Argument '{arg:event}' must be click, change or toggle."));
        }

        string? value = args.OptionalString("value");
        if (eventName == "change" && value == null)
        {
            throw ApiErrors.InvalidArgument(Vocabulary.Text("{arg:event} change needs {arg:value}: the control's new " +
                "value as text."));
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
            throw ApiErrors.InvalidArgument(Vocabulary.Text("Pass {arg:element_id} or {arg:at} (a point [x, y] on " +
                "the surface), one of them."));
        }

        if (byId)
        {
            string id = args.String("element_id");
            return elements.Find(element => element.Element.Id == id) ?? throw ApiErrors.Refused(
                "element_not_found", $"Surface '{surface}' has no element '{id}'; {Vocabulary.Tool("screen_read")} " +
                    "lists them.");
        }

        JArray at = args.Array("at", 2);
        if (at.Count != 2 || !double.TryParse(at[0].ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double x) ||
            !double.TryParse(at[1].ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double y))
        {
            throw ApiErrors.InvalidArgument(Vocabulary.Text("Argument '{arg:at}' must be [x, y]: surface pixels from " +
                "the top-left corner."));
        }

        ScreenElement? hit = layout.ControlAt(elements.ConvertAll(static element => element.Element), x, y);
        return hit != null
            ? elements.Find(element => element.Element.Id == hit.Id)!
            : throw ApiErrors.Refused("no_control_at",
                $"No shown control stands at ({x}, {y}) on surface '{surface}'; {Vocabulary.Tool("screen_read")} " +
                "gives each element's box.");
    }
}
