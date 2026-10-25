#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using StationGodMCP.Pure;
using StationGodMCP.Pure.RemoteView;
using L = Lattice.Api;

namespace StationGodMCP.LatticeLink.Model;

/// <summary>StationGod's names in Lattice: its mod id, the agent, the makers of its ghosts, its attachment key.</summary>
internal static class LatticeNames
{
    internal static readonly L.ModId Mod = Must(L.ModId.Create("stationgod"));

    /// <summary>The agent an MCP client drives: the actor of show ghost and of builds from ghosts.</summary>
    internal static readonly L.Actor Agent = new L.Actor.Agent(Must(L.AgentId.Create("stationgod")));

    /// <summary>The actor of ghosts StationGod moves in itself (an old stationgod-ghosts.json read into Lattice).</summary>
    internal static readonly L.Actor Self = new L.Actor.Mod(Mod);

    internal static readonly L.Maker AgentMaker = Must(L.Maker.Create(Mod, "agent"));

    internal static readonly L.Maker PlayerMaker = Must(L.Maker.Create(Mod, "ghost-mode"));

    internal static readonly L.Maker MovedMaker = Must(L.Maker.Create(Mod, "board-file"));

    /// <summary>A ghost's build state when it is not the last (show ghost's state): Lattice's build means built.</summary>
    internal static readonly L.AttachmentKey BuildState = Must(L.AttachmentKey.Create(Mod, "build-state"));

    /// <summary>An agent ghost's look preset when it is not plan (GhostLook), kept on the ghost for every viewer.</summary>
    internal static readonly L.AttachmentKey Look = Must(L.AttachmentKey.Create(Mod, "look"));

    /// <summary>"off" on a ghost drawn without see-through (show ghost xray false).</summary>
    internal static readonly L.AttachmentKey XRay = Must(L.AttachmentKey.Create(Mod, "xray"));

    /// <summary>"problem" on a ghost whose plan had problems when it was placed.</summary>
    internal static readonly L.AttachmentKey PlanState = Must(L.AttachmentKey.Create(Mod, "plan"));

    internal static readonly L.Attachment Off = Must(L.Attachment.Create("off"));

    internal static readonly L.Attachment ProblemMark = Must(L.Attachment.Create("problem"));

    internal static readonly L.PlanName AgentPlan = Must(L.PlanName.Create("StationGod agent"));

    internal static readonly L.ModuleName Render = Must(L.ModuleName.Create("render"));

    internal static T Must<T>(L.Result<T, L.Reason> result) where T : notnull =>
        result.Match(static value => value, static why => throw new InvalidOperationException(LatticeText.Of(why)));
}

/// <summary>Lattice's reasons as one line of text: the code, then its facts.</summary>
internal static class LatticeText
{
    internal static string Of(L.Reason reason)
    {
        List<string> facts = new List<string>();
        foreach (KeyValuePair<string, string> fact in reason.Facts)
        {
            facts.Add($"{fact.Key} {fact.Value}");
        }

        facts.Sort(StringComparer.Ordinal);
        string text = facts.Count > 0 ? $"{reason.Code.Value} ({string.Join(", ", facts)})" : reason.Code.Value;
        return reason.GameMessage.TryGet(out string message) && message.Length > 0 ? $"{text}: {message}" : text;
    }

    internal static string Of(L.ValueList<L.Refusal> refusals)
    {
        List<string> lines = new List<string>(refusals.Count);
        foreach (L.Refusal refusal in refusals)
        {
            lines.Add(refusal.Ghosts.Count > 0
                ? $"{Of(refusal.Why)} (ghosts {Ids(refusal.Ghosts)})"
                : Of(refusal.Why));
        }

        return string.Join("; ", lines);
    }

    private static string Ids(L.ValueList<L.GhostId> ids)
    {
        List<string> text = new List<string>(ids.Count);
        foreach (L.GhostId id in ids)
        {
            text.Add(id.Value.ToString(CultureInfo.InvariantCulture));
        }

        return string.Join(", ", text);
    }
}

/// <summary>
/// The board's ghosts and Lattice's, both ways. A Lattice cell is the game's Grid3 key of a structure's position
/// (metres x 10, rounded away from zero), its turn the contract's (Turn.Axes: where it sends +X, +Y, +Z; Turn.Of back).
/// Only build ghosts are on StationGod's board;
/// remove, swap and finish ghosts are Lattice's alone.
/// </summary>
internal static class LatticeGhosts
{
    /// <summary>A Lattice build ghost as the board holds it; null for any other intent.</summary>
    /// <param name="ghost">The ghost.</param>
    /// <param name="prefabName">The prefab's name by hash ("" when the game has none).</param>
    /// <param name="lastState">The prefab's last build state by hash.</param>
    /// <param name="ownerName">Who placed it, as find ghost's owner reads.</param>
    /// <param name="untinted">The tint of a ghost Lattice holds none for.</param>
    internal static BoardGhost? ToBoard(L.Ghost ghost, Func<int, string> prefabName, Func<int, int> lastState,
        Func<L.Actor, string> ownerName, Rgba untinted)
    {
        if (!(ghost.Intent is L.Intent.Build build))
        {
            return null;
        }

        L.Placement target = build.Target;
        int hash = target.Piece.Hash;
        CubeRotation rotation = RotationOf(target.Turn);
        (int xTurns, int yTurns, int zTurns) = rotation.EulerTurns();
        int last = lastState(hash);
        int state = last;
        if (ghost.Attached.TryGetValue(LatticeNames.BuildState, out L.Attachment attached) &&
            int.TryParse(attached.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int given))
        {
            state = Math.Max(0, Math.Min(given, last));
        }

        Rgba tint = ghost.Tint.TryGet(out L.Tint t)
            ? new Rgba(t.R / 255f, t.G / 255f, t.B / 255f, t.A / 255f)
            : untinted;
        GhostSource source = ghost.PlacedBy is L.Actor.Agent ? GhostSource.Agent : GhostSource.Player;
        string? label = ghost.Label.TryGet(out L.Label text) ? text.Value : null;
        return new BoardGhost(ghost.Id.Value, hash, prefabName(hash), state, PositionOf(target.At),
            new Vec3(xTurns * 90, yTurns * 90, zTurns * 90), tint, source, ownerName(ghost.PlacedBy), label,
            ghost.PlacedAt.ToUnixTimeMilliseconds() / 1000.0);
    }

    /// <summary>
    /// A board placement as a Lattice build ghost to place; null with the refusal when its turn is not a quarter turn.
    /// A label Lattice refuses (too long, blank) is left off, and said in notes.
    /// </summary>
    internal static L.GhostDraft? ToDraft(GhostPlacement placement, L.Maker madeBy, L.Option<L.PlanId> plan,
        int lastState, List<string> notes, out string? refusal, GhostLook? look = null)
    {
        refusal = null;
        if (TurnOf(placement.Euler) is not { } turn)
        {
            refusal = $"its turn ({placement.Euler.X}, {placement.Euler.Y}, {placement.Euler.Z}) is not quarter turns";
            return null;
        }

        L.Option<L.Label> label = L.Option<L.Label>.None;
        if (placement.Label != null)
        {
            L.Result<L.Label, L.Reason> made = L.Label.Create(placement.Label);
            if (made.TryGet(out L.Label value))
            {
                label = L.Option.Some(value);
            }
            else if (made.TryGetError(out L.Reason why))
            {
                notes.Add($"The label \"{placement.Label}\" was left off: Lattice refused it ({LatticeText.Of(why)}).");
            }
        }

        L.ValueMap<L.AttachmentKey, L.Attachment> attached = L.ValueMap<L.AttachmentKey, L.Attachment>.Empty;
        if (placement.BuildState < lastState)
        {
            attached = attached.With(LatticeNames.BuildState, LatticeNames.Must(
                L.Attachment.Create(placement.BuildState.ToString(CultureInfo.InvariantCulture))));
        }

        attached = LatticeLooks.Marks(look ?? GhostLook.Plain, attached);

        L.Placement target = new L.Placement(new L.PrefabKey(placement.PrefabHash), CellOf(placement.Position), turn);
        return new L.GhostDraft(new L.Intent.Build(target, L.Option<L.Finish>.None), plan, madeBy,
            new L.Visibility.Everyone(), label, L.Option.Some(TintOf(placement.Tint)), L.ValueSet<L.GroupName>.Empty,
            attached);
    }

    /// <summary>
    /// The way a piece joining a port needs its end: back into the port, the opposite of Lattice's Port.Toward (out of
    /// the piece), as StationGod's ports say it.
    /// </summary>
    internal static string IntoPort(L.Face outOfPiece) => outOfPiece switch
    {
        L.Face.PlusX => "-x",
        L.Face.MinusX => "+x",
        L.Face.PlusY => "-y",
        L.Face.MinusY => "+y",
        L.Face.PlusZ => "-z",
        _ => "+z"
    };

    /// <summary>A port role as the game names it (ConnectionRole); null for a plain run end.</summary>
    internal static string? RoleOf(L.Option<L.PortRole> role) =>
        role.TryGet(out L.PortRole given) ? given.ToString() : null;

    /// <summary>The cell of a position in metres: the game's Grid3 key, as Lattice reads a structure's.</summary>
    internal static L.Cell CellOf(Vec3 position) => new L.Cell(Key(position.X), Key(position.Y), Key(position.Z));

    internal static Vec3 PositionOf(L.Cell cell) => new Vec3(cell.X * 0.1, cell.Y * 0.1, cell.Z * 0.1);

    /// <summary>The Lattice turn of Euler degrees (Quaternion.Euler's order); null when one is not a quarter turn.</summary>
    internal static L.Turn? TurnOf(Vec3 euler)
    {
        if (!CubeRotation.TryQuarterTurns(euler.X, out int x) || !CubeRotation.TryQuarterTurns(euler.Y, out int y) ||
            !CubeRotation.TryQuarterTurns(euler.Z, out int z))
        {
            return null;
        }

        return TurnOf(CubeRotation.FromEuler(x, y, z));
    }

    /// <summary>The Lattice turn of a quarter-turn rotation: where it sends local +X and +Y.</summary>
    internal static L.Turn? TurnOf(CubeRotation rotation) =>
        L.Turn.Of(AxisOf(rotation.Right), AxisOf(rotation.Up)).TryGet(out L.Turn turn) ? turn : null;

    /// <summary>The rotation a Lattice turn makes.</summary>
    internal static CubeRotation RotationOf(L.Turn turn)
    {
        L.TurnAxes axes = turn.Axes;
        return CubeRotation.FromAxes(UnitOf(axes.X), UnitOf(axes.Y));
    }

    private static L.SignedAxis AxisOf(GridStep step)
    {
        L.AxisSign sign = step.Dx + step.Dy + step.Dz > 0 ? L.AxisSign.Plus : L.AxisSign.Minus;
        L.Axis axis = step.Dx != 0 ? L.Axis.X : step.Dy != 0 ? L.Axis.Y : L.Axis.Z;
        return new L.SignedAxis(axis, sign);
    }

    private static (int X, int Y, int Z) UnitOf(L.SignedAxis axis)
    {
        int sign = axis.Sign == L.AxisSign.Minus ? -1 : 1;
        return axis.Axis switch
        {
            L.Axis.X => (sign, 0, 0),
            L.Axis.Y => (0, sign, 0),
            _ => (0, 0, sign)
        };
    }

    internal static L.Tint TintOf(Rgba tint) => new L.Tint(Byte(tint.Red), Byte(tint.Green), Byte(tint.Blue),
        Byte(tint.Alpha));

    private static byte Byte(float channel) => (byte)Math.Round(Math.Max(0f, Math.Min(1f, channel)) * 255f);

    private static int Key(double metres) => (int)Math.Round(metres * 10.0, MidpointRounding.AwayFromZero);
}

/// <summary>
/// StationGod's look in Lattice's drawing (Meaning band, so a viewer's theme still wins): the agent's ghosts glow in
/// StationGod's agent colour, a token a theme can recolour, and every StationGod ghost (the agent's and ghost mode's)
/// shows through walls, frames and terrain as StationGod's own board drawing does. Fill (the ghost's tint), patterns
/// (fails, removes, warns) and edges (findings, the owner's colour) stay the library's: patterns mean something there.
/// </summary>
internal static class LatticeLooks
{
    internal static readonly L.StyleLayerId Id = LatticeNames.Must(L.StyleLayerId.Create(LatticeNames.Mod, "look"));

    internal static readonly L.PaletteKey AgentColour = LatticeNames.Must(L.PaletteKey.Create(LatticeNames.Mod, "agent"));

    internal static readonly L.Rgba Violet = new L.Rgba(170, 110, 255, 255);

    /// <summary>show highlight's "look here" on a ghost: Lattice's selection glow, pulsing, shown through walls.</summary>
    internal static L.Style LookHere => new L.Style
    {
        Glow = L.Option.Some<L.GlowLook>(new L.GlowLook.Lit(new L.Paint.Token(L.LibraryPalette.Select),
            LatticeNames.Must(L.Fraction.Create(0.9f)))),
        Effect = Pulse(0.6f, 0.4f),
        SeeThrough = L.Option.Some<L.SeeThroughLook>(new L.SeeThroughLook.Through(
            LatticeNames.Must(L.Fraction.Create(0.8f)), LatticeNames.Must(L.Metres.Create(200f)))),
    };

    /// <summary>The look's marks on a ghost's attachments: the preset (not plan), xray off, a plan with problems.</summary>
    internal static L.ValueMap<L.AttachmentKey, L.Attachment> Marks(GhostLook look,
        L.ValueMap<L.AttachmentKey, L.Attachment> attached)
    {
        if (look.Preset != GhostLook.Plan)
        {
            attached = attached.With(LatticeNames.Look, LatticeNames.Must(L.Attachment.Create(look.Preset)));
        }

        if (!look.XRay)
        {
            attached = attached.With(LatticeNames.XRay, LatticeNames.Off);
        }

        return look.Problem ? attached.With(LatticeNames.PlanState, LatticeNames.ProblemMark) : attached;
    }

    private static L.StyleMatch Tagged(L.AttachmentKey key, string value) =>
        new L.StyleMatch.Picks(new L.Selector.Tagged(key, L.Option.Some(LatticeNames.Must(L.Attachment.Create(value)))));

    private static L.Option<L.EffectLook> Pulse(float seconds, float low) =>
        L.Option.Some<L.EffectLook>(new L.EffectLook.Pulse(LatticeNames.Must(L.Period.Create(seconds)),
            LatticeNames.Must(L.Fraction.Create(low))));

    // As StationGod's own see-through pass: half the ghost's strength, through anything within a long room's reach.
    private const float SeeThroughStrength = 0.5f;

    private const float SeeThroughRangeM = 60f;

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
            L.Style seeThrough = new L.Style
            {
                SeeThrough = L.Option.Some<L.SeeThroughLook>(new L.SeeThroughLook.Through(
                    LatticeNames.Must(L.Fraction.Create(SeeThroughStrength)),
                    LatticeNames.Must(L.Metres.Create(SeeThroughRangeM)))),
            };
            L.Style plain = seeThrough with
            {
                Ports = L.Option.Some(L.ShowMode.Shown),
                OpenEnds = L.Option.Some(L.ShowMode.Shown),
            };
            return new L.StyleLayerDescriptor(Id, L.StyleBand.Meaning, 0, L.ValueList.From(new[]
            {
                new L.StyleRule(agents, glow),
                new L.StyleRule(ours, plain),
                new L.StyleRule(Tagged(LatticeNames.Look, "solid"), new L.Style { Fill = L.Option.Some(L.FillMode.Solid) }),
                new L.StyleRule(Tagged(LatticeNames.Look, "wire"), new L.Style { Fill = L.Option.Some(L.FillMode.Wire) }),
                new L.StyleRule(Tagged(LatticeNames.Look, "pulse"), new L.Style { Effect = Pulse(1.2f, 0.35f) }),
                new L.StyleRule(Tagged(LatticeNames.Look, "quiet"), new L.Style
                {
                    SeeThrough = L.Option.Some<L.SeeThroughLook>(new L.SeeThroughLook.None()),
                    Ports = L.Option.Some(L.ShowMode.None),
                    OpenEnds = L.Option.Some(L.ShowMode.None),
                }),
                new L.StyleRule(Tagged(LatticeNames.XRay, "off"), new L.Style
                {
                    SeeThrough = L.Option.Some<L.SeeThroughLook>(new L.SeeThroughLook.None()),
                }),
                new L.StyleRule(Tagged(LatticeNames.PlanState, "problem"), new L.Style
                {
                    Glow = L.Option.Some<L.GlowLook>(new L.GlowLook.Lit(new L.Paint.Token(L.LibraryPalette.Fail),
                        LatticeNames.Must(L.Fraction.Create(0.6f)))),
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
}

/// <summary>What Lattice's validation says about one ghost, as find ghost's check reports it.</summary>
internal sealed class GhostCheck
{
    private GhostCheck(long ghost, string verdict, string? why, IReadOnlyList<long>? waitsFor)
    {
        Ghost = ghost;
        Verdict = verdict;
        Why = why;
        WaitsFor = waitsFor;
    }

    internal long Ghost { get; }

    /// <summary>passes, waits, fails, warns, or unknown (a finding of a newer Lattice).</summary>
    internal string Verdict { get; }

    internal string? Why { get; }

    internal IReadOnlyList<long>? WaitsFor { get; }

    internal static GhostCheck Of(L.Finding finding)
    {
        long ghost = finding.Ghost.Value;
        switch (finding)
        {
            case L.Finding.Passes:
                return new GhostCheck(ghost, "passes", null, null);
            case L.Finding.WaitsFor waits:
                List<long> others = new List<long>(waits.Others.Count);
                foreach (L.GhostId other in waits.Others)
                {
                    others.Add(other.Value);
                }

                return new GhostCheck(ghost, "waits", null, others);
            case L.Finding.WillFail fails:
                return new GhostCheck(ghost, "fails", LatticeText.Of(fails.Why), null);
            case L.Finding.Warns warns:
                return new GhostCheck(ghost, "warns", LatticeText.Of(warns.Why), null);
            default:
                return new GhostCheck(ghost, "unknown", finding.GetType().Name, null);
        }
    }

    /// <summary>
    /// One check per ghost, worst first among a ghost's findings (fails, warns, waits, passes): a ghost can have
    /// several findings.
    /// </summary>
    internal static Dictionary<long, GhostCheck> PerGhost(IEnumerable<L.Finding> findings)
    {
        Dictionary<long, GhostCheck> checks = new Dictionary<long, GhostCheck>();
        foreach (L.Finding finding in findings)
        {
            GhostCheck check = Of(finding);
            if (!checks.TryGetValue(check.Ghost, out GhostCheck? standing) || Rank(check) > Rank(standing))
            {
                checks[check.Ghost] = check;
            }
        }

        return checks;
    }

    private static int Rank(GhostCheck check) => check.Verdict switch
    {
        "fails" => 4,
        "unknown" => 3,
        "warns" => 2,
        "waits" => 1,
        _ => 0
    };
}
