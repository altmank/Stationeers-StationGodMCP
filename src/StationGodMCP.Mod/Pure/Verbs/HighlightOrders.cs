#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// show highlight's call, checked without the game: targets (1 to 64, each exactly one of id (one id, or a list of up
/// to 1024), network and at, with a colour, a label of up to 80 characters and pulse), seconds (60 by default, at most
/// 600), keep and clear. A target without a colour takes the palette in turn by its index, so neighbouring targets
/// differ. The what's label prefixes every refusal ("show highlight: ...") and every problem names the target's path
/// (targets[2].color). clear only removes what is drawn, so targets, seconds and keep go without it.
/// </summary>
internal sealed class HighlightOrder
{
    /// <summary>The most targets one call draws (the option's maxItems).</summary>
    internal const int MaximumTargets = 64;

    /// <summary>The most ids one target's id list holds.</summary>
    internal const int MaximumIds = 1024;

    /// <summary>The longest label a target shows.</summary>
    internal const int MaximumLabel = 80;

    /// <summary>How long it shows when the call gives no seconds (the option's default).</summary>
    internal const double DefaultSeconds = 60.0;

    /// <summary>The longest it shows (the option's maximum).</summary>
    internal const double MaximumSeconds = 600.0;

    /// <summary>The three ways a target names what to highlight; a target takes exactly one of them.</summary>
    private static readonly string[] Forms = { "id", "network", "at", "ghost" };

    /// <summary>The keys a target takes, as an unknown key is answered with the nearest of them.</summary>
    private static readonly string[] Keys = { "id", "network", "at", "ghost", "color", "label", "pulse" };

    /// <summary>The options clear only removes what is drawn against.</summary>
    private static readonly string[] WithClear = { "targets", "seconds", "keep" };

    /// <summary>The palette's names, as the refusal about a colour lists them.</summary>
    private static readonly string ColourNames = string.Join(", ", HighlightRequest.ColorNames);

    private HighlightOrder(HighlightRequest? request, bool clear)
    {
        Request = request;
        Clear = clear;
    }

    /// <summary>The targets, the seconds and keep the screen draws; null with clear (nothing else is read then).</summary>
    public HighlightRequest? Request { get; }

    /// <summary>Only remove what is drawn.</summary>
    public bool Clear { get; }

    /// <summary>
    /// The order show highlight's options give; null with every problem added. given is the reader they were bound
    /// from: clear goes with targets, seconds and keep, and a defaulted option reads the same whether the call gave it
    /// or left it out, so only the reader says whether the call gave it.
    /// </summary>
    internal static HighlightOrder? Of(ShowHighlightOptions options, OptionReader given, List<OptionProblem> problems)
    {
        int before = problems.Count;
        if (options.Clear ?? false)
        {
            ClearOnly(given, problems);
            return problems.Count > before ? null : new HighlightOrder(null, true);
        }

        double seconds = options.Seconds ?? DefaultSeconds;
        if (seconds > MaximumSeconds)
        {
            problems.Add(new OptionProblem("seconds", $"is at most {MaximumSeconds}"));
        }

        List<HighlightTarget> targets = Targets(options.Targets, problems);
        return problems.Count == before
            ? new HighlightOrder(new HighlightRequest(targets, seconds, options.Keep ?? false), false)
            : null;
    }

    // clear only removes what is drawn: the options that draw something go without it.
    private static void ClearOnly(OptionReader given, List<OptionProblem> problems)
    {
        foreach (string name in WithClear)
        {
            if (given.Raw(name) != null)
            {
                problems.Add(new OptionProblem(name, "goes without clear: clear only removes what is drawn"));
            }
        }
    }

    // The targets: the what's 1 to 64 of them, each read on its own so every problem comes back at once.
    private static List<HighlightTarget> Targets(JToken? given, List<OptionProblem> problems)
    {
        List<HighlightTarget> targets = new List<HighlightTarget>();
        if (!(given is JArray items) || items.Count == 0 || items.Count > MaximumTargets)
        {
            problems.Add(new OptionProblem("targets",
                $"is 1 to {MaximumTargets} targets, each {{id, network or at}}"));
            return targets;
        }

        for (int index = 0; index < items.Count; index++)
        {
            string path = $"targets[{index}]";
            if (!(items[index] is JObject item))
            {
                problems.Add(new OptionProblem(path, "is an object: {id, network or at}"));
                continue;
            }

            HighlightTarget? target = Target(item, path, index, problems);
            if (target != null)
            {
                targets.Add(target);
            }
        }

        return targets;
    }

    // One target: exactly one of id, network and at, then its colour (the palette in turn when it names none), label
    // and pulse. Null with its problems added when what it named could not be read.
    private static HighlightTarget? Target(JObject item, string path, int index, List<OptionProblem> problems)
    {
        int forms = 0;
        List<ThingId>? ids = null;
        ThingId network = default;
        bool isNetwork = false;
        Vec3 at = default;
        bool isAt = false;
        long? ghost = null;
        (Tint Tint, string Name)? colour = null;
        bool coloured = false;
        string? label = null;
        bool pulse = false;
        foreach (JProperty key in item.Properties())
        {
            if (key.Value.Type == JTokenType.Null)
            {
                continue;      // JSON null is absent, as every option reads it
            }

            switch (key.Name)
            {
                case "id":
                    forms++;
                    ids = Ids(key.Value, path + ".id", problems);
                    break;
                case "network":
                    forms++;
                    isNetwork = ThingId.TryRead(key.Value, out network);
                    if (!isNetwork)
                    {
                        problems.Add(new OptionProblem(path + ".network",
                            "is a network's id, or a piece on it, as a decimal string"));
                    }

                    break;
                case "at":
                    forms++;
                    isAt = PlacementOrders.Position(key.Value, path + ".at", problems, out Metres point);
                    if (isAt)
                    {
                        at = new Vec3(point.X, point.Y, point.Z);
                    }

                    break;
                case "ghost":
                    forms++;
                    if (key.Value.Type == JTokenType.Integer && (long)key.Value > 0)
                    {
                        ghost = (long)key.Value;
                    }
                    else
                    {
                        problems.Add(new OptionProblem(path + ".ghost", "is a ghost id (find ghost lists them)"));
                    }

                    break;
                case "color":
                    coloured = true;
                    colour = Colour(key.Value, path + ".color", index, problems);
                    break;
                case "label":
                    label = Label(key.Value, path + ".label", problems);
                    break;
                case "pulse":
                    if (key.Value.Type != JTokenType.Boolean)
                    {
                        problems.Add(new OptionProblem(path + ".pulse", "is true or false"));
                    }
                    else
                    {
                        pulse = (bool)key.Value;
                    }

                    break;
                default:
                    problems.Add(new OptionProblem($"{path}.{key.Name}", "is not a target key",
                        NearestNames.Of(key.Name, Keys)));
                    break;
            }
        }

        if (forms != 1)
        {
            problems.Add(new OptionProblem(path, $"takes exactly one of {string.Join(", ", Forms)}"));
            return null;
        }

        if (coloured && colour == null)
        {
            return null;      // the colour's problem is listed
        }

        (Tint tint, string name) = colour ?? HighlightRequest.Turn(index);
        if (ids != null)
        {
            return new HighlightTarget.Things(ids, tint, name, label, pulse);
        }

        if (isNetwork)
        {
            return new HighlightTarget.Network(network, tint, name, label, pulse);
        }

        if (ghost.HasValue)
        {
            return new HighlightTarget.Ghost(ghost.Value, tint, name, label, pulse);
        }

        // The one form given that could not be read is no target: its problem is listed.
        return isAt ? new HighlightTarget.Point(at, tint, name, label, pulse) : (HighlightTarget?)null;
    }

    // id: one thing's id, or a list of up to 1024; null with every problem the list has.
    private static List<ThingId>? Ids(JToken given, string path, List<OptionProblem> problems)
    {
        List<ThingId> ids = new List<ThingId>();
        if (given is JArray list)
        {
            if (list.Count == 0 || list.Count > MaximumIds)
            {
                problems.Add(new OptionProblem(path, $"is 1 to {MaximumIds} ids"));
                return null;
            }

            bool read = true;
            for (int index = 0; index < list.Count; index++)
            {
                if (ThingId.TryRead(list[index], out ThingId id))
                {
                    ids.Add(id);
                }
                else
                {
                    problems.Add(new OptionProblem($"{path}[{index}]", "is a thing's id, as a decimal string"));
                    read = false;
                }
            }

            return read ? ids : null;
        }

        if (ThingId.TryRead(given, out ThingId one))
        {
            ids.Add(one);
            return ids;
        }

        problems.Add(new OptionProblem(path, "is a thing's id, or a list of them, as decimal strings"));
        return null;
    }

    // color: a palette name, or [r, g, b] each 0 to 1; none: the palette in turn by the target's index.
    private static (Tint Tint, string Name)? Colour(JToken given, string path, int index, List<OptionProblem> problems)
    {
        if (given.Type == JTokenType.String)
        {
            string name = given.Value<string>()!.Trim();
            if (HighlightRequest.ByName(name) is { } known)
            {
                return known;
            }

            problems.Add(new OptionProblem(path,
                $"is one of {ColourNames}, or [r, g, b] with each 0 to 1",
                NearestNames.Of(name, HighlightRequest.ColorNames)));
            return null;
        }

        if (given is JArray rgb && rgb.Count == 3 && Channel(rgb[0], out double red) &&
            Channel(rgb[1], out double green) && Channel(rgb[2], out double blue))
        {
            return (new Tint(red, green, blue), HighlightRequest.Custom);
        }

        problems.Add(new OptionProblem(path, $"is one of {ColourNames}, or [r, g, b] with each 0 to 1"));
        return null;
    }

    // label: the text shown at the target, up to 80 characters; blank is none.
    private static string? Label(JToken given, string path, List<OptionProblem> problems)
    {
        if (given.Type != JTokenType.String)
        {
            problems.Add(new OptionProblem(path, "is the text shown at the target"));
            return null;
        }

        string trimmed = ((string)given!).Trim();
        if (trimmed.Length > MaximumLabel)
        {
            problems.Add(new OptionProblem(path, $"is at most {MaximumLabel} characters"));
            return null;
        }

        return trimmed.Length == 0 ? null : trimmed;
    }

    // One channel of an [r, g, b] colour: 0 to 1.
    private static bool Channel(JToken token, out double value)
    {
        value = 0.0;
        if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
        {
            return false;
        }

        value = token.Value<double>();
        return value >= 0 && value <= 1;
    }
}
