#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.LatticeLink.Model;
using StationGodMCP.Pure;
using StationGodMCP.Pure.RemoteView;
using StationGodMCP.Pure.Verbs;
using Lattice.Testing;
using Xunit;
using L = Lattice.Api;

namespace StationGodMCP.Tests.LatticeLink;

/// <summary>Lattice's test host runs one world per process at a time: its tests never run beside each other.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LatticeWorldCollection
{
    internal const string Name = "Lattice world";
}

/// <summary>
/// StationGod's look through Lattice's own style cascade (Lattice.Testing, nothing drawn): the agent's ghosts glow, a
/// player's do not, both show through walls, and fill, pattern and edge stay the library's.
/// </summary>
[Collection(LatticeWorldCollection.Name)]
public sealed class LatticeLooksTests
{
    private static readonly Rgba Orange = new Rgba(1f, 0.6f, 0.2f, 0.4f);

    [Fact]
    public void AgentGhostsGlowAllShowThroughAndTheLibraryKeepsFillPatternAndEdge()
    {
        using TestWorld world = Start();
        L.IGhostSystem system = world.System;
        Assert.True(system.Styles.Add(LatticeLooks.Layer).TryGet(out L.IStyleLayer _));

        L.GhostId agent = Place(world, LatticeNames.Agent, LatticeNames.AgentMaker, new Vec3(1, 1, 1));
        L.GhostId player = Place(world, new L.Actor.Player(new L.ClientId(1)), LatticeNames.PlayerMaker, new Vec3(3, 1, 1));
        Assert.True(world.Settle().TryGet(out int _));

        L.StyleStory agentLook = Story(system, agent);
        L.StyleStory playerLook = Story(system, player);
        L.StyleSource ours = new L.StyleSource.Layer(L.StyleBand.Meaning, LatticeLooks.Id, 0);
        Assert.Equal(ours, Source(agentLook, L.StyleProperty.Glow));
        Assert.NotEqual(ours, Source(playerLook, L.StyleProperty.Glow));
        L.StyleSource seeThrough = new L.StyleSource.Layer(L.StyleBand.Meaning, LatticeLooks.Id, 1);
        Assert.Equal(seeThrough, Source(agentLook, L.StyleProperty.SeeThrough));
        Assert.Equal(seeThrough, Source(playerLook, L.StyleProperty.SeeThrough));
        Assert.IsType<L.SeeThroughLook.Through>(playerLook.Resolved.SeeThrough.Reduce(new L.SeeThroughLook.None()));
        foreach (L.StyleProperty library in new[] { L.StyleProperty.FillPaint, L.StyleProperty.Pattern, L.StyleProperty.Edge })
        {
            Assert.IsNotType<L.StyleSource.Layer>(Source(agentLook, library));
        }

        Assert.Equal(L.Option.Some<L.Paint>(new L.Paint.GhostTint(L.LibraryPalette.Planned)), agentLook.Resolved.FillPaint);
    }

    // A look kept on the ghost (stationgod/style) is drawn by its own layer: a preset's fill, see-through off, and a
    // placement whose plan had problems pulsing red over the agent's violet.
    [Fact]
    public void ALooksOwnLayerDrawsItOverTheBase()
    {
        using TestWorld world = Start();
        L.IGhostSystem system = world.System;
        Assert.True(system.Styles.Add(LatticeLooks.Layer).TryGet(out L.IStyleLayer _));

        GhostLook solid = Look(new GhostLookChange("solid", false, null, null, null, null)).WithProblem(true);
        L.GhostId marked = Place(world, LatticeNames.Agent, LatticeNames.AgentMaker, new Vec3(1, 1, 1), solid);
        L.GhostId plain = Place(world, LatticeNames.Agent, LatticeNames.AgentMaker, new Vec3(3, 1, 1));
        Assert.True(world.Settle().TryGet(out int _));

        L.StyleStory look = Story(system, marked);
        Assert.Equal(L.Option.Some(L.FillMode.Solid), look.Resolved.Fill);
        Assert.IsType<L.SeeThroughLook.None>(look.Resolved.SeeThrough.Reduce(new L.SeeThroughLook.Through(default, default)));
        Assert.IsType<L.StyleSource.Layer>(Source(look, L.StyleProperty.Glow));
        Assert.NotEqual(LatticeLooks.Id, ((L.StyleSource.Layer)Source(look, L.StyleProperty.Glow)).Id);
        Assert.IsType<L.EffectLook.Pulse>(look.Resolved.Effect.Reduce(new L.EffectLook.Steady()));

        L.StyleStory standard = Story(system, plain);
        Assert.Equal(L.Option.Some(L.ShowMode.Shown), standard.Resolved.Ports);
        Assert.Equal(L.Option.Some(L.ShowMode.Shown), standard.Resolved.FlowArrows);
        Assert.Equal(L.Option.Some(L.ShowMode.Shown), standard.Resolved.WaitsLinks);
        Assert.Equal(new L.StyleSource.Layer(L.StyleBand.Meaning, LatticeLooks.Id, 0), Source(standard, L.StyleProperty.Glow));
        L.SeeThroughLook.Through through = Assert.IsType<L.SeeThroughLook.Through>(standard.Resolved.SeeThrough.Reduce(new L.SeeThroughLook.None()));
        Assert.Equal(1f, through.Strength.Value);
        Assert.Equal(120f, through.Range.Value);
    }

    // LIVE 2026-10-25: look quiet dropped x-ray, and markers could not go off with x-ray kept. Quiet now only hides the
    // markers; x-ray strength and range are the caller's.
    [Fact]
    public void QuietKeepsXRayAndMarkersGoOffAlone()
    {
        using TestWorld world = Start();
        L.IGhostSystem system = world.System;
        Assert.True(system.Styles.Add(LatticeLooks.Layer).TryGet(out L.IStyleLayer _));

        L.GhostId quiet = Place(world, LatticeNames.Agent, LatticeNames.AgentMaker, new Vec3(1, 1, 1),
            Look(new GhostLookChange("quiet", true, 0.8, 300, null, null)));
        L.GhostId bare = Place(world, LatticeNames.Agent, LatticeNames.AgentMaker, new Vec3(3, 1, 1),
            Look(new GhostLookChange(null, null, null, null, new JObject { ["ports"] = false, ["flow"] = false }, null)));
        Assert.True(world.Settle().TryGet(out int _));

        L.StyleStory quietLook = Story(system, quiet);
        L.SeeThroughLook.Through through = Assert.IsType<L.SeeThroughLook.Through>(quietLook.Resolved.SeeThrough.Reduce(new L.SeeThroughLook.None()));
        Assert.Equal(0.8f, through.Strength.Value, 3);
        Assert.Equal(300f, through.Range.Value);
        Assert.Equal(L.Option.Some(L.ShowMode.None), quietLook.Resolved.Ports);
        Assert.Equal(L.Option.Some(L.ShowMode.None), quietLook.Resolved.OpenEnds);

        L.StyleStory bareLook = Story(system, bare);
        Assert.Equal(L.Option.Some(L.ShowMode.None), bareLook.Resolved.Ports);
        Assert.Equal(L.Option.Some(L.ShowMode.None), bareLook.Resolved.FlowArrows);
        Assert.Equal(L.Option.Some(L.ShowMode.Shown), bareLook.Resolved.OpenEnds);
        Assert.IsType<L.SeeThroughLook.Through>(bareLook.Resolved.SeeThrough.Reduce(new L.SeeThroughLook.None()));
    }

    // A look's own Lattice style goes on top; a look made of the same settings is one layer.
    [Fact]
    public void ALooksStyleGoesOnTopAndEqualLooksShareALayer()
    {
        using TestWorld world = Start();
        L.IGhostSystem system = world.System;
        Assert.True(system.Styles.Add(LatticeLooks.Layer).TryGet(out L.IStyleLayer _));
        GhostLook edged = Look(new GhostLookChange(null, null, null, null, null,
            JObject.Parse("""{"edge": {"line": {"style": "dashed", "width": 2, "paint": [0, 1, 0]}}, "fill": "wire"}""")));

        L.GhostId first = Place(world, LatticeNames.Agent, LatticeNames.AgentMaker, new Vec3(1, 1, 1), edged);
        L.GhostId second = Place(world, LatticeNames.Agent, LatticeNames.AgentMaker, new Vec3(3, 1, 1), edged);
        Assert.True(world.Settle().TryGet(out int _));

        L.StyleStory look = Story(system, first);
        Assert.Equal(L.Option.Some(L.FillMode.Wire), look.Resolved.Fill);
        L.EdgeLook.Line line = Assert.IsType<L.EdgeLook.Line>(look.Resolved.Edge.Reduce(new L.EdgeLook.None()));
        Assert.Equal(L.LineStyle.Dashed, line.Style);
        Assert.Equal(Source(look, L.StyleProperty.Edge), Source(Story(system, second), L.StyleProperty.Edge));
    }

    [Fact]
    public void XRayStrengthWithXRayOffIsRefusedNotIgnored()
    {
        List<OptionProblem> problems = new();

        Assert.Null(GhostLook.Plain.Change(new GhostLookChange(null, false, 0.5, null, null, null), problems));

        Assert.Equal("xray_strength", Assert.Single(problems).Path);
    }

    [Fact]
    public void TheLayerIsAddedOncePerWorld()
    {
        using TestWorld world = Start();
        Assert.True(world.System.Styles.Add(LatticeLooks.Layer).TryGet(out L.IStyleLayer _));
        Assert.True(world.System.Styles.Add(LatticeLooks.Layer).TryGetError(out L.Reason _));
    }

    private static TestWorld Start() =>
        TestWorld.Start(TestScenario.Empty).Match(static world => world,
            static why => throw new InvalidOperationException(LatticeText.Of(why)));

    private static L.GhostId Place(TestWorld world, L.Actor actor, L.Maker maker, Vec3 at, GhostLook? look = null)
    {
        L.PrefabKey frame = TestCatalogue.Hand.Key("StructureFrame").Reduce(default(L.PrefabKey));
        GhostPlacement placement = new GhostPlacement(frame.Hash, 0, at, new Vec3(0, 0, 0), Orange, null);
        L.GhostDraft draft = LatticeGhosts.ToDraft(placement, maker, L.Option<L.PlanId>.None, 0, new(), out string? refusal,
                                 look)
                             ?? throw new InvalidOperationException(refusal);
        List<string> notes = new();
        if (look?.Encoded() is { } encoded)
        {
            LatticeLooks.Ensure(world.System, encoded, notes);
        }

        Assert.Empty(notes);
        L.Submission sent = world.System.Commands.Submit(L.CommandId.New(),
            new L.Command.Place(L.ValueList.Of(draft)), actor);
        L.Submission.Answered answered = Assert.IsType<L.Submission.Answered>(sent);
        L.Outcome.Applied applied = Assert.IsType<L.Outcome.Applied>(answered.Outcome);
        return Assert.Single(applied.Changes.Added).Id;
    }

    private static GhostLook Look(GhostLookChange change) =>
        GhostLook.Plain.Change(change, new List<OptionProblem>()) ?? throw new InvalidOperationException("look refused");

    private static L.StyleSource Source(L.StyleStory story, L.StyleProperty property) =>
        story.Sources.TryGetValue(property, out L.StyleSource source) ? source : new L.StyleSource.Default();

    private static L.StyleStory Story(L.IGhostSystem system, L.GhostId ghost) =>
        system.Styles.Explain(ghost).Match(static story => story,
            static why => throw new InvalidOperationException(LatticeText.Of(why)));
}
