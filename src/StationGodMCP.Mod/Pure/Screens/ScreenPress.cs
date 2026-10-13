#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Pure.Screens;

/// <summary>
/// One touch on a ScriptedScreens screen (write screen): the surface (null: the main screen's), the control by its
/// element id or the point [x, y] in surface pixels from the top left, the event (click, change or toggle), the new
/// value as text with change, and the name the script's handler receives (null: the player's).
/// </summary>
internal sealed class ScreenPress
{
    internal ScreenPress(string? surface, ScreenPress.Target on, string @event, string? text, string? asPlayer)
    {
        Surface = surface;
        On = on;
        Event = @event;
        Text = text;
        AsPlayer = asPlayer;
    }

    public string? Surface { get; }

    public ScreenPress.Target On { get; }

    public string Event { get; }

    public string? Text { get; }

    public string? AsPlayer { get; }

    internal const string Change = "change";

    /// <summary>The control touched: by element id, or the one at a point.</summary>
    internal abstract class Target
    {
        private Target()
        {
        }

        internal sealed class Element : Target
        {
            internal Element(string id)
            {
                Id = id;
            }

            public string Id { get; }
        }

        internal sealed class Pixel : Target
        {
            internal Pixel(double x, double y)
            {
                X = x;
                Y = y;
            }

            public double X { get; }

            public double Y { get; }
        }
    }

    /// <summary>The touch the options ask for; null with every problem added.</summary>
    internal static ScreenPress? Of(string? surface, string? element, JToken? pixel, string eventName, string? text,
        string? asPlayer, List<OptionProblem> problems)
    {
        Target? on = null;
        if (element != null && pixel != null)
        {
            problems.Add(new OptionProblem("pixel", "goes without element: touch one or the other"));
        }
        else if (element != null)
        {
            on = new Target.Element(element);
        }
        else if (pixel != null)
        {
            on = pixel is JArray { Count: 2 } point && OptionReader.NumberOf(point[0]) is { } x &&
                 OptionReader.NumberOf(point[1]) is { } y
                ? new Target.Pixel(x, y)
                : null;
            if (on == null)
            {
                problems.Add(new OptionProblem("pixel", "must be [x, y]: surface pixels from the top left"));
            }
        }
        else
        {
            problems.Add(new OptionProblem("element", "or pixel is needed: the element to touch, or a point [x, y]"));
        }

        if (eventName == Change && text == null)
        {
            problems.Add(new OptionProblem("text", "is needed with event change: the control's new value as text"));
        }

        return on != null && problems.Count == 0 ? new ScreenPress(surface, on, eventName, text, asPlayer) : null;
    }
}
