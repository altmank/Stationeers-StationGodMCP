#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// The gas check of every pipe job (GasAudit, GasRefills, GasCheckView) and the mechanism it guards against: the
/// game's merge queues the old network's gas for the survivor and applies it only at the next tick, so two merges in
/// one held tick copy a survivor that has not received the first merge's gas yet. The 2026-09-28 incidents: a run
/// that joined a gas network and closed a loop lost the first network's gas (4190 mol, then 11526 mol), and a
/// change at an in-line tank's port left the tank network's 1433 mol in a pipeless network its devices still used.
/// </summary>
public sealed class GasAuditTests
{
    private const int Types = 3;
    private const int Methane = 0;
    private const int Ozone = 1;
    private const int NitrousOxide = 2;

    private static GasMix Mix(params (int type, double mol, double joules)[] parts)
    {
        double[] moles = new double[Types];
        double[] energies = new double[Types];
        foreach ((int type, double mol, double joules) in parts)
        {
            moles[type] += mol;
            energies[type] += joules;
        }

        return new GasMix(moles, energies);
    }

    private static GasMix Empty => GasMix.Empty(Types);

    private static NetworkGas Net(long id, GasMix gas, double volumeL, long[] members, params long[] devices) =>
        new NetworkGas(id, gas, volumeL, members, devices);

    private static NetworkGas Ghost(long id, GasMix gas, params long[] devices) =>
        new NetworkGas(id, gas, 0.0, new long[0], devices);

    [Fact]
    public void AMergeThatKeepsEveryMoleIsOk()
    {
        List<NetworkGas> before = new List<NetworkGas>
        {
            Net(10, Mix((Methane, 100, 2_000_000)), 20, new long[] { 1, 2 }),
            Net(20, Mix((Methane, 50, 1_000_000)), 10, new long[] { 3 })
        };
        List<NetworkGas> after = new List<NetworkGas>
        {
            Net(10, Mix((Methane, 150, 3_000_000)), 40, new long[] { 1, 2, 3, 4 })
        };

        GasAudit audit = GasAudit.Of(before, after, GasTolerance.Default);

        Assert.True(audit.Ok);
        GasFamily family = Assert.Single(audit.Families);
        Assert.Equal(new long[] { 10, 20 }, family.Before.ConvertAll(static n => n.Id));
        Assert.Equal(new long[] { 10 }, family.After.ConvertAll(static n => n.Id));
        Assert.Equal(0.0, family.MissingMol, 9);
        Assert.Empty(GasRefills.For(audit, GasTolerance.Default));
    }

    [Fact]
    public void UntouchedNetworksAreLeftOut()
    {
        NetworkGas bystander = Net(99, Mix((Ozone, 7, 10_000)), 5, new long[] { 90, 91 });
        List<NetworkGas> before = new List<NetworkGas> { bystander, Net(10, Empty, 5, new long[] { 1 }) };
        List<NetworkGas> after = new List<NetworkGas> { bystander, Net(10, Empty, 10, new long[] { 1, 2 }) };

        GasAudit audit = GasAudit.Of(before, after, GasTolerance.Default);

        GasFamily family = Assert.Single(audit.Families);
        Assert.False(family.Contains(99));
        Assert.True(audit.Ok);
    }

    [Fact]
    public void ASplitDividingTheContentsIsOk()
    {
        // Pipe 2 removed: the game divides network 10's 90 mol by volume among the networks rebuilt from its
        // neighbours (NetworkAtmosphereEvent.Apply).
        List<NetworkGas> before = new List<NetworkGas>
        {
            Net(10, Mix((Methane, 90, 900)), 35, new long[] { 1, 2, 3 })
        };
        List<NetworkGas> after = new List<NetworkGas>
        {
            Net(11, Mix((Methane, 30, 300)), 10, new long[] { 1 }),
            Net(12, Mix((Methane, 60, 600)), 20, new long[] { 3 })
        };

        GasAudit audit = GasAudit.Of(before, after, GasTolerance.Default);

        Assert.True(audit.Ok);
        GasFamily family = Assert.Single(audit.Families);
        Assert.Equal(new long[] { 11, 12 }, family.After.ConvertAll(static n => n.Id));
    }

    [Fact]
    public void TheDoubleMergeLossIsFoundAndLocatedAndItsRefillRestoresTheFamily()
    {
        // Live 2026-09-28 (run-29): the run joined gas network 10 (merge 1: its gas queued for network 30, which the
        // job's pieces had formed), then its extra end joined network 20 (merge 2: network 30 copied while still
        // empty and merged into 20). The first event then filled network 30's atmosphere: 30 is left with no pipes,
        // holding network 10's gas, and network 20 never gets it.
        GasMix fuel = Mix((Methane, 11526, 250_000_000));
        List<NetworkGas> before = new List<NetworkGas>
        {
            Net(10, fuel, 100, new long[] { 1, 2 }),
            Net(20, Mix((Methane, 40, 900_000)), 50, new long[] { 5 })
        };
        List<NetworkGas> after = new List<NetworkGas>
        {
            Net(20, Mix((Methane, 40, 900_000)), 200, new long[] { 1, 2, 5, 100, 101 }),
            Ghost(30, fuel, 777)
        };

        GasAudit audit = GasAudit.Of(before, after, GasTolerance.Default);

        Assert.False(audit.Ok);
        GasFamily family = Assert.Single(audit.Families);
        Assert.False(family.Conserved);
        Assert.Equal(11526.0, family.MissingMol, 6);
        GasNetworkGhost ghost = Assert.Single(audit.Ghosts);
        Assert.Equal(30, ghost.Network.Id);
        Assert.Null(ghost.Family);

        GasRefill refill = Assert.Single(GasRefills.For(audit, GasTolerance.Default));
        Assert.Equal(20, refill.Into);
        Assert.Equal(11526.0, refill.Gas.MolesOf(Methane), 6);
        Assert.Equal(250_000_000.0, refill.Gas.EnergyOf(Methane), 3);

        // After the refill and the ghost cleared, the same two readings agree.
        List<NetworkGas> repaired = new List<NetworkGas>
        {
            Net(20, Mix((Methane, 40, 900_000)).Plus(refill.Gas), 200, new long[] { 1, 2, 5, 100, 101 })
        };
        Assert.True(GasAudit.Of(before, repaired, GasTolerance.Default).Ok);
    }

    [Fact]
    public void TheInLineTankGhostIsReportedWithTheDevicesStillOnIt()
    {
        // Live 2026-09-28 (run-45): the tank network's 1433 mol sat in pipeless network 171238, which the pump and
        // fill connector still read; the tank's own network read 0 mol.
        GasMix oxidiser = Mix((Ozone, 1000, 6_000_000), (NitrousOxide, 433, 3_000_000));
        List<NetworkGas> before = new List<NetworkGas>
        {
            Net(124742, oxidiser, 60, new long[] { 124640, 124683 }, 501, 502)
        };
        List<NetworkGas> after = new List<NetworkGas>
        {
            Net(171240, Empty, 90, new long[] { 124640, 200001, 200002 }, 501, 502, 503),
            Ghost(171238, oxidiser, 501, 502, 503)
        };

        GasAudit audit = GasAudit.Of(before, after, GasTolerance.Default);

        Assert.False(audit.Ok);
        GasNetworkGhost ghost = Assert.Single(audit.Ghosts);
        Assert.Equal(new long[] { 501, 502, 503 }, ghost.Network.Devices);
        GasRefill refill = Assert.Single(GasRefills.For(audit, GasTolerance.Default));
        Assert.Equal(171240, refill.Into);
        Assert.Equal(1000.0, refill.Gas.MolesOf(Ozone), 9);
        Assert.Equal(433.0, refill.Gas.MolesOf(NitrousOxide), 9);

        GasCheckView view = GasCheckView.Of(audit, new List<GasRefill>(), new List<long>());
        Assert.False(view.Ok);
        Assert.StartsWith("GAS LOST: 1433 mol", view.Summary);
        Assert.Equal(3, view.Ghosts[0].Devices.Count);
    }

    [Fact]
    public void AGhostThatHeldTheFamilysPipesKnowsItsFamily()
    {
        // Network 10 itself was merged away while awaiting an event: its id is still listed, with no pipes.
        List<NetworkGas> before = new List<NetworkGas>
        {
            Net(10, Mix((Methane, 5, 50)), 10, new long[] { 1 }),
            Net(20, Empty, 10, new long[] { 2 })
        };
        List<NetworkGas> after = new List<NetworkGas>
        {
            Ghost(10, Mix((Methane, 5, 50))),
            Net(20, Empty, 30, new long[] { 1, 2, 3 })
        };

        GasAudit audit = GasAudit.Of(before, after, GasTolerance.Default);

        GasNetworkGhost ghost = Assert.Single(audit.Ghosts);
        Assert.NotNull(ghost.Family);
        Assert.Equal(new long[] { 20 }, ghost.Family!.After.ConvertAll(static n => n.Id));
    }

    [Fact]
    public void RemovingANetworksLastPipeDeletesItsContentsAndIsNotALoss()
    {
        List<NetworkGas> before = new List<NetworkGas> { Net(10, Mix((Methane, 3, 30)), 10, new long[] { 1 }) };
        List<NetworkGas> after = new List<NetworkGas>();

        GasAudit audit = GasAudit.Of(before, after, GasTolerance.Default);

        Assert.True(audit.Ok);
        Assert.True(Assert.Single(audit.Families).Emptied);
        Assert.Empty(GasRefills.For(audit, GasTolerance.Default));
        GasCheckView view = GasCheckView.Of(audit, new List<GasRefill>(), new List<long>());
        Assert.Equal(0.0, view.Families[0].MissingMol);
        Assert.Contains("3 mol went with the last pipes", view.Summary);
    }

    [Fact]
    public void AGhostThatWasAlreadyThereIsReportedButIsNotTheJobs()
    {
        NetworkGas old = Ghost(55, Mix((Ozone, 12, 100)), 7);
        List<NetworkGas> before = new List<NetworkGas> { old, Net(10, Empty, 5, new long[] { 1 }) };
        List<NetworkGas> after = new List<NetworkGas> { old, Net(10, Empty, 10, new long[] { 1, 2 }) };

        GasAudit audit = GasAudit.Of(before, after, GasTolerance.Default);

        Assert.True(audit.Ok);
        Assert.Empty(audit.Ghosts);
        Assert.Equal(55, Assert.Single(audit.OldGhosts).Id);
    }

    [Fact]
    public void GasThatAppearsFailsTheCheckAndIsNotRefilled()
    {
        List<NetworkGas> before = new List<NetworkGas> { Net(10, Mix((Methane, 10, 100)), 5, new long[] { 1 }) };
        List<NetworkGas> after = new List<NetworkGas>
        {
            Net(10, Mix((Methane, 20, 200)), 10, new long[] { 1, 2 })
        };

        GasAudit audit = GasAudit.Of(before, after, GasTolerance.Default);

        Assert.False(audit.Ok);
        Assert.Equal(-10.0, audit.Families[0].MissingMol, 9);
        Assert.Empty(GasRefills.For(audit, GasTolerance.Default));
    }

    [Fact]
    public void ARefillIsSharedByVolumeLikeTheGamesSplit()
    {
        List<NetworkGas> before = new List<NetworkGas> { Net(10, Mix((Methane, 90, 900)), 30, new long[] { 1, 2, 3 }) };
        List<NetworkGas> after = new List<NetworkGas>
        {
            Net(11, Empty, 10, new long[] { 1 }),
            Net(12, Empty, 20, new long[] { 3 })
        };

        List<GasRefill> refills = GasRefills.For(GasAudit.Of(before, after, GasTolerance.Default),
            GasTolerance.Default);

        Assert.Equal(2, refills.Count);
        Assert.Equal(30.0, refills[0].Gas.TotalMol, 9);
        Assert.Equal(60.0, refills[1].Gas.TotalMol, 9);
    }

    [Fact]
    public void RoundingInTheGamesArithmeticIsNotALoss()
    {
        List<NetworkGas> before = new List<NetworkGas>
        {
            Net(10, Mix((Methane, 11526.123456, 250_000_000)), 10, new long[] { 1 })
        };
        List<NetworkGas> after = new List<NetworkGas>
        {
            Net(10, Mix((Methane, 11526.123456 * (1 + 1e-9), 250_000_000 + 0.5)), 20, new long[] { 1, 2 })
        };

        Assert.True(GasAudit.Of(before, after, GasTolerance.Default).Ok);
    }

    [Theory]
    [InlineData("applied", true, true, 0, "applied")]
    [InlineData("applied", true, true, 1, "applied_with_differences")]
    [InlineData("applied", true, false, 0, "gas_lost")]
    [InlineData("stopped", true, false, 0, "gas_lost")]
    [InlineData("applied_with_differences", true, false, 1, "gas_lost")]
    [InlineData("stopped", true, true, 1, "stopped")]
    [InlineData("applied", false, false, 0, "applied")]
    public void AFailedGasCheckOverridesTheJobStatus(string status, bool isChecked, bool ok, int refills,
        string expected)
    {
        GasCheckView check = isChecked ? CheckWith(ok, refills) : GasCheckView.Unchecked("a save");

        Assert.Equal(expected, GasCheckView.JobStatus(status, check));
        Assert.Equal(status, GasCheckView.JobStatus(status, null));
    }

    private static GasCheckView CheckWith(bool ok, int refills)
    {
        List<NetworkGas> before = new List<NetworkGas> { Net(10, Mix((Methane, 10, 100)), 5, new long[] { 1 }) };
        List<NetworkGas> after = new List<NetworkGas>
        {
            Net(10, ok ? Mix((Methane, 10, 100)) : Empty, 10, new long[] { 1, 2 })
        };
        List<GasRefill> recovered = new List<GasRefill>();
        for (int index = 0; index < refills; index++)
        {
            recovered.Add(new GasRefill(10, Mix((Methane, 10, 100))));
        }

        return GasCheckView.Of(GasAudit.Of(before, after, GasTolerance.Default), recovered, new List<long>());
    }

    [Fact]
    public void APipeRunCarriesItsGasCheckAndACableRunDoesNot()
    {
        GasCheckView check = CheckWith(true, 0);
        RunJobView pipes = new RunJobView("run-3", "place_pipes", "applied", null!,
            new RunJobResultView(null, null, null, null, check));
        RunJobView cables = new RunJobView("run-4", "place_cables", "applied", null!,
            new RunJobResultView(null, null, null, null));

        JObject json = JObject.Parse(WireCheck.New(pipes));
        Assert.True((bool)json["gas_check"]!["ok"]!);
        Assert.True((bool)json["gas_check"]!["checked"]!);
        Assert.Equal("10", (string?)json["gas_check"]!["families"]![0]!["networks_before"]![0]);
        Assert.Equal(10.0, (double)json["gas_check"]!["families"]![0]!["mol_after"]!);
        Assert.Null(JObject.Parse(WireCheck.New(cables))["gas_check"]);
    }

    // ---- The game's merge, as a model: why a job must apply the queued gas after every piece. ----

    [Fact]
    public void TwoMergesInOneTickLoseTheFirstNetworksGas()
    {
        MergeModel game = new MergeModel();
        game.Network(10, 100, 1, 2);
        game.Network(20, 40, 5);
        game.Network(30, 0, 100);
        List<NetworkGas> before = game.Read();

        game.Merge(survivor: 30, old: 10);
        game.Merge(survivor: 20, old: 30);
        game.ApplyQueued();

        GasAudit audit = GasAudit.Of(before, game.Read(), GasTolerance.Default);
        Assert.False(audit.Ok);
        Assert.Equal(100.0, audit.Families[0].MissingMol, 9);
        Assert.Equal(30, Assert.Single(audit.Ghosts).Network.Id);
    }

    [Fact]
    public void ApplyingTheQueuedGasAfterEachMergeKeepsIt()
    {
        MergeModel game = new MergeModel();
        game.Network(10, 100, 1, 2);
        game.Network(20, 40, 5);
        game.Network(30, 0, 100);
        List<NetworkGas> before = game.Read();

        game.Merge(survivor: 30, old: 10);
        game.ApplyQueued();
        game.Merge(survivor: 20, old: 30);
        game.ApplyQueued();

        GasAudit audit = GasAudit.Of(before, game.Read(), GasTolerance.Default);
        Assert.True(audit.Ok);
        Assert.Empty(audit.Ghosts);
        Assert.Equal(140.0, Assert.Single(audit.Families).GasAfter.TotalMol, 9);
    }

    /// <summary>
    /// AtmosphericsNetwork.Merge and the tick's event queue, reduced to moles: the merge copies the old network's gas
    /// now and queues it for the survivor (marking the survivor's atmosphere as awaiting); the old network's members
    /// move at once, and it is dropped unless its own atmosphere awaits an event (ReferencableNetwork.RefreshNetwork).
    /// </summary>
    private sealed class MergeModel
    {
        private readonly Dictionary<long, double> _gas = new Dictionary<long, double>();
        private readonly Dictionary<long, List<long>> _members = new Dictionary<long, List<long>>();
        private readonly HashSet<long> _awaiting = new HashSet<long>();
        private readonly HashSet<long> _listed = new HashSet<long>();
        private readonly Queue<(long into, double mol)> _queue = new Queue<(long into, double mol)>();

        internal void Network(long id, double mol, params long[] members)
        {
            _gas[id] = mol;
            _members[id] = new List<long>(members);
            _listed.Add(id);
        }

        internal void Merge(long survivor, long old)
        {
            _queue.Enqueue((survivor, _gas[old]));
            _awaiting.Add(survivor);
            _members[survivor].AddRange(_members[old]);
            _members[old].Clear();
            if (!_awaiting.Contains(old))
            {
                _listed.Remove(old);
            }
        }

        internal void ApplyQueued()
        {
            while (_queue.Count > 0)
            {
                (long into, double mol) = _queue.Dequeue();
                _gas[into] += mol;
                _awaiting.Remove(into);
            }
        }

        internal List<NetworkGas> Read()
        {
            List<NetworkGas> read = new List<NetworkGas>();
            foreach (long id in _listed)
            {
                read.Add(new NetworkGas(id, Mix((Methane, _gas[id], _gas[id] * 100)), _members[id].Count,
                    _members[id].ToArray(), new long[0]));
            }

            return read;
        }
    }
}
