#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Api.Views;

/// <summary>screen_read: a ScriptedScreens host's surfaces and one surface's elements, a page of them.</summary>
internal sealed class ScreenReadView
{
    internal ScreenReadView(ThingView holder, ThingId chipId, List<ScreenSurfaceView> surfaces, string surface,
        double? width, double? height, Slice<ScreenElementView> page, ScreenImageView? image)
    {
        Holder = holder;
        ChipId = chipId;
        Surfaces = surfaces;
        Surface = surface;
        Width = width;
        Height = height;
        Elements = page.Items;
        Count = page.Items.Count;
        Total = page.Total;
        Offset = page.Offset;
        Limit = page.Limit;
        HasMore = page.HasMore;
        Image = image;
    }

    public ThingView Holder { get; }

    public ThingId ChipId { get; }

    public List<ScreenSurfaceView> Surfaces { get; }

    /// <summary>The surface the elements are from.</summary>
    public string Surface { get; }

    /// <summary>Its virtual resolution, when the script set one; null otherwise.</summary>
    public double? Width { get; }

    public double? Height { get; }

    public List<ScreenElementView> Elements { get; }

    public int Count { get; }

    public int Total { get; }

    public int Offset { get; }

    public int Limit { get; }

    public bool HasMore { get; }

    /// <summary>With image true: the PNG ScriptedScreens drew of the surface.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ScreenImageView? Image { get; }
}

/// <summary>A surface, how many elements it holds, and the screens that show it.</summary>
internal sealed class ScreenSurfaceView
{
    internal ScreenSurfaceView(string name, int elementCount, List<int> shownOn)
    {
        Name = name;
        ElementCount = elementCount;
        ShownOn = shownOn;
    }

    public string Name { get; }

    public int ElementCount { get; }

    /// <summary>Screen indexes showing it (0 the main screen); empty when it is not shown.</summary>
    public List<int> ShownOn { get; }
}

/// <summary>One element: what it is, where it stands on the surface, and its props as the script set them.</summary>
internal sealed class ScreenElementView
{
    internal ScreenElementView(string id, string type, ScreenBoxView? box, bool shown, bool touch, JObject props,
        JObject? style)
    {
        Id = id;
        Type = type;
        Box = box;
        Shown = shown;
        Touch = touch;
        Props = props;
        Style = style;
    }

    public string Id { get; }

    public string Type { get; }

    /// <summary>Its box in surface pixels from the top-left; null when it needs a surface size the script did not set.</summary>
    public ScreenBoxView? Box { get; }

    /// <summary>It and every element it is placed in are visible.</summary>
    public bool Shown { get; }

    /// <summary>A control a touch acts on (button, checkbox, toggle, slider...).</summary>
    public bool Touch { get; }

    public JObject Props { get; }

    /// <summary>With include_style only.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public JObject? Style { get; }
}

/// <summary>A box in surface pixels.</summary>
internal sealed class ScreenBoxView
{
    internal ScreenBoxView(double x, double y, double w, double h)
    {
        X = System.Math.Round(x, 1);
        Y = System.Math.Round(y, 1);
        W = System.Math.Round(w, 1);
        H = System.Math.Round(h, 1);
    }

    public double X { get; }

    public double Y { get; }

    public double W { get; }

    public double H { get; }
}

/// <summary>A drawn surface saved as a PNG.</summary>
internal sealed class ScreenImageView
{
    internal ScreenImageView(string path, string surface, int width, int height)
    {
        Path = path;
        Surface = surface;
        Width = width;
        Height = height;
    }

    public string Path { get; }

    public string Surface { get; }

    public int Width { get; }

    public int Height { get; }
}

/// <summary>screen_press: the touch sent, and the element it went to.</summary>
internal sealed class ScreenPressView
{
    internal ScreenPressView(ThingView holder, string surface, ScreenElementView element, string eventName,
        string? value, string player, bool accepted)
    {
        Holder = holder;
        Surface = surface;
        Element = element;
        Event = eventName;
        Value = value;
        Player = player;
        Accepted = accepted;
    }

    public ThingView Holder { get; }

    public string Surface { get; }

    public ScreenElementView Element { get; }

    public string Event { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Value { get; }

    /// <summary>The name the script's handler receives as the player.</summary>
    public string Player { get; }

    /// <summary>ScriptedScreens took the event; the script's handler runs on its next tick.</summary>
    public bool Accepted { get; }
}
