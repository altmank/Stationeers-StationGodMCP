#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// LIVE 2026-10-08: a client joined to the dedicated server threw in Pipe.DeserializeOnJoin while applying the state
/// packet of a place_pipes run with seven branches (run-18, 71 pieces, joined to a standing network), lost the rest of
/// that packet (Thing.DeserializeDeltaState later threw for the run's last pipe, 4980680), and then threw at
/// StructureNetwork.DeserializeDeltaState on every packet: the host kept 4980670, the network of the sixth branch's
/// first piece, which the client no longer had. The run was built in the plan's order with nothing standing to grow
/// from, so each branch started alone on a network of its own. ClientReplay plays a frame of placements as the host
/// registers them (Pipe.OnRegistered) and as a client deserializes the packet (Pipe.DeserializeOnJoin).
/// </summary>
public sealed class ClientNetworkReplayTests
{
    private static GridCell AtZ(int z) => CleanModels.At(0, 0, z);

    // A standing piece at z 0 (network 100); new pieces at z 1..4. The plan lists a (z 2) and c (z 4) before the
    // pieces that join them to anything; d meets c's network before a's.
    private static PieceModel Standing() => CleanModels.Piece(1, AtZ(0), AtZ(1));

    private static List<PieceModel> Run() => new List<PieceModel>
    {
        CleanModels.Piece(-1, AtZ(2), AtZ(1), AtZ(3)),
        CleanModels.Piece(-2, AtZ(1), AtZ(0), AtZ(2)),
        CleanModels.Piece(-3, AtZ(4), AtZ(3)),
        CleanModels.Piece(-4, AtZ(3), AtZ(4), AtZ(2))
    };

    [Fact]
    public void ThePlanOrderLosesTheClientANetworkTheHostKeeps()
    {
        ReplayResult result = ClientReplay.Play(new[] { Standing() }, new long[] { 100 }, Run());

        Assert.True(result.ClientThrew);
    }

    [Fact]
    public void GrownFromTheStandingPieceTheClientKeepsEveryNetwork()
    {
        List<PieceModel> ordered = GrowthOrder.From(new[] { Standing() }, Run(), static piece => piece);

        ReplayResult result = ClientReplay.Play(new[] { Standing() }, new long[] { 100 }, ordered);

        Assert.Equal(new long[] { -2, -1, -4, -3 }, ordered.ConvertAll(static piece => piece.Id));
        Assert.False(result.ClientThrew);
        Assert.True(result.SameAsHost);
        Assert.Equal(new long[] { 100 }, result.HostNetworks);
    }

    [Fact]
    public void WithNothingStandingTheRunStillGrowsFromItsFirstPiece()
    {
        List<PieceModel> run = new List<PieceModel>
        {
            CleanModels.Piece(-1, AtZ(0), AtZ(1)),
            CleanModels.Piece(-2, AtZ(2), AtZ(1), AtZ(3)),
            CleanModels.Piece(-3, AtZ(1), AtZ(0), AtZ(2)),
            CleanModels.Piece(-4, AtZ(3), AtZ(2))
        };

        List<PieceModel> ordered = GrowthOrder.From(new List<PieceModel>(), run, static piece => piece);

        Assert.Equal(new long[] { -1, -3, -2, -4 }, ordered.ConvertAll(static piece => piece.Id));
    }

    // Random trees on the grid, cut where standing pieces of one or two networks meet them, built in a random order
    // grown from the standing pieces: the client ends with the host's networks, piece for piece.
    [Fact]
    public void GrownRunsKeepEveryClientInStepWithTheHost()
    {
        Random random = new Random(20261008);
        int planOrderFailures = 0;
        for (int trial = 0; trial < 400; trial++)
        {
            RandomRun run = RandomRun.Make(random);
            ReplayResult planOrder = ClientReplay.Play(run.Standing, run.StandingNetworks, run.Pieces);
            if (planOrder.ClientThrew || !planOrder.SameAsHost)
            {
                planOrderFailures++;
            }

            List<PieceModel> ordered = GrowthOrder.From(run.Standing, run.Pieces, static piece => piece);
            ReplayResult grown = ClientReplay.Play(run.Standing, run.StandingNetworks, ordered);

            Assert.False(grown.ClientThrew, $"trial {trial}");
            Assert.True(grown.SameAsHost, $"trial {trial}");
        }

        Assert.True(planOrderFailures > 0, "the random plan orders never reproduced the live failure");
    }

    private sealed class RandomRun
    {
        private RandomRun(List<PieceModel> standing, List<long> standingNetworks, List<PieceModel> pieces)
        {
            Standing = standing;
            StandingNetworks = standingNetworks;
            Pieces = pieces;
        }

        internal List<PieceModel> Standing { get; }

        internal List<long> StandingNetworks { get; }

        internal List<PieceModel> Pieces { get; }

        internal static RandomRun Make(Random random)
        {
            int size = random.Next(6, 40);
            List<GridCell> cells = new List<GridCell> { CleanModels.At(0, 0, 0) };
            Dictionary<GridCell, List<GridCell>> tree = new Dictionary<GridCell, List<GridCell>>
            {
                [cells[0]] = new List<GridCell>()
            };
            int[][] steps = { new[] { 1, 0 }, new[] { -1, 0 }, new[] { 0, 1 }, new[] { 0, -1 } };
            int attempts = 0;
            while (cells.Count < size && attempts++ < 2000)
            {
                GridCell from = cells[random.Next(cells.Count)];
                int[] step = steps[random.Next(steps.Length)];
                GridCell to = new GridCell(from.X + step[0] * CleanModels.Step, 0, from.Z + step[1] * CleanModels.Step);
                if (tree.ContainsKey(to))
                {
                    continue;
                }

                cells.Add(to);
                tree[to] = new List<GridCell> { from };
                tree[from].Add(to);
            }

            List<GridCell> leaves = cells.FindAll(cell => tree[cell].Count == 1);
            int standingCount = Math.Min(leaves.Count, random.Next(0, 4));
            HashSet<GridCell> standingCells = new HashSet<GridCell>();
            while (standingCells.Count < standingCount)
            {
                standingCells.Add(leaves[random.Next(leaves.Count)]);
            }

            List<PieceModel> standing = new List<PieceModel>();
            List<long> networks = new List<long>();
            List<PieceModel> pieces = new List<PieceModel>();
            long nextId = 1;
            foreach (GridCell cell in cells)
            {
                List<GridCell> towards = new List<GridCell>(tree[cell]);
                Shuffle(random, towards);
                PieceModel piece = CleanModels.Piece(nextId++, cell, towards.ToArray());
                if (standingCells.Contains(cell))
                {
                    standing.Add(piece);
                    networks.Add(100 + random.Next(2));
                }
                else
                {
                    pieces.Add(piece);
                }
            }

            Shuffle(random, pieces);
            return new RandomRun(standing, networks, pieces);
        }

        private static void Shuffle<T>(Random random, List<T> list)
        {
            for (int index = list.Count - 1; index > 0; index--)
            {
                int other = random.Next(index + 1);
                (list[index], list[other]) = (list[other], list[index]);
            }
        }
    }
}

internal sealed class ReplayResult
{
    internal ReplayResult(bool clientThrew, bool sameAsHost, long[] hostNetworks)
    {
        ClientThrew = clientThrew;
        SameAsHost = sameAsHost;
        HostNetworks = hostNetworks;
    }

    /// <summary>A piece named a network the client does not have and met none (Pipe.DeserializeOnJoin's NRE).</summary>
    internal bool ClientThrew { get; }

    /// <summary>Every piece is in the network with the host's id, and the client has every network the host keeps.</summary>
    internal bool SameAsHost { get; }

    internal long[] HostNetworks { get; }
}

/// <summary>
/// One frame of placements, as the host makes them and as a client replays the state packet that carries them.
/// Host (Pipe.OnRegistered): a piece's connected neighbours' networks, in the order of its ends, are merged into the
/// first (StructureNetwork.Merge: the first survives, each other one is emptied and deregistered); with none, the
/// piece gets a new network. Client (Pipe.DeserializeOnJoin): the new networks of the packet are created first; each
/// piece is created in the host's order and merges its connected neighbours' networks the same way, and with none goes
/// into the network the host names for it when the packet is written (its final one), which must still be registered.
/// </summary>
internal static class ClientReplay
{
    internal static ReplayResult Play(IReadOnlyList<PieceModel> standing, IReadOnlyList<long> standingNetworks,
        IReadOnlyList<PieceModel> built)
    {
        World host = new World(standing, standingNetworks);
        long nextNetwork = 1000;
        List<long> created = new List<long>();
        foreach (PieceModel piece in built)
        {
            List<long> met = host.Place(piece);
            if (met.Count == 0)
            {
                long fresh = nextNetwork++;
                created.Add(fresh);
                host.Join(piece, fresh);
            }
            else
            {
                host.Join(piece, host.Merge(met));
            }
        }

        World client = new World(standing, standingNetworks);
        client.Registered.UnionWith(created);
        foreach (PieceModel piece in built)
        {
            List<long> met = client.Place(piece);
            if (met.Count > 0)
            {
                client.Join(piece, client.Merge(met));
                continue;
            }

            long named = host.NetworkOf[piece.Id];
            if (!client.Registered.Contains(named))
            {
                return new ReplayResult(true, false, host.Alive());
            }

            client.Join(piece, named);
        }

        bool same = true;
        foreach (KeyValuePair<long, long> entry in host.NetworkOf)
        {
            same &= client.NetworkOf.TryGetValue(entry.Key, out long network) && network == entry.Value;
        }

        foreach (long network in host.Alive())
        {
            same &= client.Registered.Contains(network);
        }

        return new ReplayResult(false, same, host.Alive());
    }

    private sealed class World
    {
        private readonly List<PieceModel> _present = new List<PieceModel>();

        internal World(IReadOnlyList<PieceModel> standing, IReadOnlyList<long> networks)
        {
            for (int index = 0; index < standing.Count; index++)
            {
                _present.Add(standing[index]);
                NetworkOf[standing[index].Id] = networks[index];
                Registered.Add(networks[index]);
            }
        }

        internal Dictionary<long, long> NetworkOf { get; } = new Dictionary<long, long>();

        internal HashSet<long> Registered { get; } = new HashSet<long>();

        // The networks of the pieces present that the new piece connects to, in the order of its ends.
        internal List<long> Place(PieceModel piece)
        {
            List<long> met = new List<long>();
            foreach (PieceEnd end in piece.Ends)
            {
                foreach (PieceModel other in _present)
                {
                    bool linked = Connectivity.Links(piece, other) || Connectivity.Links(other, piece);
                    if (linked && other.Occupies(end.Local) && !met.Contains(NetworkOf[other.Id]))
                    {
                        met.Add(NetworkOf[other.Id]);
                    }
                }
            }

            _present.Add(piece);
            return met;
        }

        internal long Merge(List<long> met)
        {
            long survivor = met[0];
            for (int index = 1; index < met.Count; index++)
            {
                foreach (long id in new List<long>(NetworkOf.Keys))
                {
                    if (NetworkOf[id] == met[index])
                    {
                        NetworkOf[id] = survivor;
                    }
                }

                Registered.Remove(met[index]);
            }

            return survivor;
        }

        internal void Join(PieceModel piece, long network) => NetworkOf[piece.Id] = network;

        internal long[] Alive()
        {
            SortedSet<long> alive = new SortedSet<long>(NetworkOf.Values);
            return new List<long>(alive).ToArray();
        }
    }
}
