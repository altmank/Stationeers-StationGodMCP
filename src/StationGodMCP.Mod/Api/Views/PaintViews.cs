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
}

/// <summary>A thing that was painted, with its colour before and after.</summary>
internal sealed class PaintedView : BatchItemView
{
    internal PaintedView(int index, ThingId referenceId, ThingColorView previousColor, ThingColorView color)
        : base(index, ok: true)
    {
        ReferenceId = referenceId;
        PreviousColor = previousColor;
        Color = color;
    }

    public ThingId ReferenceId { get; }

    public ThingColorView PreviousColor { get; }

    public ThingColorView Color { get; }
}

/// <summary>A thing that was not painted: why, and its colour when it could be read.</summary>
internal sealed class NotPaintedView : BatchItemView
{
    internal NotPaintedView(int index, ThingId? referenceId, ThingColorView? previousColor, ApiException error)
        : base(index, ok: false)
    {
        ReferenceId = referenceId;
        PreviousColor = previousColor;
        Error = new ErrorView(error.Code, error.Message);
    }

    public ThingId? ReferenceId { get; }

    public ThingColorView? PreviousColor { get; }

    public ErrorView Error { get; }
}
