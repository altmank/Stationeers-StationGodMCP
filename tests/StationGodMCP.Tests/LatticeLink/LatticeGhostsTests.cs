#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.LatticeLink.Model;
using StationGodMCP.Pure;
using StationGodMCP.Pure.RemoteView;
using Xunit;
using L = Lattice.Api;

namespace StationGodMCP.Tests.LatticeLink;

/// <summary>
/// StationGod's ghosts on Lattice without the game: Lattice's turns (Turn.Axes, Turn.Of) as StationGod's rotations,
/// board placements into Lattice drafts and Lattice ghosts back, and Lattice's findings as find ghost's check.
/// </summary>
public sealed class LatticeGhostsTests
{
    private static readonly Rgba Orange = new Rgba(1f, 0.6f, 0.2f, 0.4f);

    [Fact]
    public void EveryLatticeTurnIsTheRotationItsAxesSay()
    {
        HashSet<CubeRotation> seen = new HashSet<CubeRotation>();
        for (int index = 0; index < L.Turn.Count; index++)
        {
            L.Turn turn = L.Turn.Create(index).ToOption().Reduce(default(L.Turn));
            CubeRotation rotation = LatticeGhosts.RotationOf(turn);

            Assert.Equal(turn, LatticeGhosts.TurnOf(rotation));
            Assert.True(seen.Add(rotation));
        }

        // Lattice's +Z is +X cross +Y, as the game's (CubeRotation's) forward is right cross up.
        L.TurnAxes axes = LatticeGhosts.TurnOf(CubeRotation.FromEuler(0, 1, 0))!.Value.Axes;
        Assert.Equal(L.SignedAxis.ZMinus, axes.X);
        Assert.Equal(L.SignedAxis.YPlus, axes.Y);
        Assert.Equal(L.SignedAxis.XPlus, axes.Z);
    }

    [Fact]
    public void APlacementGoesToLatticeAndComesBackAsItWas()
    {
        GhostPlacement placement = new GhostPlacement(-1234, 1, new Vec3(716.5, 203.25, -668.75), new Vec3(0, 90, 0),
            Orange, "second pump");
        List<string> notes = new List<string>();

        L.GhostDraft? draft = LatticeGhosts.ToDraft(placement, LatticeNames.AgentMaker, L.Option<L.PlanId>.None, 3,
            notes, out string? refusal);

        Assert.NotNull(draft);
        Assert.Null(refusal);
        Assert.Empty(notes);
        L.Intent.Build build = Assert.IsType<L.Intent.Build>(draft!.Intent);
        Assert.Equal(new L.Cell(7165, 2033, -6688), build.Target.At);
        Assert.Equal(CubeRotation.FromEuler(0, 1, 0), LatticeGhosts.RotationOf(build.Target.Turn));
        Assert.Equal(new L.Tint(255, 153, 51, 102), draft.Tint.Reduce(default(L.Tint)));
        Assert.Equal("1", draft.Attached[LatticeNames.BuildState].Value);

        L.Ghost ghost = Ghost(42, draft.Intent, LatticeNames.Agent, draft.Label, draft.Tint, draft.Attached);
        BoardGhost back = LatticeGhosts.ToBoard(ghost, _ => "StructureVolumePump", _ => 3, _ => "agent", Orange)!;

        Assert.Equal(42, back.Id);
        Assert.Equal(-1234, back.PrefabHash);
        Assert.Equal("StructureVolumePump", back.PrefabName);
        Assert.Equal(1, back.BuildState);
        Assert.Equal(716.5, back.Position.X, 6);
        Assert.Equal(203.3, back.Position.Y, 6);
        Assert.Equal(-668.8, back.Position.Z, 6);
        Assert.Equal(new Vec3(0, 90, 0), back.Euler);
        Assert.Equal(GhostSource.Agent, back.Source);
        Assert.Equal("second pump", back.Label);
        Assert.Equal(1f, back.Tint.Red, 2);
        Assert.Equal(0.4f, back.Tint.Alpha, 2);
    }

    [Fact]
    public void AFinishedGhostCarriesNoBuildStateAndAPlayersGhostIsAPlayers()
    {
        GhostPlacement placement = new GhostPlacement(77, 5, new Vec3(1, 2, 3), new Vec3(90, 0, 0), Orange, null);
        L.GhostDraft draft = LatticeGhosts.ToDraft(placement, LatticeNames.PlayerMaker, L.Option<L.PlanId>.None, 5,
            new List<string>(), out _)!;

        Assert.Empty(draft.Attached);
        Assert.True(draft.Label.IsNone);
        L.Ghost ghost = Ghost(7, draft.Intent, new L.Actor.Player(new L.ClientId(9)), draft.Label,
            L.Option<L.Tint>.None, draft.Attached);
        BoardGhost back = LatticeGhosts.ToBoard(ghost, _ => "", _ => 5, _ => "Tester", Orange)!;

        Assert.Equal(5, back.BuildState);
        Assert.Equal(GhostSource.Player, back.Source);
        Assert.Equal("Tester", back.Owner);
        Assert.Equal(new Vec3(90, 0, 0), back.Euler);
        Assert.Equal(Orange.Green, back.Tint.Green);
    }

    [Fact]
    public void ATurnOffTheGridIsRefusedAndOtherIntentsStayOffTheBoard()
    {
        GhostPlacement tilted = new GhostPlacement(77, 0, new Vec3(1, 2, 3), new Vec3(0, 45, 0), Orange, null);

        Assert.Null(LatticeGhosts.ToDraft(tilted, LatticeNames.AgentMaker, L.Option<L.PlanId>.None, 0,
            new List<string>(), out string? refusal));
        Assert.Contains("not quarter turns", refusal);

        L.Intent remove = new L.Intent.Remove(new L.StructureRef(new L.ReferenceId(5), new L.PrefabKey(77),
            new L.Cell(10, 20, 30)));
        Assert.Null(LatticeGhosts.ToBoard(Ghost(1, remove, LatticeNames.Agent, L.Option<L.Label>.None,
            L.Option<L.Tint>.None, L.ValueMap<L.AttachmentKey, L.Attachment>.Empty), _ => "", _ => 0, _ => "", Orange));
    }

    [Fact]
    public void ALabelLatticeRefusesIsLeftOffWithANote()
    {
        GhostPlacement placement = new GhostPlacement(77, 0, new Vec3(1, 2, 3), Vec3.Zero, Orange, "   ");
        List<string> notes = new List<string>();

        L.GhostDraft? draft = LatticeGhosts.ToDraft(placement, LatticeNames.AgentMaker, L.Option<L.PlanId>.None, 0,
            notes, out _);

        Assert.NotNull(draft);
        Assert.True(draft!.Label.IsNone);
        Assert.Contains("was left off", Assert.Single(notes));
    }

    [Fact]
    public void AGhostsCheckIsItsWorstFinding()
    {
        L.Reason why = L.Reason.Of(L.ReasonCodes.NeedsSupport, default,
            L.ValueMap<string, string>.Empty.With("cell", "1,2,3"));
        List<L.Finding> findings = new List<L.Finding>
        {
            new L.Finding.Passes(new L.GhostId(1)),
            new L.Finding.WaitsFor(new L.GhostId(2), L.ValueList.Of(new L.GhostId(1))),
            new L.Finding.Passes(new L.GhostId(3)),
            new L.Finding.WillFail(new L.GhostId(3), why),
            new L.Finding.Warns(new L.GhostId(3), why)
        };

        Dictionary<long, GhostCheck> checks = GhostCheck.PerGhost(findings);

        Assert.Equal("passes", checks[1].Verdict);
        Assert.Equal("waits", checks[2].Verdict);
        Assert.Equal(new long[] { 1 }, checks[2].WaitsFor);
        Assert.Equal("fails", checks[3].Verdict);
        Assert.Equal("needs-support (cell 1,2,3)", checks[3].Why);
    }

    private static L.Ghost Ghost(long id, L.Intent intent, L.Actor by, L.Option<L.Label> label, L.Option<L.Tint> tint,
        L.ValueMap<L.AttachmentKey, L.Attachment> attached) =>
        new L.Ghost(new L.GhostId(id), new L.PlanId(1), new L.Revision(1), intent, new L.GhostState.Planned(), by,
            LatticeNames.AgentMaker, DateTimeOffset.FromUnixTimeSeconds(1_800_000_000), new L.Visibility.Everyone(),
            label, tint, L.ValueSet<L.GroupName>.Empty, attached);
}

/// <summary>
/// Lattice is optional at run time: on Mono a method that names a type of a missing assembly fails when it is
/// compiled. So Lattice's types appear only in LatticeLink, and its classes whose members name them (LatticeBoard,
/// LatticeVerbs, LatticeEvents, LatticeCards) are called from outside LatticeLink only through LatticePresence's non-inlined
/// forwarders.
/// </summary>
public sealed class LatticeIsolationTests
{
    [Fact]
    public void OnlyLatticeLinkNamesLatticeAndOnlyLatticePresenceCallsLatticeBoard()
    {
        string mod = System.IO.Path.Combine(StationGodMCP.Tests.CatalogueChecks.CatalogueFiles.RepositoryRoot(), "src",
            "StationGodMCP.Mod");
        string link = System.IO.Path.Combine(mod, "LatticeLink") + System.IO.Path.DirectorySeparatorChar;
        List<string> wrong = new List<string>();
        foreach (string file in System.IO.Directory.GetFiles(mod, "*.cs", System.IO.SearchOption.AllDirectories))
        {
            if (file.Contains(System.IO.Path.DirectorySeparatorChar + "obj" + System.IO.Path.DirectorySeparatorChar))
            {
                continue;
            }

            string text = System.IO.File.ReadAllText(file);
            bool inLink = file.StartsWith(link, StringComparison.OrdinalIgnoreCase);
            if (!inLink && text.Contains("Lattice.Api"))
            {
                wrong.Add($"{file} names Lattice.Api");
            }

            foreach (string typed in new[] { "LatticeBoard.", "LatticeVerbs.", "LatticeEvents.", "LatticeCards." })
            {
                if (!inLink && text.Contains(typed))
                {
                    wrong.Add($"{file} calls {typed.TrimEnd('.')}");
                }
            }
        }

        Assert.Empty(wrong);
    }

    // Lattice's Port.Toward points out of the piece; StationGod's ports (find ghost, {ghost, port}) say the end a joining
    // piece needs, back into the port.
    [Fact]
    public void APortsTowardIsTurnedIntoThePort()
    {
        Assert.Equal("-y", LatticeGhosts.IntoPort(L.Face.PlusY));
        Assert.Equal("+x", LatticeGhosts.IntoPort(L.Face.MinusX));
        Assert.Equal("+z", LatticeGhosts.IntoPort(L.Face.MinusZ));
        Assert.Equal("Output", LatticeGhosts.RoleOf(L.Option.Some(L.PortRole.Output)));
        Assert.Null(LatticeGhosts.RoleOf(L.Option<L.PortRole>.None));
    }
}
