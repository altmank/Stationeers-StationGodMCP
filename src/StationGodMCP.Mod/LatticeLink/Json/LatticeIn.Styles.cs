#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.LatticeLink.Model;
using L = Lattice.Api;

namespace StationGodMCP.LatticeLink.Json;

/// <summary>
/// Lattice's styles read from JSON: every Style property (snake_case names), the looks under them, style matches, style
/// layers and highlights. A key is "owner/name" for a mod's, or one of Lattice's own bare names.
/// </summary>
internal sealed partial class LatticeIn
{
    internal static readonly Dictionary<string, L.PaletteKey> LibraryColours = new Dictionary<string, L.PaletteKey>
    {
        ["planned"] = L.LibraryPalette.Planned, ["next"] = L.LibraryPalette.Next, ["waits"] = L.LibraryPalette.Waits,
        ["fail"] = L.LibraryPalette.Fail, ["warn"] = L.LibraryPalette.Warn, ["remove"] = L.LibraryPalette.Remove,
        ["stale"] = L.LibraryPalette.Stale, ["draft"] = L.LibraryPalette.Draft, ["select"] = L.LibraryPalette.Select,
    };

    internal static readonly Dictionary<string, L.PaletteId> LibraryPaletteIds = new Dictionary<string, L.PaletteId>
    {
        ["default"] = L.LibraryPalettes.Default, ["colour-safe"] = L.LibraryPalettes.ColourSafe,
        ["high-contrast"] = L.LibraryPalettes.HighContrast,
    };

    internal static readonly Dictionary<string, L.IconKey> LibraryIconKeys = new Dictionary<string, L.IconKey>
    {
        ["fails"] = L.LibraryIcons.Fails, ["warns"] = L.LibraryIcons.Warns, ["remove"] = L.LibraryIcons.Remove,
        ["swap"] = L.LibraryIcons.Swap, ["finish"] = L.LibraryIcons.Finish, ["stale"] = L.LibraryIcons.Stale,
        ["waiting"] = L.LibraryIcons.Waiting, ["paused"] = L.LibraryIcons.Paused,
    };

    internal static readonly Dictionary<string, L.MaterialKey> LibraryMaterials = new Dictionary<string, L.MaterialKey>
    {
        ["ghost"] = L.MaterialKeys.Ghost, ["solid"] = L.MaterialKeys.Solid, ["wire"] = L.MaterialKeys.Wire,
        ["edge"] = L.MaterialKeys.Edge, ["see-through"] = L.MaterialKeys.SeeThrough, ["glow"] = L.MaterialKeys.Glow,
        ["label"] = L.MaterialKeys.Label, ["faded"] = L.MaterialKeys.Faded,
    };

    internal static readonly Dictionary<string, L.PieceFamily> LibraryFamilies = new Dictionary<string, L.PieceFamily>
    {
        ["cable"] = L.PieceFamilies.Cable, ["pipe"] = L.PieceFamilies.Pipe, ["chute"] = L.PieceFamilies.Chute,
        ["frame"] = L.PieceFamilies.Frame, ["wall"] = L.PieceFamilies.Wall, ["window"] = L.PieceFamilies.Window,
        ["device"] = L.PieceFamilies.Device, ["other"] = L.PieceFamilies.Other,
    };

    internal static readonly Dictionary<string, L.StyleSetId> LibrarySetIds = new Dictionary<string, L.StyleSetId>
    {
        ["selected"] = L.LibrarySets.Selected, ["hovered"] = L.LibrarySets.Hovered,
    };

    internal static readonly Dictionary<string, L.TextStyleKey> LibraryTextStyles = new Dictionary<string, L.TextStyleKey>
    {
        ["default"] = L.LibraryText.Default,
    };

    /// <summary>A key: one of Lattice's own (bare), or a mod's "owner/name".</summary>
    internal T? Key<T>(JToken? token, string path, Dictionary<string, T> library, Func<L.ModId, string, L.Result<T, L.Reason>> create)
        where T : struct
    {
        if (Text(token, path) is not { } text)
        {
            return null;
        }

        if (library.TryGetValue(text, out T known))
        {
            return known;
        }

        int slash = text.IndexOf('/');
        if (slash < 0)
        {
            Problem(path, $"is one of Lattice's own ({string.Join(", ", library.Keys)}) or a mod's owner/name");
            return null;
        }

        return Made(L.ModId.Create(text.Substring(0, slash)), path) is { } owner
            ? Made(create(owner, text.Substring(slash + 1)), path)
            : null;
    }

    internal L.PaletteKey? Colour(JToken? token, string path) => Key(token, path, LibraryColours, L.PaletteKey.Create);

    internal L.StyleSetId? SetId(JToken? token, string path) => Key(token, path, LibrarySetIds, L.StyleSetId.Create);

    internal L.PieceFamily? Family(JToken? token, string path) => Key(token, path, LibraryFamilies, L.PieceFamily.Create);

    // ---- measures ----------------------------------------------------------------------------------------------

    internal L.Fraction? Fraction(JToken? token, string path) =>
        Number(token, path) is { } value ? Made(L.Fraction.Create((float)value), path) : null;

    internal L.Metres? Metres(JToken? token, string path) =>
        Number(token, path) is { } value ? Made(L.Metres.Create((float)value), path) : null;

    internal L.Duration? Duration(JToken? token, string path) =>
        Number(token, path) is { } value ? Made(L.Duration.Create((float)value), path) : null;

    internal L.Period? Period(JToken? token, string path) =>
        Number(token, path) is { } value ? Made(L.Period.Create((float)value), path) : null;

    internal L.Pixels? Pixels(JToken? token, string path) =>
        Number(token, path) is { } value ? Made(L.Pixels.Create((float)value), path) : null;

    // ---- style -------------------------------------------------------------------------------------------------

    internal static readonly string[] StyleProperties =
    {
        "presence", "fill", "fill_paint", "fill_opacity", "edge", "pattern", "glow", "effect", "enter", "ease",
        "appear", "vanish", "see_through", "beyond_clip", "distance_fade", "near_limit", "dim", "label", "icon",
        "open_ends", "ports", "flow_arrows", "waits_links", "mark", "material", "pickable"
    };

    /// <summary>A Lattice Style: each property it gives, the rest no opinion.</summary>
    internal L.Style? Style(JToken? token, string path)
    {
        if (Object(token, path) is not { } given)
        {
            return null;
        }

        int before = Problems.Count;
        L.Style style = L.Style.Empty;
        foreach (KeyValuePair<string, JToken?> property in given)
        {
            string at = path + "." + property.Key;
            JToken? value = property.Value;
            switch (property.Key)
            {
                case "presence":
                    if (Case(value, at, "drawn", "hidden") is { } presence)
                    {
                        style = style with { Presence = L.Option.Some(presence.Kind == "drawn" ? L.Presence.Drawn : L.Presence.Hidden) };
                    }

                    break;
                case "fill":
                    if (Case(value, at, "ghost", "solid", "wire", "none") is { } fill)
                    {
                        style = style with
                        {
                            Fill = L.Option.Some(fill.Kind switch
                            {
                                "ghost" => L.FillMode.Ghost,
                                "solid" => L.FillMode.Solid,
                                "wire" => L.FillMode.Wire,
                                _ => L.FillMode.None
                            })
                        };
                    }

                    break;
                case "fill_paint":
                    if (Paint(value, at) is { } fillPaint)
                    {
                        style = style with { FillPaint = L.Option.Some(fillPaint) };
                    }

                    break;
                case "fill_opacity":
                    if (Fraction(value, at) is { } opacity)
                    {
                        style = style with { FillOpacity = L.Option.Some(opacity) };
                    }

                    break;
                case "edge":
                    if (Edge(value, at) is { } edge)
                    {
                        style = style with { Edge = L.Option.Some(edge) };
                    }

                    break;
                case "pattern":
                    if (Case(value, at, "none", "hatch", "cross_hatch", "dots") is { } pattern)
                    {
                        style = style with
                        {
                            Pattern = L.Option.Some(pattern.Kind switch
                            {
                                "hatch" => L.Pattern.Hatch,
                                "cross_hatch" => L.Pattern.CrossHatch,
                                "dots" => L.Pattern.Dots,
                                _ => L.Pattern.None
                            })
                        };
                    }

                    break;
                case "glow":
                    if (Glow(value, at) is { } glow)
                    {
                        style = style with { Glow = L.Option.Some(glow) };
                    }

                    break;
                case "effect":
                    if (Effect(value, at) is { } effect)
                    {
                        style = style with { Effect = L.Option.Some(effect) };
                    }

                    break;
                case "enter":
                    if (Enter(value, at) is { } enter)
                    {
                        style = style with { Enter = L.Option.Some(enter) };
                    }

                    break;
                case "ease":
                    if (Duration(value, at) is { } ease)
                    {
                        style = style with { Ease = L.Option.Some(ease) };
                    }

                    break;
                case "appear":
                    if (Duration(value, at) is { } appear)
                    {
                        style = style with { Appear = L.Option.Some(appear) };
                    }

                    break;
                case "vanish":
                    if (Duration(value, at) is { } vanish)
                    {
                        style = style with { Vanish = L.Option.Some(vanish) };
                    }

                    break;
                case "see_through":
                    if (SeeThrough(value, at) is { } seeThrough)
                    {
                        style = style with { SeeThrough = L.Option.Some(seeThrough) };
                    }

                    break;
                case "beyond_clip":
                    if (BeyondClip(value, at) is { } beyond)
                    {
                        style = style with { BeyondClip = L.Option.Some(beyond) };
                    }

                    break;
                case "distance_fade":
                    if (value is JArray { Count: 2 } fade && Metres(fade[0], at + "[0]") is { } start &&
                        Metres(fade[1], at + "[1]") is { } end && Made(L.FadeRange.Create(start, end), at) is { } range)
                    {
                        style = style with { DistanceFade = L.Option.Some(range) };
                    }
                    else if (!(value is JArray { Count: 2 }))
                    {
                        Problem(at, "is [start, end] in metres");
                    }

                    break;
                case "near_limit":
                    if (Metres(value, at) is { } near)
                    {
                        style = style with { NearLimit = L.Option.Some(near) };
                    }

                    break;
                case "dim":
                    if (Fraction(value, at) is { } dim)
                    {
                        style = style with { Dim = L.Option.Some(dim) };
                    }

                    break;
                case "label":
                    if (LabelLook(value, at) is { } label)
                    {
                        style = style with { Label = L.Option.Some(label) };
                    }

                    break;
                case "icon":
                    if (value is JValue { Type: JTokenType.String } && (string)value! == "none")
                    {
                        style = style with { Icon = L.Option.Some<L.IconLook>(new L.IconLook.None()) };
                    }
                    else if (Key(value, at, LibraryIconKeys, L.IconKey.Create) is { } icon)
                    {
                        style = style with { Icon = L.Option.Some<L.IconLook>(new L.IconLook.Shown(icon)) };
                    }

                    break;
                case "open_ends":
                    if (Show(value, at) is { } openEnds)
                    {
                        style = style with { OpenEnds = L.Option.Some(openEnds) };
                    }

                    break;
                case "ports":
                    if (Show(value, at) is { } ports)
                    {
                        style = style with { Ports = L.Option.Some(ports) };
                    }

                    break;
                case "flow_arrows":
                    if (Show(value, at) is { } flow)
                    {
                        style = style with { FlowArrows = L.Option.Some(flow) };
                    }

                    break;
                case "waits_links":
                    if (Show(value, at) is { } waits)
                    {
                        style = style with { WaitsLinks = L.Option.Some(waits) };
                    }

                    break;
                case "mark":
                    if (value is JValue { Type: JTokenType.String } && (string)value! == "none")
                    {
                        style = style with { Mark = L.Option.Some<L.MarkLook>(new L.MarkLook.None()) };
                    }
                    else if (Key(value, at, new Dictionary<string, L.DecorationKey>(), L.DecorationKey.Create) is { } mark)
                    {
                        style = style with { Mark = L.Option.Some<L.MarkLook>(new L.MarkLook.Shown(mark)) };
                    }

                    break;
                case "material":
                    if (Key(value, at, LibraryMaterials, L.MaterialKey.Create) is { } material)
                    {
                        style = style with { Material = L.Option.Some(material) };
                    }

                    break;
                case "pickable":
                    if (Flag(value, at) is { } pickable)
                    {
                        style = style with { Pickable = L.Option.Some(pickable ? L.Pickable.Yes : L.Pickable.No) };
                    }

                    break;
                default:
                    Problem(at, $"is not a style property: {string.Join(", ", StyleProperties)}");
                    break;
            }
        }

        return Problems.Count == before ? style : null;
    }

    /// <summary>A paint: [r, g, b(, a)] or "#rrggbb(aa)" (fixed), a palette key (token), "owner" (the owner's colour),
    /// {"ghost_tint": fallback key} or {"finish_colour": fallback key}.</summary>
    internal L.Paint? Paint(JToken? token, string path)
    {
        if (token is JArray || token is JValue { Type: JTokenType.String } hex && ((string)hex!).StartsWith("#", StringComparison.Ordinal))
        {
            return Rgba(token, path) is { } colour ? new L.Paint.Fixed(colour) : null;
        }

        if (token is JValue { Type: JTokenType.String } word)
        {
            return (string)word! == "owner"
                ? new L.Paint.OwnerColour()
                : Colour(word, path) is { } key ? new L.Paint.Token(key) : null;
        }

        if (Case(token, path, "token", "ghost_tint", "finish_colour", "fixed") is not { } paint)
        {
            return null;
        }

        string at = path + "." + paint.Kind;
        return paint.Kind switch
        {
            "fixed" => Rgba(paint.Payload, at) is { } fixedColour ? new L.Paint.Fixed(fixedColour) : null,
            "token" => Colour(paint.Payload, at) is { } token1 ? new L.Paint.Token(token1) : null,
            "ghost_tint" => Colour(paint.Payload, at) is { } tintFallback ? new L.Paint.GhostTint(tintFallback) : null,
            _ => Colour(paint.Payload, at) is { } finishFallback ? new L.Paint.FinishColour(finishFallback) : null
        };
    }

    private L.EdgeLook? Edge(JToken? token, string path)
    {
        if (Case(token, path, "none", "line") is not { } edge)
        {
            return null;
        }

        if (edge.Kind == "none")
        {
            return new L.EdgeLook.None();
        }

        string at = path + ".line";
        if (Object(edge.Payload, at) is not { } line)
        {
            return null;
        }

        L.LineStyle? kind = L.LineStyle.Solid;
        if (Given(line, "style") && Case(line["style"], at + ".style", "solid", "dashed", "dotted") is { } named)
        {
            kind = named.Kind switch { "dashed" => L.LineStyle.Dashed, "dotted" => L.LineStyle.Dotted, _ => L.LineStyle.Solid };
        }

        L.Pixels? width = Pixels(line["width"], at + ".width");
        L.Paint? paint = Paint(line["paint"], at + ".paint");
        return kind is { } k && width is { } w && paint != null ? new L.EdgeLook.Line(k, w, paint) : null;
    }

    private L.GlowLook? Glow(JToken? token, string path)
    {
        if (Case(token, path, "none", "lit") is not { } glow)
        {
            return null;
        }

        if (glow.Kind == "none")
        {
            return new L.GlowLook.None();
        }

        string at = path + ".lit";
        if (Object(glow.Payload, at) is not { } lit)
        {
            return null;
        }

        L.Paint? paint = Paint(lit["paint"], at + ".paint");
        L.Fraction? strength = Fraction(lit["strength"], at + ".strength");
        return paint != null && strength is { } s ? new L.GlowLook.Lit(paint, s) : null;
    }

    private L.EffectLook? Effect(JToken? token, string path)
    {
        if (Case(token, path, "steady", "pulse", "blink") is not { } effect)
        {
            return null;
        }

        if (effect.Kind == "steady")
        {
            return new L.EffectLook.Steady();
        }

        string at = path + "." + effect.Kind;
        if (Object(effect.Payload, at) is not { } timing || Period(timing["period"], at + ".period") is not { } period)
        {
            return null;
        }

        if (effect.Kind == "pulse")
        {
            return Fraction(timing["low"], at + ".low") is { } low ? new L.EffectLook.Pulse(period, low) : null;
        }

        return Fraction(timing["duty"], at + ".duty") is { } duty ? new L.EffectLook.Blink(period, duty) : null;
    }

    private L.EnterLook? Enter(JToken? token, string path)
    {
        if (Case(token, path, "none", "flash") is not { } enter)
        {
            return null;
        }

        if (enter.Kind == "none")
        {
            return new L.EnterLook.None();
        }

        string at = path + ".flash";
        if (Object(enter.Payload, at) is not { } flash)
        {
            return null;
        }

        L.Repeats? count = Whole(flash["count"], at + ".count") is { } times ? Made(L.Repeats.Create(times), at + ".count") : null;
        L.Period? period = Period(flash["period"], at + ".period");
        return count is { } c && period is { } p ? new L.EnterLook.Flash(c, p) : null;
    }

    internal L.SeeThroughLook? SeeThrough(JToken? token, string path)
    {
        if (Case(token, path, "none", "through") is not { } seeThrough)
        {
            return null;
        }

        if (seeThrough.Kind == "none")
        {
            return new L.SeeThroughLook.None();
        }

        string at = path + ".through";
        if (Object(seeThrough.Payload, at) is not { } through)
        {
            return null;
        }

        L.Fraction? strength = Fraction(through["strength"], at + ".strength");
        L.Metres? range = Metres(through["range"], at + ".range");
        L.Option<L.Fraction> opacity = L.Option<L.Fraction>.None;
        if (Given(through, "opacity"))
        {
            if (Minor < LatticeLooks.OpacityMinor)
            {
                Problem(at + ".opacity", $"needs Lattice 0.{LatticeLooks.OpacityMinor} or later (this one is 0.{Minor})");
            }
            else if (Fraction(through["opacity"], at + ".opacity") is { } own)
            {
                opacity = L.Option.Some(own);
            }
        }

        return strength is { } s && range is { } r ? new L.SeeThroughLook.Through(s, r) { Opacity = opacity } : null;
    }

    private L.BeyondClipLook? BeyondClip(JToken? token, string path)
    {
        if (Case(token, path, "drawn", "hidden", "wire", "dim") is not { } beyond)
        {
            return null;
        }

        return beyond.Kind switch
        {
            "drawn" => new L.BeyondClipLook.Drawn(),
            "hidden" => new L.BeyondClipLook.Hidden(),
            "wire" => new L.BeyondClipLook.Wire(),
            _ => Fraction(beyond.Payload, path + ".dim") is { } by ? new L.BeyondClipLook.Dim(by) : null
        };
    }

    private L.LabelLook? LabelLook(JToken? token, string path)
    {
        if (Case(token, path, "none", "shown") is not { } label)
        {
            return null;
        }

        if (label.Kind == "none")
        {
            return new L.LabelLook.None();
        }

        string at = path + ".shown";
        if (Object(label.Payload, at) is not { } shown)
        {
            return null;
        }

        L.LabelText? says = LabelText(shown["says"], at + ".says");
        L.TextStyleKey? textStyle = Given(shown, "style")
            ? Key(shown["style"], at + ".style", LibraryTextStyles, L.TextStyleKey.Create)
            : L.LibraryText.Default;
        L.LabelAnchor? anchor = L.LabelAnchor.Above;
        if (Given(shown, "anchor") && Case(shown["anchor"], at + ".anchor", "above", "centre", "below") is { } where)
        {
            anchor = where.Kind switch { "centre" => L.LabelAnchor.Centre, "below" => L.LabelAnchor.Below, _ => L.LabelAnchor.Above };
        }

        int? priority = Given(shown, "priority") ? Whole(shown["priority"], at + ".priority") : 0;
        L.Metres? range = Given(shown, "range") ? Metres(shown["range"], at + ".range") : Made(L.Metres.Create(30f), at);
        return says != null && textStyle is { } t && anchor is { } a && priority is { } p && range is { } r
            ? new L.LabelLook.Shown(says, t, a, p, r)
            : null;
    }

    private L.LabelText? LabelText(JToken? token, string path)
    {
        if (Case(token, path, "ghost_label", "finish_name", "finding_reason", "plan_title", "step_number", "bill_count",
                "literal") is not { } says)
        {
            return null;
        }

        return says.Kind switch
        {
            "ghost_label" => new L.LabelText.GhostLabel(),
            "finish_name" => new L.LabelText.FinishName(),
            "finding_reason" => new L.LabelText.FindingReason(),
            "plan_title" => new L.LabelText.PlanTitle(),
            "step_number" => new L.LabelText.StepNumber(),
            "bill_count" => new L.LabelText.BillCount(),
            _ => Text(says.Payload, path + ".literal") is { } words && Made(L.Label.Create(words), path + ".literal") is { } label
                ? new L.LabelText.Literal(label)
                : null
        };
    }

    private L.ShowMode? Show(JToken? token, string path)
    {
        if (token is JValue { Type: JTokenType.Boolean } flag)
        {
            return (bool)flag ? L.ShowMode.Shown : L.ShowMode.None;
        }

        return Case(token, path, "shown", "none") is { } show
            ? show.Kind == "shown" ? L.ShowMode.Shown : L.ShowMode.None
            : (L.ShowMode?)null;
    }

    // ---- matches, layers, highlights ---------------------------------------------------------------------------

    internal L.StyleMatch? Match(JToken? token, string path)
    {
        if (Case(token, path, "everything", "picks", "intent", "part", "state", "finding", "status", "family", "facing",
                "in_set", "mine", "tinted", "finishes", "draft", "pending", "structure", "all", "any", "not") is not { } match)
        {
            return null;
        }

        string at = path + "." + match.Kind;
        JToken? payload = match.Payload;
        switch (match.Kind)
        {
            case "everything":
                return new L.StyleMatch.Everything();
            case "picks":
                return Selector(payload, at) is { } which ? new L.StyleMatch.Picks(which) : null;
            case "intent":
                return Case(payload, at, "build", "remove", "swap", "finish_only") is { } intent
                    ? new L.StyleMatch.Intent(intent.Kind switch
                    {
                        "build" => L.IntentKind.Build,
                        "remove" => L.IntentKind.Remove,
                        "swap" => L.IntentKind.Swap,
                        _ => L.IntentKind.FinishOnly
                    })
                    : null;
            case "part":
                return Case(payload, at, "placement", "existing") is { } part
                    ? new L.StyleMatch.Part(part.Kind == "placement" ? L.PartKind.Placement : L.PartKind.Existing)
                    : null;
            case "state":
                return Case(payload, at, "planned", "started", "stale") is { } state
                    ? new L.StyleMatch.State(state.Kind switch
                    {
                        "planned" => L.StateKind.Planned,
                        "started" => L.StateKind.Started,
                        _ => L.StateKind.Stale
                    })
                    : null;
            case "finding":
            {
                JToken? kindToken = payload is JObject finding ? finding["kind"] : payload;
                if (Case(kindToken, at, "unchecked", "passes", "waits_for", "will_fail", "warns") is not { } kind)
                {
                    return null;
                }

                L.FindingKind findingKind = kind.Kind switch
                {
                    "unchecked" => L.FindingKind.Unchecked,
                    "passes" => L.FindingKind.Passes,
                    "waits_for" => L.FindingKind.WaitsFor,
                    "will_fail" => L.FindingKind.WillFail,
                    _ => L.FindingKind.Warns
                };
                L.Option<L.ReasonCode> code = L.Option<L.ReasonCode>.None;
                if (payload is JObject withCode && Given(withCode, "code") && Qualified(withCode["code"], at + ".code") is { } named &&
                    Made(L.ReasonCode.Create(named.Owner, named.Name), at + ".code") is { } reason)
                {
                    code = L.Option.Some(reason);
                }

                return new L.StyleMatch.Finding(findingKind, code);
            }
            case "status":
            {
                JToken? kindToken = payload is JObject status ? status["kind"] : payload;
                if (Case(kindToken, at, "working", "waiting", "paused", "none") is not { } kind)
                {
                    return null;
                }

                L.StatusKind statusKind = kind.Kind switch
                {
                    "working" => L.StatusKind.Working,
                    "waiting" => L.StatusKind.Waiting,
                    "paused" => L.StatusKind.Paused,
                    _ => L.StatusKind.None
                };
                L.Option<L.ExecutorId> by = L.Option<L.ExecutorId>.None;
                if (payload is JObject withBy && Given(withBy, "by") && Qualified(withBy["by"], at + ".by") is { } executor &&
                    Made(L.ExecutorId.Create(executor.Owner, executor.Name), at + ".by") is { } id)
                {
                    by = L.Option.Some(id);
                }

                return new L.StyleMatch.Status(statusKind, by);
            }
            case "family":
                return Family(payload, at) is { } family ? new L.StyleMatch.Family(family) : null;
            case "facing":
                return Axis(payload, at) is { } normal ? new L.StyleMatch.Facing(normal) : null;
            case "in_set":
                return SetId(payload, at) is { } set ? new L.StyleMatch.InSet(set) : null;
            case "mine":
                return new L.StyleMatch.Mine();
            case "tinted":
                return new L.StyleMatch.Tinted();
            case "finishes":
                return new L.StyleMatch.Finishes();
            case "draft":
                return new L.StyleMatch.Draft();
            case "pending":
                return new L.StyleMatch.Pending();
            case "structure":
                return new L.StyleMatch.Structure();
            case "all":
                return new L.StyleMatch.All(Each(payload, at, Match));
            case "any":
                return new L.StyleMatch.Any(Each(payload, at, Match));
            default:
                return Match(payload, at) is { } inner ? new L.StyleMatch.Not(inner) : null;
        }
    }

    /// <summary>A style layer: {name (StationGod's own, "agent-" prefixed), band, order, rules [{match, style}], palette, motion, enabled}.</summary>
    internal L.StyleLayerDescriptor? Layer(JToken? token, string path)
    {
        if (Object(token, path) is not { } layer)
        {
            return null;
        }

        L.StyleLayerId? id = Text(layer["name"], path + ".name") is { } name
            ? Made(L.StyleLayerId.Create(LatticeNames.Mod, "agent-" + name), path + ".name")
            : null;
        L.StyleBand? band = L.StyleBand.Meaning;
        if (Given(layer, "band") && Case(layer["band"], path + ".band", "meaning", "theme", "view", "selection", "highlight") is { } named)
        {
            band = named.Kind switch
            {
                "theme" => L.StyleBand.Theme,
                "view" => L.StyleBand.View,
                "selection" => L.StyleBand.Selection,
                "highlight" => L.StyleBand.Highlight,
                _ => L.StyleBand.Meaning
            };
        }

        int? order = Given(layer, "order") ? Whole(layer["order"], path + ".order") : 0;
        L.ValueList<L.StyleRule> rules = Each(layer["rules"], path + ".rules", Rule);
        L.Option<L.Palette> palette = L.Option<L.Palette>.None;
        if (Given(layer, "palette") && Palette(layer["palette"], path + ".palette", id) is { } given)
        {
            palette = L.Option.Some(given);
        }

        L.Option<L.Motion> motion = L.Option<L.Motion>.None;
        if (Given(layer, "motion") && Case(layer["motion"], path + ".motion", "full", "reduced", "off") is { } moves)
        {
            motion = L.Option.Some(moves.Kind switch { "reduced" => L.Motion.Reduced, "off" => L.Motion.Off, _ => L.Motion.Full });
        }

        bool? enabled = Given(layer, "enabled") ? Flag(layer["enabled"], path + ".enabled") : true;
        return id is { } i && band is { } b && order is { } o && enabled is { } e
            ? new L.StyleLayerDescriptor(i, b, o, rules) { Palette = palette, Motion = motion, Enabled = e }
            : null;
    }

    private L.StyleRule? Rule(JToken? token, string path)
    {
        if (Object(token, path) is not { } rule)
        {
            return null;
        }

        L.StyleMatch? match = Match(rule["match"], path + ".match");
        L.Style? style = Style(rule["style"], path + ".style");
        return match != null && style != null ? new L.StyleRule(match, style) : null;
    }

    // A layer's palette: {colours: {"owner/name" or Lattice's own: colour}, owner_ramp: [colour], base: palette id}.
    private L.Palette? Palette(JToken? token, string path, L.StyleLayerId? layer)
    {
        if (Object(token, path) is not { } palette || layer is not { } owner)
        {
            return null;
        }

        L.ValueMap<L.PaletteKey, L.Rgba> colours = L.ValueMap<L.PaletteKey, L.Rgba>.Empty;
        if (Given(palette, "colours") && Object(palette["colours"], path + ".colours") is { } given)
        {
            foreach (KeyValuePair<string, JToken?> colour in given)
            {
                if (Colour(new JValue(colour.Key), $"{path}.colours.{colour.Key}") is { } key &&
                    Rgba(colour.Value, $"{path}.colours.{colour.Key}") is { } rgba)
                {
                    colours = colours.With(key, rgba);
                }
            }
        }

        L.ValueList<L.Rgba> ramp = Given(palette, "owner_ramp")
            ? EachValue(palette["owner_ramp"], path + ".owner_ramp", Rgba)
            : L.ValueList<L.Rgba>.Empty;
        L.Option<L.PaletteId> under = L.Option<L.PaletteId>.None;
        if (Given(palette, "base") && Key(palette["base"], path + ".base", LibraryPaletteIds, L.PaletteId.Create) is { } baseId)
        {
            under = L.Option.Some(baseId);
        }

        return Made(L.PaletteId.Create(LatticeNames.Mod, owner.Value.Replace('/', '-')), path) is { } id
            ? new L.Palette(id, colours) { OwnerRamp = ramp, Base = under }
            : null;
    }

    internal L.HighlightTargets? Highlighted(JToken? token, string path)
    {
        if (Case(token, path, "ghosts", "picked", "structures") is not { } targets)
        {
            return null;
        }

        string at = path + "." + targets.Kind;
        return targets.Kind switch
        {
            "ghosts" => new L.HighlightTargets.Ghosts(EachValue(targets.Payload, at, Ghost)),
            "picked" => Selector(targets.Payload, at) is { } which ? new L.HighlightTargets.Picked(which) : null,
            _ => new L.HighlightTargets.Structures(EachValue(targets.Payload, at, Structure))
        };
    }

    /// <summary>A clip: {plane: {point, normal}} or {box: [corner, opposite]}, metres.</summary>
    internal L.ClipShape? Clip(JToken? token, string path)
    {
        if (Case(token, path, "plane", "box") is not { } clip)
        {
            return null;
        }

        string at = path + "." + clip.Kind;
        if (clip.Kind == "plane")
        {
            if (Object(clip.Payload, at) is not { } plane)
            {
                return null;
            }

            L.WorldPoint? point = Point(plane["point"], at + ".point");
            L.WorldVector? normal = Vector(plane["normal"], at + ".normal");
            return point is { } p && normal is { } n ? MadeRef(L.ClipShape.Plane.Create(p, n), at) : null;
        }

        if (clip.Payload is JArray { Count: 2 } corners && Point(corners[0], at + "[0]") is { } a &&
            Point(corners[1], at + "[1]") is { } b)
        {
            return MadeRef(L.ClipShape.Box.Create(a, b), at);
        }

        Problem(at, "is two corners [[x, y, z], [x, y, z]] in metres");
        return null;
    }
}
