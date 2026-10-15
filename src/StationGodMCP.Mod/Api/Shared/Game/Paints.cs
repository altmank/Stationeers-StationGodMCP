#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Util;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Scheduling;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// Paint with the game's own paint, as the spray can does, and the game's colours.
///
/// How the game paints (CODE): a spray can's ISprayer.DoSpray runs only when Thing.IsPaintable and not
/// Thing.HasColorState (Thing.AttackWith), and after using up the paint calls OnServer.SetCustomColor(thing, index).
/// That does nothing when thing.HasColorState (the colour is a state the Color logic type sets, e.g. the LED display;
/// the lights have none and paint), else calls Thing.SetCustomColor(index) and, on a client, sends a
/// ThingColorMessage. This calls the same OnServer.SetCustomColor, with no paint used. Thing.SetCustomColor ignores an
/// index that fails GameManager.IsValidColor, sets CustomColor and network flag 32, so clients see it. It is saved:
/// Thing.InitialiseSaveData stores CustomColorIndex, or -1 when CustomColor.Normal == PaintableMaterial (the prefab's
/// own colour). Thing.IsPaintable is PaintableMaterial != null or HasPaintableMaskMaterial (suits, gas masks,
/// overalls, bobble heads). Structure.SetCustomColor throws NotImplementedException unless structureRenderMode is
/// Standard, so a batched structure is refused before trying.
///
/// Colours are GameManager.CustomColors (Singleton), a List of ColorSwatch whose position is the index;
/// ColorSwatch.DisplayName is the localised name and PaintOnly marks spray-only colours (the metallic cans).
/// "default" is each thing's prefab colour, GameManager.GetColorIndex(PaintableMaterial); a thing whose paint is a
/// mask material has none.
/// </summary>
internal static class Paints
{
    /// <summary>The game's colour list, refused before a world has loaded it.</summary>
    internal static List<ColorSwatch> Swatches() =>
        Singleton<GameManager>.Instance != null
            ? Singleton<GameManager>.Instance.CustomColors
            : throw ApiErrors.Refused("not_ready", "The game's colour list is not loaded yet.");

    /// <summary>Every colour, its position in the list being its index.</summary>
    internal static PaintColorsView Colors(List<ColorSwatch> swatches)
    {
        List<PaintColorView> colors = new List<PaintColorView>(swatches.Count);
        for (int index = 0; index < swatches.Count; index++)
        {
            ColorSwatch swatch = swatches[index];
            colors.Add(new PaintColorView(
                index, swatch != null ? swatch.DisplayName : null, swatch != null && swatch.PaintOnly));
        }

        return new PaintColorsView(colors);
    }

    /// <summary>
    /// A network's things, one paint each in the colour given: its pieces, and its devices when devices asks. The
    /// network is the kind named, else the one the network option names (NetworkHandles).
    /// </summary>
    internal static List<PaintOrder> NetworkOrders(PaintRequest.Network network)
    {
        const string option = "network";
        UpgradeFamily family = network.Kind is { } kind
            ? NetworkHandles.FamilyNamed(kind)
            : NetworkHandles.FamilyOf(network.Target, option);
        ThingId id = NetworkHandles.Resolve(network.Target, option, family);
        List<Thing> things = NetworkReader.Things(family.NetworkKind, id, network.Devices);
        List<PaintOrder> orders = new List<PaintOrder>(things.Count);
        foreach (Thing thing in things)
        {
            orders.Add(new PaintOrder(orders.Count,
                thing.ReferenceId.ToString(CultureInfo.InvariantCulture), network.Color));
        }

        return orders;
    }

    // Paints checked or applied per step of WriteSteps (a network paint holds thousands of pieces).
    private const int PaintsAStep = 32;

    /// <summary>
    /// The paints in order, each failing alone, as work over frames (review D, R-D-11): a batch a step, checked and
    /// reported (report), or painted, with the colour before and after; the results into results. An id may be
    /// "looking_at". A colour change needs no single-frame apply: each paint stands alone.
    /// </summary>
    internal static IEnumerable<object?> WriteSteps(IReadOnlyList<PaintOrder> orders, bool report, string label,
        List<ColorSwatch> swatches, Answer<List<PaintResult>> results)
    {
        List<PaintResult> written = new List<PaintResult>(orders.Count);
        for (int index = 0; index < orders.Count; index++)
        {
            Write(orders[index], report, label, swatches, written);
            if ((index + 1) % PaintsAStep == 0 && index + 1 < orders.Count)
            {
                yield return null;
            }
        }

        results.Value = written;
    }

    private static void Write(PaintOrder order, bool report, string label, List<ColorSwatch> swatches,
        List<PaintResult> results)
    {
        try
        {
            Thing thing = TargetIds.Thing(order.Id, label);
            int index = Painter.Check(thing, order.Color, swatches);
            ThingColorView previous = ThingColors.ColorOf(thing, swatches);
            if (report)
            {
                results.Add(new PaintResult.Planned(new ThingId(thing.ReferenceId), thing.DisplayName, previous,
                    NameOf(index, swatches) ?? order.Color));
                return;
            }

            Painter.Apply(thing, index);
            results.Add(new PaintResult.Painted(new ThingId(thing.ReferenceId), previous,
                ThingColors.ColorOf(thing, swatches)));
        }
        catch (ApiException refused)
        {
            results.Add(new PaintResult.Failed(order.Index, order.Id, VerbError.Of(refused)));
        }
    }

    private static string? NameOf(int index, List<ColorSwatch> swatches) =>
        index >= 0 && index < swatches.Count && swatches[index] != null ? swatches[index].DisplayName : null;
}

/// <summary>Resolves a colour as given: a name, an index, or "default".</summary>
internal static class PaintColor
{
    private const string DefaultWord = "default";

    /// <summary>The colour's index in the list; refused invalid_color when the text names none for this thing.</summary>
    internal static int Resolve(string given, Thing thing, List<ColorSwatch> swatches)
    {
        string text = given.Trim();
        if (text.Length == 0)
        {
            throw ApiErrors.Refused("invalid_color", "color: empty; give a colour name, an index, or 'default'.");
        }

        if (string.Equals(text, DefaultWord, StringComparison.OrdinalIgnoreCase))
        {
            int prefab = GameManager.GetColorIndex(thing.PaintableMaterial);
            return prefab >= 0
                ? prefab
                : throw ApiErrors.Refused("invalid_color",
                    $"{Names.Of(thing)} has no prefab colour to restore (its paint is a mask material).");
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
        {
            return GameManager.IsValidColor(index)
                ? index
                : throw ApiErrors.Refused("invalid_color",
                    $"color: index {index} is not between 0 and {swatches.Count - 1}.");
        }

        return ByName(text, swatches);
    }

    // The localised DisplayName first, then the internal Name; a name that matches two colours is refused.
    private static int ByName(string text, List<ColorSwatch> swatches)
    {
        int match = -1;
        for (int pass = 0; pass < 2 && match < 0; pass++)
        {
            for (int index = 0; index < swatches.Count; index++)
            {
                ColorSwatch swatch = swatches[index];
                string? name = swatch == null ? null : pass == 0 ? swatch.DisplayName : swatch.Name;
                if (!string.Equals(name, text, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (match >= 0)
                {
                    throw ApiErrors.Refused("invalid_color",
                        $"color: '{text}' matches more than one colour; give its index.");
                }

                match = index;
            }
        }

        return match >= 0
            ? match
            : throw ApiErrors.Refused("invalid_color",
                $"color: '{text}' is not a colour name, a colour index or 'default'.");
    }
}

/// <summary>Paints one thing through OnServer.SetCustomColor, checked first, and reads the colour back.</summary>
internal static class Painter
{
    /// <summary>The colour index the thing would be painted; refused when the spray can would not paint it.</summary>
    internal static int Check(Thing thing, string color, List<ColorSwatch> swatches)
    {
        if (thing.HasColorState)
        {
            throw ApiErrors.Refused("has_color_state",
                $"{Names.Of(thing)} keeps its colour as an animator state; the spray can does not paint it.");
        }

        if (!thing.IsPaintable)
        {
            throw ApiErrors.Refused("not_paintable", $"{Names.Of(thing)} has no paintable material.");
        }

        if (thing is Structure structure && structure.structureRenderMode != StructureRenderMode.Standard)
        {
            throw ApiErrors.Refused("not_paintable",
                $"{Names.Of(thing)} is a batched structure, which the game cannot repaint.");
        }

        return PaintColor.Resolve(color, thing, swatches);
    }

    /// <summary>Paints a checked thing; refused when the game would not take the colour.</summary>
    internal static void Apply(Thing thing, int colorIndex)
    {
        try
        {
            OnServer.SetCustomColor(thing, colorIndex);
        }
        catch (NotImplementedException)
        {
            // Structure.SetCustomColor throws this for a render mode the check above did not know.
            throw ApiErrors.Refused("not_paintable",
                $"{Names.Of(thing)} refused the paint: the game does not paint this kind of thing.");
        }

        int now = GameManager.GetColorIndex(thing.CustomColor);
        if (now != colorIndex)
        {
            throw ApiErrors.Refused("paint_failed",
                $"{Names.Of(thing)} still shows colour index {now} after painting it {colorIndex}.");
        }
    }
}
