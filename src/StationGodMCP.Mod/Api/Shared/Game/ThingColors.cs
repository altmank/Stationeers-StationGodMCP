#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Util;
using StationGodMCP.Api.Views;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>The colours things show, by the game's colour list.</summary>
internal static class ThingColors
{
    /// <summary>The game's colour list, its position in the list being each colour's index; empty with no game loaded.</summary>
    internal static List<ColorSwatch> Swatches() =>
        Singleton<GameManager>.Instance != null
            ? Singleton<GameManager>.Instance.CustomColors
            : new List<ColorSwatch>();

    /// <summary>
    /// The colour a thing shows when it has one, as write paint reads it (a paintable thing's paint, a state-coloured
    /// thing's state colour); null for a thing with neither.
    /// </summary>
    internal static ThingColorView? ShownColorOf(Thing thing, List<ColorSwatch> swatches) =>
        thing.IsPaintable || thing.HasColorState ? ColorOf(thing, swatches) : null;

    /// <summary>
    /// The colour a thing shows: for a thing whose colour is a state (Thing.HasColorState, e.g. the LED display) the
    /// state the Color logic type reads and writes (Thing.ColorState, an index into the colour list), which is what it
    /// shows; for any other thing its paint (Thing.CustomColor). CustomColor of a state-coloured thing is a paint it
    /// does not show.
    /// </summary>
    internal static ThingColorView ColorOf(Thing thing, List<ColorSwatch> swatches)
    {
        ColorSwatch? swatch = thing.CustomColor;
        int index = GameManager.GetColorIndex(swatch);
        if (thing.HasColorState)
        {
            index = thing.ColorState;
            swatch = index >= 0 && index < swatches.Count ? swatches[index] : null;
        }

        Material? normal = swatch != null ? swatch.Normal : null;
        return ThingColorView.Of(index, swatch != null ? swatch.DisplayName : null,
            thing.PaintableMaterial != null && normal == thing.PaintableMaterial);
    }
}
