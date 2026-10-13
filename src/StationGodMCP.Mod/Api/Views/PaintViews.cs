#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Api.Views;

/// <summary>paint with no targets: the game's colour list.</summary>
internal sealed class PaintColorsView
{
    internal PaintColorsView(List<PaintColorView> colors)
    {
        Colors = colors;
        Count = colors.Count;
    }

    public List<PaintColorView> Colors { get; }

    public int Count { get; }
}

/// <summary>One colour of GameManager.CustomColors, its position in the list being its index.</summary>
internal sealed class PaintColorView
{
    internal PaintColorView(int index, string? name, bool paintOnly)
    {
        Index = index;
        Name = name;
        PaintOnly = paintOnly;
    }

    public int Index { get; }

    public string? Name { get; }

    /// <summary>Spray-only colours (the metallic cans): logic cannot set them, a spray can can.</summary>
    public bool PaintOnly { get; }
}

/// <summary>A thing's colour: its index (null when it has none), name, and whether it is the prefab's own.</summary>
internal sealed class ThingColorView
{
    internal ThingColorView(int? index, string? name, bool isDefault)
    {
        Index = index;
        Name = name;
        IsDefault = isDefault;
    }

    public int? Index { get; }

    public string? Name { get; }

    public bool IsDefault { get; }

    /// <summary>
    /// A colour by its place in the game's colour list (negative: not in it). A swatch the list does not hold is no
    /// colour: index and name null, since its name is an unlocalised placeholder ("&lt;A:EN:0&gt;"), and a thing's
    /// own paint only when it has a paintable material and the swatch paints with it.
    /// </summary>
    internal static ThingColorView Of(int index, string? name, bool isPrefabColor) =>
        index >= 0 ? new ThingColorView(index, name, isPrefabColor) : new ThingColorView(null, null, isPrefabColor);
}
