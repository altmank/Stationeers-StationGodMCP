#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

public sealed class LoopCuttingTests
{
    private const long Device = 900;

    /// <summary>
    /// The shape of the 2026-09-27 incident (Ingot Vault 132000): the device's power port joins cell (0,0,0) and its
    /// data port cell (0,0,2), both towards +x. The power route ran from the power port through the data port cell
    /// (a 4-way there) and on to the network (trunk at x = -3); the data route, planned separately, ran from the data
    /// port cell as a parallel run to the same trunk, closing a loop.
    /// </summary>
    private static (List<PieceModel> Pieces, PieceModel Device) Incident()
    {
        List<PieceModel> pieces = new List<PieceModel>
        {
            RunModels.Piece(1, RunModels.At(0, 0, 0), "+x", "+z"),
            RunModels.Piece(2, RunModels.At(0, 0, 1), "-z", "+z"),
            RunModels.Piece(3, RunModels.At(0, 0, 2), "-z", "+x", "-x", "+z"),
            RunModels.Piece(4, RunModels.At(-1, 0, 2), "+x", "-x"),
            RunModels.Piece(5, RunModels.At(-2, 0, 2), "+x", "-x"),
            RunModels.Piece(20, RunModels.At(-3, 0, 0), "-z", "+z"),
            RunModels.Piece(21, RunModels.At(-3, 0, 1), "-z", "+z"),
            RunModels.Piece(22, RunModels.At(-3, 0, 2), "-z", "+z", "+x"),
            RunModels.Piece(23, RunModels.At(-3, 0, 3), "-z", "+z", "+x"),
            RunModels.Piece(24, RunModels.At(-3, 0, 4), "-z", "+z"),
            RunModels.Piece(10, RunModels.At(0, 0, 3), "-z", "-x"),
            RunModels.Piece(11, RunModels.At(-1, 0, 3), "+x", "-x"),
            RunModels.Piece(12, RunModels.At(-2, 0, 3), "+x", "-x")
        };
        PieceModel device = new PieceModel(Device,
            new[] { RunModels.At(1, 0, 0), RunModels.At(1, 0, 1), RunModels.At(1, 0, 2) },
            new[]
            {
                new PieceEnd(RunModels.At(0, 0, 0), RunModels.At(1, 0, 0), RunModels.PowerAndData, 0),
                new PieceEnd(RunModels.At(0, 0, 2), RunModels.At(1, 0, 2), RunModels.PowerAndData, 0)
            }, null);
        return (pieces, device);
    }

    private static LoopResult Find(List<PieceModel> pieces, PieceModel device, HashSet<long>? keep = null,
        Dictionary<long, string>? blocked = null) =>
        LoopCutting.Find(pieces, new[] { device }, new HashSet<long> { Device },
            blocked ?? new Dictionary<long, string>(), keep ?? new HashSet<long>());

    [Fact]
    public void TheIncidentLoopIsFoundAndBrokenByTheShorterParallelRun()
    {
        (List<PieceModel> pieces, PieceModel device) = Incident();
        LoopResult result = Find(pieces, device);

        NetworkLoop loop = Assert.Single(result.Loops);
        Assert.False(loop.Spared);
        Assert.Null(loop.Unbroken);
        Assert.Equal(new long[] { 3, 4, 5, 10, 11, 12, 22, 23 }, loop.Pieces);
        // Two chains close the loop: 4-5 (the power route's last two cells) and 10-11-12 (the parallel data run).
        Assert.Equal(new long[] { 4, 5 }, result.Cut);
    }

    [Fact]
    public void AfterTheCutEveryPieceAndPortIsStillJoined()
    {
        (List<PieceModel> pieces, PieceModel device) = Incident();
        HashSet<long> cut = new HashSet<long>(Find(pieces, device).Cut);
        List<PieceModel> left = pieces.FindAll(piece => !cut.Contains(piece.Id));

        HashSet<long> reached = new HashSet<long> { 20 };
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (PieceModel a in left)
            {
                foreach (PieceModel b in left)
                {
                    if (reached.Contains(a.Id) && !reached.Contains(b.Id) &&
                        (Connectivity.Links(a, b) || Connectivity.Links(b, a)))
                    {
                        reached.Add(b.Id);
                        grew = true;
                    }
                }
            }
        }

        Assert.Equal(left.Count, reached.Count);
        Assert.True(Connectivity.Links(device, left.Find(piece => piece.Id == 1)!));
        Assert.True(Connectivity.Links(device, left.Find(piece => piece.Id == 3)!));
    }

    [Fact]
    public void AKeepIdSparesTheWholeLoop()
    {
        (List<PieceModel> pieces, PieceModel device) = Incident();
        LoopResult result = Find(pieces, device, new HashSet<long> { 11 });

        NetworkLoop loop = Assert.Single(result.Loops);
        Assert.True(loop.Spared);
        Assert.Empty(result.Cut);
    }

    [Fact]
    public void ABlockedPieceMovesTheCutToTheOtherChain()
    {
        (List<PieceModel> pieces, PieceModel device) = Incident();
        LoopResult result = Find(pieces, device, blocked: new Dictionary<long, string> { [4] = "device_mounted" });

        Assert.Equal(new long[] { 10, 11, 12 }, result.Cut);
    }

    [Fact]
    public void PiecesLinkedToADeviceAreNeverCut()
    {
        // A ring through the device's port piece: the rest of the ring serves nothing else and goes whole.
        List<PieceModel> pieces = new List<PieceModel>
        {
            RunModels.Piece(1, RunModels.At(0, 0, 0), "+x", "+z"),
            RunModels.Piece(2, RunModels.At(1, 0, 0), "-x", "+z"),
            RunModels.Piece(3, RunModels.At(0, 0, 1), "-z", "+x", "-x"),
            RunModels.Piece(4, RunModels.At(1, 0, 1), "-z", "-x", "+x")
        };
        PieceModel device = new PieceModel(Device, new[] { RunModels.At(-1, 0, 1) },
            new[] { new PieceEnd(RunModels.At(0, 0, 1), RunModels.At(-1, 0, 1), RunModels.PowerAndData, 0) }, null);

        LoopResult result = Find(pieces, device);

        NetworkLoop loop = Assert.Single(result.Loops);
        Assert.DoesNotContain(3L, loop.Cut);
        Assert.Equal(new long[] { 1, 2, 4 }, loop.Cut);
    }

    [Fact]
    public void ALoopOfPiecesThatMayNotGoStaysUnbroken()
    {
        // A 2 x 2 ring: one piece is linked to a device, the other three may not be removed, so no chain has a piece
        // that may go.
        List<PieceModel> pieces = new List<PieceModel>
        {
            RunModels.Piece(1, RunModels.At(0, 0, 0), "+x", "+z"),
            RunModels.Piece(2, RunModels.At(1, 0, 0), "-x", "+z"),
            RunModels.Piece(3, RunModels.At(0, 0, 1), "-z", "+x"),
            RunModels.Piece(4, RunModels.At(1, 0, 1), "-z", "-x", "+x")
        };
        PieceModel device = new PieceModel(Device, new[] { RunModels.At(2, 0, 1) },
            new[] { new PieceEnd(RunModels.At(1, 0, 1), RunModels.At(2, 0, 1), RunModels.PowerAndData, 0) }, null);
        Dictionary<long, string> blocked = new Dictionary<long, string>
        {
            [1] = "device_mounted", [2] = "device_mounted", [3] = "device_mounted"
        };

        NetworkLoop loop = Assert.Single(Find(pieces, device, blocked: blocked).Loops);
        Assert.Empty(loop.Cut);
        Assert.Equal(LoopCutting.NoRemovableChain, loop.Unbroken);
    }

    [Fact]
    public void ATreeHasNoLoops()
    {
        List<PieceModel> pieces = new List<PieceModel>
        {
            RunModels.Piece(1, RunModels.At(0, 0, 0), "+x", "-x", "+z"),
            RunModels.Piece(2, RunModels.At(1, 0, 0), "-x", "+x"),
            RunModels.Piece(3, RunModels.At(0, 0, 1), "-z", "+z")
        };
        PieceModel device = new PieceModel(Device, new[] { RunModels.At(9, 0, 9) }, new PieceEnd[0], null);

        Assert.Empty(Find(pieces, device).Loops);
    }
}

public sealed class LoopCuttingRingTests
{
    [Fact]
    public void AnIsolatedRingIsFoundAndCutOnce()
    {
        List<PieceModel> ring = new List<PieceModel>
        {
            RunModels.Piece(1, RunModels.At(0, 0, 0), "+x", "+z"),
            RunModels.Piece(2, RunModels.At(1, 0, 0), "-x", "+z"),
            RunModels.Piece(3, RunModels.At(0, 0, 1), "-z", "+x"),
            RunModels.Piece(4, RunModels.At(1, 0, 1), "-z", "-x")
        };
        LoopResult result = LoopCutting.Find(ring, new PieceModel[0], new HashSet<long>(),
            new Dictionary<long, string>(), new HashSet<long>());

        NetworkLoop loop = Assert.Single(result.Loops);
        Assert.Single(loop.Cut);
        Assert.Null(loop.Unbroken);
    }
}
