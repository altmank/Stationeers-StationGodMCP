#nullable enable

using System;
using StationGodMCP.LatticeLink.Model;
using StationGodMCP.Pure;
using StationGodMCP.Pure.RemoteView;
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

    private static L.GhostId Place(TestWorld world, L.Actor actor, L.Maker maker, Vec3 at)
    {
        L.PrefabKey frame = TestCatalogue.Hand.Key("StructureFrame").Reduce(default(L.PrefabKey));
        GhostPlacement placement = new GhostPlacement(frame.Hash, 0, at, new Vec3(0, 0, 0), Orange, null);
        L.GhostDraft draft = LatticeGhosts.ToDraft(placement, maker, L.Option<L.PlanId>.None, 0, new(), out string? refusal)
                             ?? throw new InvalidOperationException(refusal);
        L.Submission sent = world.System.Commands.Submit(L.CommandId.New(),
            new L.Command.Place(L.ValueList.Of(draft)), actor);
        L.Submission.Answered answered = Assert.IsType<L.Submission.Answered>(sent);
        L.Outcome.Applied applied = Assert.IsType<L.Outcome.Applied>(answered.Outcome);
        return Assert.Single(applied.Changes.Added).Id;
    }

    private static L.StyleSource Source(L.StyleStory story, L.StyleProperty property) =>
        story.Sources.TryGetValue(property, out L.StyleSource source) ? source : new L.StyleSource.Default();

    private static L.StyleStory Story(L.IGhostSystem system, L.GhostId ghost) =>
        system.Styles.Explain(ghost).Match(static story => story,
            static why => throw new InvalidOperationException(LatticeText.Of(why)));
}
