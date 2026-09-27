#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Util;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using UnityEngine;

namespace StationGodMCP.Api;

/// <summary>
/// paint: paint things with the game's own paint, as the spray can does, and list the colours. Writes.
///
/// How the game paints (CODE): a spray can's ISprayer.DoSpray runs only when Thing.IsPaintable and not
/// Thing.HasColorState (Thing.AttackWith), and after using up the paint calls OnServer.SetCustomColor(thing, index).
/// That does nothing when thing.HasColorState (the colour is an animator state, e.g. a light), else calls
/// Thing.SetCustomColor(index) and, on a client, sends a ThingColorMessage. This calls the same
/// OnServer.SetCustomColor, with no paint used. Thing.SetCustomColor ignores an index that fails
/// GameManager.IsValidColor, sets CustomColor and network flag 32, so clients see it. It is saved:
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
internal static class PaintApi
{
    internal const int MaximumTargets = 256;

    internal static object Handle(Args args)
    {
        List<ColorSwatch> swatches = Singleton<GameManager>.Instance != null
            ? Singleton<GameManager>.Instance.CustomColors
            : throw ApiErrors.Refused("not_ready", "The game's colour list is not loaded yet.");
        switch (PaintRequest.Parse(args))
        {
            case PaintRequest.ColorList _:
                return ListColors(swatches);
            case PaintRequest.Orders orders:
                return PaintAll(orders.List, swatches);
            default:
                throw ApiErrors.InvalidArgument("Unknown paint form.");
        }
    }

    private static PaintColorsView ListColors(List<ColorSwatch> swatches)
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

    private static BatchResultView PaintAll(List<PaintOrder> orders, List<ColorSwatch> swatches)
    {
        BatchBuilder batch = new BatchBuilder(orders.Count);
        for (int index = 0; index < orders.Count; index++)
        {
            PaintOne(batch, index, orders[index], swatches);
        }

        return batch.Build();
    }

    private static void PaintOne(BatchBuilder batch, int index, PaintOrder order, List<ColorSwatch> swatches)
    {
        if (!ThingId.TryRead(order.Id, out ThingId id))
        {
            batch.Failed(new NotPaintedView(index, null, null, ApiErrors.InvalidArgument(
                "Argument 'reference_id' must be a reference id as a decimal string.")));
            return;
        }

        ApiException? refusal = PaintColor.TryRead(order.Color, out string? colorText);
        Thing? thing = null;
        if (refusal == null && !GameLookup.TryFindThing(id, out thing))
        {
            refusal = ApiErrors.ThingNotFound(id);
        }

        ThingColorView? previous = thing != null ? ColorOf(thing) : null;
        refusal ??= Painter.Paint(thing!, colorText!, swatches);
        if (refusal != null)
        {
            batch.Failed(new NotPaintedView(index, id, previous, refusal));
            return;
        }

        batch.Succeeded(new PaintedView(index, id, previous!, ColorOf(thing!)));
    }

    internal static ThingColorView ColorOf(Thing thing)
    {
        ColorSwatch swatch = thing.CustomColor;
        int index = GameManager.GetColorIndex(swatch);
        Material? normal = swatch != null ? swatch.Normal : null;
        return new ThingColorView(index >= 0 ? index : (int?)null, swatch != null ? swatch.DisplayName : null,
            normal == thing.PaintableMaterial);
    }
}

/// <summary>What to paint which colour: one colour for many ids, or a colour per id.</summary>
internal abstract class PaintRequest
{
    private PaintRequest()
    {
    }

    internal static PaintRequest Parse(Args args)
    {
        bool ids = args.Has("reference_ids");
        bool items = args.Has("items");
        bool color = args.Has("color");
        if (ids && items)
        {
            throw ApiErrors.InvalidArgument("Pass reference_ids with color, or items, not both.");
        }

        if (items && color)
        {
            throw ApiErrors.InvalidArgument(
                "Argument 'color' applies only to reference_ids; each item carries its own color.");
        }

        if (ids != color)
        {
            throw ApiErrors.InvalidArgument("Arguments 'reference_ids' and 'color' go together.");
        }

        return ids ? SameColor(args) : items ? PerItem(args) : new ColorList();
    }

    private static Orders SameColor(Args args)
    {
        JToken color = new JValue(args.String("color"));
        List<PaintOrder> list = new List<PaintOrder>();
        foreach (JToken id in args.Array("reference_ids", PaintApi.MaximumTargets))
        {
            list.Add(new PaintOrder(id, color));
        }

        return new Orders(list);
    }

    private static Orders PerItem(Args args)
    {
        JArray items = args.Array("items", PaintApi.MaximumTargets);
        List<PaintOrder> list = new List<PaintOrder>(items.Count);
        for (int index = 0; index < items.Count; index++)
        {
            JObject item = items[index] as JObject
                ?? throw ApiErrors.InvalidArgument($"Argument 'items[{index}]' must be an object.");
            list.Add(new PaintOrder(item["reference_id"], item["color"]));
        }

        return new Orders(list);
    }

    internal sealed class ColorList : PaintRequest
    {
    }

    internal sealed class Orders : PaintRequest
    {
        internal Orders(List<PaintOrder> list)
        {
            List = list;
        }

        internal List<PaintOrder> List { get; }
    }
}

/// <summary>One thing and its colour as given; each is checked when painted, so one bad entry fails alone.</summary>
internal sealed class PaintOrder
{
    internal PaintOrder(JToken? id, JToken? color)
    {
        Id = id;
        Color = color;
    }

    internal JToken? Id { get; }

    internal JToken? Color { get; }
}

/// <summary>Resolves a colour as given: a name, an index, or "default".</summary>
internal static class PaintColor
{
    private const string DefaultWord = "default";

    internal static ApiException? TryRead(JToken? token, out string? text)
    {
        text = token != null && token.Type == JTokenType.String ? token.Value<string>() : null;
        if (token == null || token.Type == JTokenType.Null)
        {
            return ApiErrors.InvalidArgument("Argument 'color' is required.");
        }

        return text == null ? ApiErrors.InvalidArgument("Argument 'color' must be a string.") : null;
    }

    internal static ApiException? Resolve(string given, Thing thing, List<ColorSwatch> swatches, out int index)
    {
        index = -1;
        string text = given.Trim();
        if (text.Length == 0)
        {
            return ApiErrors.Refused("invalid_color", "Empty color.");
        }

        if (string.Equals(text, DefaultWord, StringComparison.OrdinalIgnoreCase))
        {
            index = GameManager.GetColorIndex(thing.PaintableMaterial);
            return index >= 0 ? null : ApiErrors.Refused(
                "invalid_color",
                $"{thing.DisplayName} has no prefab colour to restore (its paint is a mask material).");
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out index))
        {
            return GameManager.IsValidColor(index) ? null : ApiErrors.Refused(
                "invalid_color", $"Colour index {index} is not between 0 and {swatches.Count - 1}.");
        }

        return ByName(text, swatches, out index);
    }

    // The localised DisplayName first, then the internal Name; a name that matches two colours is refused.
    private static ApiException? ByName(string text, List<ColorSwatch> swatches, out int match)
    {
        match = -1;
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
                    return ApiErrors.Refused(
                        "invalid_color",
                        $"Colour name '{text}' matches more than one colour; pass its index.");
                }

                match = index;
            }
        }

        return match >= 0 ? null : ApiErrors.Refused(
            "invalid_color",
            $"'{text}' is not a colour name, a colour index or 'default'.");
    }
}

/// <summary>Paints one thing through OnServer.SetCustomColor and reads the colour back.</summary>
internal static class Painter
{
    internal static ApiException? Paint(Thing thing, string colorText, List<ColorSwatch> swatches)
    {
        int colorIndex = -1;
        ApiException? refusal = Refusal(thing);
        if (refusal == null)
        {
            refusal = PaintColor.Resolve(colorText, thing, swatches, out colorIndex);
        }

        if (refusal != null)
        {
            return refusal;
        }

        try
        {
            OnServer.SetCustomColor(thing, colorIndex);
        }
        catch (NotImplementedException)
        {
            // Structure.SetCustomColor throws this for a render mode the check above did not know.
            return ApiErrors.Refused(
                "not_paintable",
                $"{thing.DisplayName} refused the paint (its SetCustomColor is not implemented).");
        }

        int now = GameManager.GetColorIndex(thing.CustomColor);
        return now == colorIndex ? null : ApiErrors.Refused(
            "paint_failed", $"{thing.DisplayName} still shows colour index {now} after painting it {colorIndex}.");
    }

    private static ApiException? Refusal(Thing thing)
    {
        if (thing.HasColorState)
        {
            return ApiErrors.Refused(
                "has_color_state",
                $"{thing.DisplayName} keeps its colour as an animator state; the spray can does not paint it.");
        }

        if (!thing.IsPaintable)
        {
            return ApiErrors.Refused("not_paintable", $"{thing.DisplayName} has no paintable material.");
        }

        return thing is Structure structure && structure.structureRenderMode != StructureRenderMode.Standard
            ? ApiErrors.Refused(
                "not_paintable",
                $"{thing.DisplayName} is a batched structure, which the game cannot repaint.")
            : null;
    }
}
