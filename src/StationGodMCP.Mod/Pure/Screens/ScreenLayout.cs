#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;

namespace StationGodMCP.Pure.Screens;

/// <summary>
/// One element of a ScriptedScreens surface as the host's model holds it: its id and type, its rect (pixels from the
/// parent's top-left corner, y down, or fractions of the parent), the parent it is placed in, its z_index and whether
/// it is shown.
/// </summary>
internal sealed class ScreenElement
{
    internal ScreenElement(string id, string type, bool normalized, double x, double y, double w, double h,
        string? parentId, double z, bool visible)
    {
        Id = id;
        Type = type;
        Normalized = normalized;
        X = x;
        Y = y;
        W = w;
        H = h;
        ParentId = parentId;
        Z = z;
        Visible = visible;
    }

    internal string Id { get; }

    internal string Type { get; }

    internal bool Normalized { get; }

    internal double X { get; }

    internal double Y { get; }

    internal double W { get; }

    internal double H { get; }

    internal string? ParentId { get; }

    internal double Z { get; }

    internal bool Visible { get; }

    /// <summary>The element types a touch acts on.</summary>
    internal bool TakesTouch => Array.IndexOf(Controls, Type) >= 0;

    private static readonly string[] Controls =
        { "button", "interface_button", "checkbox", "radio", "toggle", "select", "slider", "textinput" };

    /// <summary>A prop's text as ScriptedScreens reads z_index: a number, or 0.</summary>
    internal static double ZOf(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double z) ? z : 0.0;

    /// <summary>ScriptedScreens' visible prop: hidden only by "false" or "0".</summary>
    internal static bool VisibleOf(string? text) =>
        string.IsNullOrEmpty(text) || !(string.Equals(text, "false", StringComparison.OrdinalIgnoreCase) || text == "0");
}

/// <summary>A box on the surface in pixels, from its top-left corner, y down.</summary>
internal readonly struct ScreenBox
{
    internal ScreenBox(double x, double y, double w, double h)
    {
        X = x;
        Y = y;
        W = w;
        H = h;
    }

    internal double X { get; }

    internal double Y { get; }

    internal double W { get; }

    internal double H { get; }

    internal bool Contains(double x, double y) => x >= X && x < X + W && y >= Y && y < Y + H;
}

/// <summary>
/// Where each element stands on the surface, as ScriptedScreens places it (ApplyRect): a pixel rect is an offset from
/// its parent's top-left corner, a normalized one a fraction of its parent; the parent is parent_id, else the scroll
/// view an id like "list/row3" sits in, else the surface. Scroll positions are not applied.
/// </summary>
internal sealed class ScreenLayout
{
    private readonly Dictionary<string, ScreenElement> _byId;
    private readonly Dictionary<string, ScreenBox?> _boxes = new Dictionary<string, ScreenBox?>(StringComparer.Ordinal);
    private readonly ScreenBox? _surface;

    /// <summary>width and height: the surface's virtual resolution, null when the script set none.</summary>
    internal ScreenLayout(IEnumerable<ScreenElement> elements, double? width, double? height)
    {
        _byId = new Dictionary<string, ScreenElement>(StringComparer.Ordinal);
        foreach (ScreenElement element in elements)
        {
            _byId[element.Id] = element;
        }

        _surface = width.HasValue && height.HasValue ? new ScreenBox(0, 0, width.Value, height.Value) : (ScreenBox?)null;
    }

    /// <summary>The element's box in surface pixels; null when it needs the surface size and the script set none.</summary>
    internal ScreenBox? BoxOf(string id) => BoxOf(id, 0);

    /// <summary>Whether the element and every parent it is placed in are shown.</summary>
    internal bool IsShown(string id)
    {
        for (int depth = 0; depth < 64 && _byId.TryGetValue(id, out ScreenElement element); depth++)
        {
            if (!element.Visible)
            {
                return false;
            }

            string? parent = ParentOf(element);
            if (parent == null)
            {
                return true;
            }

            id = parent;
        }

        return true;
    }

    /// <summary>
    /// The control a touch at (x, y) lands on: of the shown elements that take a touch and whose box holds the point,
    /// the one with the highest z_index, the later in the list on a tie; null for none.
    /// </summary>
    internal ScreenElement? ControlAt(IReadOnlyList<ScreenElement> ordered, double x, double y)
    {
        ScreenElement? found = null;
        foreach (ScreenElement element in ordered)
        {
            if (!element.TakesTouch || !IsShown(element.Id) || !(BoxOf(element.Id) is ScreenBox box) ||
                !box.Contains(x, y))
            {
                continue;
            }

            if (found == null || element.Z >= found.Z)
            {
                found = element;
            }
        }

        return found;
    }

    private ScreenBox? BoxOf(string id, int depth)
    {
        if (_boxes.TryGetValue(id, out ScreenBox? known))
        {
            return known;
        }

        if (!_byId.TryGetValue(id, out ScreenElement element) || depth > 64)
        {
            return null;
        }

        string? parentId = ParentOf(element);
        ScreenBox? parent = parentId != null ? BoxOf(parentId, depth + 1) : _surface;
        ScreenBox? box = element.Normalized
            ? parent is ScreenBox p ? new ScreenBox(p.X + element.X * p.W, p.Y + element.Y * p.H, element.W * p.W,
                element.H * p.H) : (ScreenBox?)null
            : parentId == null
                ? new ScreenBox(element.X, element.Y, element.W, element.H)
                : parent is ScreenBox q ? new ScreenBox(q.X + element.X, q.Y + element.Y, element.W, element.H) : (ScreenBox?)null;
        _boxes[id] = box;
        return box;
    }

    private string? ParentOf(ScreenElement element)
    {
        if (element.ParentId != null && _byId.ContainsKey(element.ParentId))
        {
            return element.ParentId;
        }

        int slash = element.Id.LastIndexOf('/');
        string? hierarchical = slash > 0 ? element.Id.Substring(0, slash) : null;
        return hierarchical != null && _byId.TryGetValue(hierarchical, out ScreenElement scroll) &&
               scroll.Type == "scrollview"
            ? hierarchical
            : null;
    }
}
