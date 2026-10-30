#nullable enable

using System;
using StationGodMCP.LatticeLink.Model;
using StationGodMCP.Pure;
using StationGodMCP.Pure.RemoteView;
using Lattice.Testing;
using Xunit;
using L = Lattice.Api;

namespace StationGodMCP.Tests.LatticeLink;

/// <summary>
/// The "StationGod agent" plan closes once it holds no ghost (LU saw it open and empty in Loom's plan menu every
/// session): an empty one is finished and leaves Lattice's active plans; one with a ghost left stays.
/// </summary>
[Collection(LatticeWorldCollection.Name)]
public sealed class AgentPlanCloseTests
{
    private static readonly Rgba Tint = new Rgba(0.2f, 0.9f, 1f, 0.4f);

    [Fact]
    public void AnEmptyAgentPlanIsFinished()
    {
        using TestWorld world = Start();
        L.PlanId plan = OpenAgentPlan(world, out L.GhostId ghost);
        Applied(world, new L.Command.Remove(new L.Selector.Ids(L.ValueList.Of(ghost))));

        Assert.Equal(new[] { plan }, LatticeAgentPlans.CloseEmpty(world.System, new()));
        Assert.True(world.System.Plans.Find(plan).IsNone);
    }

    [Fact]
    public void AnAgentPlanWithAGhostLeftStays()
    {
        using TestWorld world = Start();
        L.PlanId plan = OpenAgentPlan(world, out L.GhostId _);

        Assert.Empty(LatticeAgentPlans.CloseEmpty(world.System, new()));
        Assert.True(world.System.Plans.Find(plan).IsSome);
    }

    private static L.PlanId OpenAgentPlan(TestWorld world, out L.GhostId ghost)
    {
        L.PrefabKey frame = TestCatalogue.Hand.Key("StructureFrame").Reduce(default(L.PrefabKey));
        GhostPlacement placement = new GhostPlacement(frame.Hash, 0, new Vec3(1, 1, 1), new Vec3(0, 0, 0), Tint, null);
        L.GhostDraft draft = LatticeGhosts.ToDraft(placement, LatticeNames.AgentMaker, L.Option<L.PlanId>.None, 0, new(),
                                 out string? refusal) ?? throw new InvalidOperationException(refusal);
        L.ChangeSet changes = Applied(world, new L.Command.CreatePlan(new L.PlanDraft(LatticeNames.AgentPlan,
            LatticeNames.AgentMaker, new L.Visibility.Everyone(), L.ValueMap<L.AttachmentKey, L.Attachment>.Empty))
        {
            Ghosts = L.ValueList.Of(draft)
        });
        ghost = Assert.Single(changes.Added).Id;
        return Assert.Single(changes.PlansOpened).Id;
    }

    private static L.ChangeSet Applied(TestWorld world, L.Command command)
    {
        L.Submission sent = world.System.Commands.Submit(L.CommandId.New(), command, LatticeNames.Agent);
        L.Submission.Answered answered = Assert.IsType<L.Submission.Answered>(sent);
        return Assert.IsType<L.Outcome.Applied>(answered.Outcome).Changes;
    }

    private static TestWorld Start() =>
        TestWorld.Start(TestScenario.Empty).Match(static world => world,
            static why => throw new InvalidOperationException(LatticeText.Of(why)));
}
