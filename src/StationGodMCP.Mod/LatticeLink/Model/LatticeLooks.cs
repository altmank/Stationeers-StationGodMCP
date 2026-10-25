#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Newtonsoft.Json.Linq;
using StationGodMCP.LatticeLink.Json;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;
using L = Lattice.Api;

namespace StationGodMCP.LatticeLink.Model;

/// <summary>
/// StationGod's look in Lattice's drawing (Meaning band, so a viewer's theme still wins). The base layer: the agent's
/// ghosts glow in StationGod's agent colour (a token a theme can recolour), and every StationGod ghost shows through
/// walls, frames and terrain at full strength out to 120 m with its ports, open ends, flow arrows and waits links shown;
/// its later rules keep the looks of ghosts placed before 2.12 (attachments look, xray, plan). A ghost with a look of
/// its own (GhostLook, kept as the attachment stationgod/style) is drawn by that look's own layer, added once per
/// distinct look on every seat that sees one (Ensure): one rule, the ghosts tagged with that look, the whole look as
/// one Style. Fill (the ghost's tint), patterns and edges stay the library's unless a look's style names them.
/// </summary>
internal static class LatticeLooks
{
    internal static readonly L.StyleLayerId Id = LatticeNames.Must(L.StyleLayerId.Create(LatticeNames.Mod, "look"));

    internal static readonly L.PaletteKey AgentColour = LatticeNames.Must(L.PaletteKey.Create(LatticeNames.Mod, "agent"));

    internal static readonly L.Rgba Violet = new L.Rgba(170, 110, 255, 255);

    // The layers of looks added on this seat, by look, for the world they were added in.
    private static readonly Dictionary<string, IDisposable> Added = new Dictionary<string, IDisposable>(StringComparer.Ordinal);

    private static L.IGhostSystem? _addedFor;

    /// <summary>show highlight's "look here" on a ghost: Lattice's selection glow, pulsing, shown through walls.</summary>
    internal static L.Style LookHere => new L.Style
    {
        Glow = L.Option.Some<L.GlowLook>(new L.GlowLook.Lit(new L.Paint.Token(L.LibraryPalette.Select),
            LatticeNames.Must(L.Fraction.Create(0.9f)))),
        Effect = Pulse(0.6f, 0.4f),
        SeeThrough = L.Option.Some<L.SeeThroughLook>(new L.SeeThroughLook.Through(
            LatticeNames.Must(L.Fraction.Create(0.8f)), LatticeNames.Must(L.Metres.Create(200f)))),
    };

    /// <summary>The look's mark on a ghost's attachments: its encoded look, or none for Plain.</summary>
    internal static L.ValueMap<L.AttachmentKey, L.Attachment> Marks(GhostLook look,
        L.ValueMap<L.AttachmentKey, L.Attachment> attached)
    {
        attached = attached.Without(LatticeNames.Look).Without(LatticeNames.XRay).Without(LatticeNames.PlanState);
        return look.Encoded() is { } encoded
            ? attached.With(LatticeNames.Style, LatticeNames.Must(L.Attachment.Create(encoded)))
            : attached.Without(LatticeNames.Style);
    }

    /// <summary>A ghost's look as it keeps it: its own (stationgod/style), else from the attachments before 2.12.</summary>
    internal static GhostLook LookOf(L.Ghost ghost)
    {
        if (ghost.Attached.TryGetValue(LatticeNames.Style, out L.Attachment style))
        {
            return GhostLook.Decode(style.Value);
        }

        string? preset = ghost.Attached.TryGetValue(LatticeNames.Look, out L.Attachment look) ? look.Value : null;
        bool xrayOff = ghost.Attached.TryGetValue(LatticeNames.XRay, out L.Attachment xray) && xray.Value == "off";
        bool problem = ghost.Attached.TryGetValue(LatticeNames.PlanState, out L.Attachment plan) && plan.Value == "problem";
        return GhostLook.Legacy(preset, xrayOff, problem);
    }

    /// <summary>
    /// Makes sure the looks these ghosts carry have their layers on this seat (adding one re-signatures what Lattice
    /// draws, so each is added once per world). A look Lattice refuses is said in notes and draws as the base layer.
    /// </summary>
    internal static void Ensure(L.IGhostSystem system, IEnumerable<L.Ghost> ghosts, List<string>? notes)
    {
        foreach (L.Ghost ghost in ghosts)
        {
            if (ghost.Attached.TryGetValue(LatticeNames.Style, out L.Attachment style))
            {
                Ensure(system, style.Value, notes);
            }
        }
    }

    internal static void Ensure(L.IGhostSystem system, string encoded, List<string>? notes)
    {
        if (!ReferenceEquals(_addedFor, system))
        {
            // Registrations end with their world.
            Added.Clear();
            _addedFor = system;
        }

        if (Added.ContainsKey(encoded))
        {
            return;
        }

        LatticeIn read = new LatticeIn(system);
        L.StyleLayerDescriptor? layer = LayerOf(encoded, read);
        if (layer == null)
        {
            notes?.Add($"Lattice cannot draw the look {encoded}: {string.Join("; ", read.Problems)}.");
            Added[encoded] = NoLayer.Instance;
            return;
        }

        L.Result<L.IStyleLayer, L.Reason> added = system.Styles.Add(layer);
        if (added.TryGet(out L.IStyleLayer handle))
        {
            Added[encoded] = handle;
        }
        else if (added.TryGetError(out L.Reason why))
        {
            notes?.Add($"Lattice refused the look's layer: {LatticeText.Of(why)}.");
            Added[encoded] = NoLayer.Instance;
        }
    }

    /// <summary>The layer that draws one look: its ghosts (tagged with it) as the look's Style; null with problems in read.</summary>
    internal static L.StyleLayerDescriptor? LayerOf(string encoded, LatticeIn read)
    {
        L.Style? style = StyleOf(GhostLook.Decode(encoded), read);
        if (style == null)
        {
            return null;
        }

        L.StyleLayerId id = LatticeNames.Must(L.StyleLayerId.Create(LatticeNames.Mod, "look-" + Hash(encoded)));
        L.StyleMatch tagged = new L.StyleMatch.Picks(new L.Selector.Tagged(LatticeNames.Style,
            L.Option.Some(LatticeNames.Must(L.Attachment.Create(encoded)))));
        return new L.StyleLayerDescriptor(id, L.StyleBand.Meaning, 1, L.ValueList.Of(new L.StyleRule(tagged, style)));
    }

    /// <summary>A look as one Lattice Style: x-ray, the four markers, the preset, problems, then its own style on top.</summary>
    internal static L.Style? StyleOf(GhostLook look, LatticeIn read)
    {
        if (look.Opacity != null && read.Minor < OpacityMinor)
        {
            read.Problem("xray_opacity", $"needs Lattice 0.{OpacityMinor} or later (this one is 0.{read.Minor})");
            return null;
        }

        L.Style style = new L.Style
        {
            SeeThrough = L.Option.Some<L.SeeThroughLook>(look.XRay
                ? new L.SeeThroughLook.Through(LatticeNames.Must(L.Fraction.Create((float)look.Strength)),
                    LatticeNames.Must(L.Metres.Create((float)look.Range)))
                {
                    Opacity = look.Opacity is { } opacity
                        ? L.Option.Some(LatticeNames.Must(L.Fraction.Create((float)opacity)))
                        : L.Option<L.Fraction>.None
                }
                : new L.SeeThroughLook.None()),
            Ports = Shown(look.Shows("ports")),
            OpenEnds = Shown(look.Shows("open_ends")),
            FlowArrows = Shown(look.Shows("flow")),
            WaitsLinks = Shown(look.Shows("waits")),
        };
        style = look.Preset switch
        {
            "solid" => style with { Fill = L.Option.Some(L.FillMode.Solid) },
            "wire" => style with { Fill = L.Option.Some(L.FillMode.Wire) },
            "pulse" => style with { Effect = Pulse(1.2f, 0.35f) },
            _ => style
        };
        if (look.Problem)
        {
            style = style with { Glow = ProblemGlow, Effect = Pulse(0.8f, 0.3f) };
        }

        if (look.Style == null)
        {
            return style;
        }

        return read.Style(look.Style, "style") is { } own ? Over(style, own) : null;
    }

    /// <summary>The base style with every property the top one gives replaced.</summary>
    internal static L.Style Over(L.Style under, L.Style top) => new L.Style
    {
        Presence = top.Presence.IsSome ? top.Presence : under.Presence,
        Fill = top.Fill.IsSome ? top.Fill : under.Fill,
        FillPaint = top.FillPaint.IsSome ? top.FillPaint : under.FillPaint,
        FillOpacity = top.FillOpacity.IsSome ? top.FillOpacity : under.FillOpacity,
        Edge = top.Edge.IsSome ? top.Edge : under.Edge,
        Pattern = top.Pattern.IsSome ? top.Pattern : under.Pattern,
        Glow = top.Glow.IsSome ? top.Glow : under.Glow,
        Effect = top.Effect.IsSome ? top.Effect : under.Effect,
        Enter = top.Enter.IsSome ? top.Enter : under.Enter,
        Ease = top.Ease.IsSome ? top.Ease : under.Ease,
        Appear = top.Appear.IsSome ? top.Appear : under.Appear,
        Vanish = top.Vanish.IsSome ? top.Vanish : under.Vanish,
        SeeThrough = top.SeeThrough.IsSome ? top.SeeThrough : under.SeeThrough,
        BeyondClip = top.BeyondClip.IsSome ? top.BeyondClip : under.BeyondClip,
        DistanceFade = top.DistanceFade.IsSome ? top.DistanceFade : under.DistanceFade,
        NearLimit = top.NearLimit.IsSome ? top.NearLimit : under.NearLimit,
        Dim = top.Dim.IsSome ? top.Dim : under.Dim,
        Label = top.Label.IsSome ? top.Label : under.Label,
        Icon = top.Icon.IsSome ? top.Icon : under.Icon,
        OpenEnds = top.OpenEnds.IsSome ? top.OpenEnds : under.OpenEnds,
        Ports = top.Ports.IsSome ? top.Ports : under.Ports,
        FlowArrows = top.FlowArrows.IsSome ? top.FlowArrows : under.FlowArrows,
        WaitsLinks = top.WaitsLinks.IsSome ? top.WaitsLinks : under.WaitsLinks,
        Mark = top.Mark.IsSome ? top.Mark : under.Mark,
        Material = top.Material.IsSome ? top.Material : under.Material,
        Pickable = top.Pickable.IsSome ? top.Pickable : under.Pickable,
    };

    internal static L.StyleLayerDescriptor Layer
    {
        get
        {
            L.StyleMatch agents = new L.StyleMatch.Picks(new L.Selector.Owner(LatticeNames.Agent));
            L.Style glow = new L.Style
            {
                Glow = L.Option.Some<L.GlowLook>(new L.GlowLook.Lit(new L.Paint.Token(AgentColour),
                    LatticeNames.Must(L.Fraction.Create(0.35f)))),
            };
            L.StyleMatch ours = new L.StyleMatch.Picks(new L.Selector.Any(L.ValueList.From(new L.Selector[]
            {
                new L.Selector.MadeBy(LatticeNames.AgentMaker), new L.Selector.MadeBy(LatticeNames.PlayerMaker),
                new L.Selector.MadeBy(LatticeNames.MovedMaker),
            })));
            L.Style plain = new L.Style
            {
                SeeThrough = L.Option.Some<L.SeeThroughLook>(new L.SeeThroughLook.Through(
                    LatticeNames.Must(L.Fraction.Create((float)GhostLook.DefaultStrength)),
                    LatticeNames.Must(L.Metres.Create((float)GhostLook.DefaultRange)))),
                Ports = Shown(true),
                OpenEnds = Shown(true),
                FlowArrows = Shown(true),
                WaitsLinks = Shown(true),
            };
            return new L.StyleLayerDescriptor(Id, L.StyleBand.Meaning, 0, L.ValueList.From(new[]
            {
                new L.StyleRule(agents, glow),
                new L.StyleRule(ours, plain),
                // Ghosts placed before 2.12 keep their looks.
                new L.StyleRule(Tagged(LatticeNames.Look, "solid"), new L.Style { Fill = L.Option.Some(L.FillMode.Solid) }),
                new L.StyleRule(Tagged(LatticeNames.Look, "wire"), new L.Style { Fill = L.Option.Some(L.FillMode.Wire) }),
                new L.StyleRule(Tagged(LatticeNames.Look, "pulse"), new L.Style { Effect = Pulse(1.2f, 0.35f) }),
                new L.StyleRule(Tagged(LatticeNames.Look, "quiet"), new L.Style
                {
                    Ports = Shown(false),
                    OpenEnds = Shown(false),
                    FlowArrows = Shown(false),
                    WaitsLinks = Shown(false),
                }),
                new L.StyleRule(Tagged(LatticeNames.XRay, "off"), new L.Style
                {
                    SeeThrough = L.Option.Some<L.SeeThroughLook>(new L.SeeThroughLook.None()),
                }),
                new L.StyleRule(Tagged(LatticeNames.PlanState, "problem"), new L.Style
                {
                    Glow = ProblemGlow,
                    Effect = Pulse(0.8f, 0.3f),
                }),
            }))
            {
                Palette = L.Option.Some(new L.Palette(
                    LatticeNames.Must(L.PaletteId.Create(LatticeNames.Mod, "look")),
                    L.ValueMap<L.PaletteKey, L.Rgba>.Empty.With(AgentColour, Violet))),
            };
        }
    }

    private static L.Option<L.GlowLook> ProblemGlow => L.Option.Some<L.GlowLook>(new L.GlowLook.Lit(
        new L.Paint.Token(L.LibraryPalette.Fail), LatticeNames.Must(L.Fraction.Create(0.6f))));

    private static L.Option<L.ShowMode> Shown(bool shown) => L.Option.Some(shown ? L.ShowMode.Shown : L.ShowMode.None);

    private static L.StyleMatch Tagged(L.AttachmentKey key, string value) =>
        new L.StyleMatch.Picks(new L.Selector.Tagged(key, L.Option.Some(LatticeNames.Must(L.Attachment.Create(value)))));

    private static L.Option<L.EffectLook> Pulse(float seconds, float low) =>
        L.Option.Some<L.EffectLook>(new L.EffectLook.Pulse(LatticeNames.Must(L.Period.Create(seconds)),
            LatticeNames.Must(L.Fraction.Create(low))));

    // The contract minor that sets the hidden part's own opacity (SeeThroughLook.Through.Opacity).
    internal const int OpacityMinor = 11;

    // FNV-1a over the look's text: the same look names the same layer on every seat.
    internal static string Hash(string text)
    {
        ulong hash = 14695981039346656037UL;
        foreach (byte octet in Encoding.UTF8.GetBytes(text))
        {
            hash ^= octet;
            hash *= 1099511628211UL;
        }

        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    // A look Lattice cannot draw is remembered too, so it is not tried again every change.
    private sealed class NoLayer : IDisposable
    {
        internal static readonly NoLayer Instance = new NoLayer();

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// A draft's look in a Lattice command: {look, xray, xray_strength, xray_range, markers, style} as show ghost takes them,
/// kept on the draft as its stationgod/style.
/// </summary>
internal sealed class LatticeLookIn
{
    internal LatticeLookIn(LatticeIn read)
    {
        Read = read;
    }

    internal LatticeIn Read { get; }

    /// <summary>Every look a command's drafts carry, so their layers can be added before it is sent.</summary>
    internal List<string> Looks { get; } = new List<string>();

    internal L.ValueMap<L.AttachmentKey, L.Attachment>? Marks(JToken? token, string path,
        L.ValueMap<L.AttachmentKey, L.Attachment> attached)
    {
        if (Read.Object(token, path) is not { } given)
        {
            return null;
        }

        List<OptionProblem> problems = new List<OptionProblem>();
        GhostLookChange change = new GhostLookChange(
            LatticeIn.Given(given, "look") ? Read.Text(given["look"], path + ".look") : null,
            LatticeIn.Given(given, "xray") ? Read.Flag(given["xray"], path + ".xray") : null,
            LatticeIn.Given(given, "xray_strength") ? Read.Number(given["xray_strength"], path + ".xray_strength") : null,
            LatticeIn.Given(given, "xray_range") ? Read.Number(given["xray_range"], path + ".xray_range") : null,
            LatticeIn.Given(given, "markers") ? given["markers"] : null,
            LatticeIn.Given(given, "style") ? given["style"] : null,
            LatticeIn.Given(given, "xray_opacity") ? Read.Number(given["xray_opacity"], path + ".xray_opacity") : null);
        foreach (KeyValuePair<string, JToken?> key in given)
        {
            if (Array.IndexOf(new[] { "look", "xray", "xray_strength", "xray_range", "xray_opacity", "markers", "style" }, key.Key) < 0)
            {
                Read.Problem($"{path}.{key.Key}", "is not a look option: look, xray, xray_strength, xray_range, xray_opacity, markers, style");
            }
        }

        GhostLook? look = GhostLook.Plain.Change(change, problems);
        foreach (OptionProblem problem in problems)
        {
            Read.Problem($"{path}.{problem.Path}", problem.Problem);
        }

        if (look == null)
        {
            return null;
        }

        if (look.Encoded() is { } encoded)
        {
            if (LatticeLooks.LayerOf(encoded, Read) == null)
            {
                return null;
            }

            Looks.Add(encoded);
        }

        return LatticeLooks.Marks(look, attached);
    }
}
