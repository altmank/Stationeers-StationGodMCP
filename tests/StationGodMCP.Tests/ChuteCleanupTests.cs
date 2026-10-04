#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// clean_chutes' rule on synthetic networks in the x-z plane: device ports feed and drain lines, and every piece is
/// judged by whether an item can get from a source through it to a consumer.
/// </summary>
public sealed class ChuteCleanupTests
{
    private const long Feeder = 100;
    private const long Consumer = 200;

    private static GridCell At(int x, int z) => RunModels.At(x, 0, z);

    /// <summary>An overflow: Input towards `input`, Output towards `output`, Output2 (the overflow) towards `spill`.</summary>
    private static PieceModel Overflow(long id, GridCell cell, string input, string output, string spill) =>
        new PieceModel(id, new[] { cell }, new[]
        {
            End(cell, input, ChuteRoles.Input), End(cell, output, ChuteRoles.Output),
            End(cell, spill, ChuteRoles.Output2)
        }, null);

    private static PieceEnd End(GridCell cell, string toward, int role) =>
        new PieceEnd(RunModels.Step(toward).From(cell), cell, ChuteModels.Chute, role);

    private static DevicePort Pushes(long device, GridCell local, string toward) =>
        ChuteModels.Port(device, 1, local, toward, ChuteRoles.Output);

    private static DevicePort Takes(long device, GridCell local, string toward) =>
        ChuteModels.Port(device, 0, local, toward, ChuteRoles.Input);

    private static ChuteCleanupResult Plan(IReadOnlyList<PieceModel> pieces, IReadOnlyList<DevicePort> ports,
        Dictionary<long, string>? held = null, long[]? riding = null, long[]? outletDevices = null,
        bool plainPieces = true)
    {
        List<long> all = new List<long>();
        foreach (PieceModel piece in pieces)
        {
            all.Add(piece.Id);
        }

        return ChuteCleanup.Plan(new ChuteCleanupScene(pieces, ports, all, held ?? new Dictionary<long, string>(),
            riding ?? new long[0], outletDevices ?? new long[0], _ => plainPieces));
    }

    private static ChuteVerdict Of(ChuteCleanupResult result, long id) =>
        result.Verdicts.Find(verdict => verdict.Id == id) ?? throw new KeyNotFoundException($"no verdict for {id}");

    // A feeder's output into (0,0), along +x to a consumer's input beyond (2,0).
    private static List<PieceModel> MainLine() => new List<PieceModel>
    {
        ChuteModels.Plain(1, At(0, 0), "-x", "+x"),
        ChuteModels.Plain(2, At(1, 0), "-x", "+x"),
        ChuteModels.Plain(3, At(2, 0), "-x", "+x")
    };

    private static List<DevicePort> MainPorts() => new List<DevicePort>
    {
        Pushes(Feeder, At(0, 0), "-x"),
        Takes(Consumer, At(2, 0), "+x")
    };

    [Fact]
    public void AValidMachineToMachineLineIsLeftAlone()
    {
        ChuteCleanupResult result = Plan(MainLine(), MainPorts());

        Assert.Equal(3, result.Verdicts.Count);
        Assert.All(result.Verdicts, verdict => Assert.IsType<LiveChute>(verdict));
    }

    [Fact]
    public void ABranchFromAFeederThatReachesNoConsumerIsRemovedWhole()
    {
        // A second feeder pushes into (0,2); the branch runs +x and ends open at (2,2): items fall on the floor.
        List<PieceModel> pieces = MainLine();
        pieces.Add(ChuteModels.Plain(11, At(0, 2), "-x", "+x"));
        pieces.Add(ChuteModels.Plain(12, At(1, 2), "-x", "+x"));
        pieces.Add(ChuteModels.Plain(13, At(2, 2), "-x", "+x"));
        List<DevicePort> ports = MainPorts();
        ports.Add(Pushes(300, At(0, 2), "-x"));

        ChuteCleanupResult result = Plan(pieces, ports);

        foreach (long id in new long[] { 11, 12, 13 })
        {
            RemovedChute removed = Assert.IsType<RemovedChute>(Of(result, id));
            Assert.Equal(DeadChuteReason.NoConsumer, removed.Reason);
        }

        Assert.IsType<LiveChute>(Of(result, 2));
    }

    [Fact]
    public void AnOrphanPieceIsRemoved()
    {
        List<PieceModel> pieces = MainLine();
        pieces.Add(ChuteModels.Plain(20, At(5, 5), "-x", "+x"));

        RemovedChute removed = Assert.IsType<RemovedChute>(Of(Plan(pieces, MainPorts()), 20));

        Assert.Equal(DeadChuteReason.Orphan, removed.Reason);
    }

    [Fact]
    public void AJunctionThatLosesItsSideBranchBecomesAStraight()
    {
        // The junction at (1,0) merges a side branch from +z into the main line; nothing feeds the side branch.
        List<PieceModel> pieces = MainLine();
        pieces[1] = ChuteModels.Junction(2, At(1, 0), "-x", "+z", "+x");
        pieces.Add(ChuteModels.Plain(21, At(1, 1), "-z", "+z"));
        pieces.Add(ChuteModels.Plain(22, At(1, 2), "-z", "+z"));

        ChuteCleanupResult result = Plan(pieces, MainPorts());

        Assert.Equal(DeadChuteReason.NoSource, Assert.IsType<RemovedChute>(Of(result, 21)).Reason);
        Assert.IsType<RemovedChute>(Of(result, 22));
        ReducedChute reduced = Assert.IsType<ReducedChute>(Of(result, 2));
        Assert.Equal(new[] { "-x", "+x" }, EndCleanup.DirectionsOf(reduced.Plain.Ends));
        Assert.All(reduced.Plain.Ends, end => Assert.Equal(ChuteRoles.None, end.Role));
    }

    [Fact]
    public void AJunctionWithAnInputAlreadyLeadingNowhereBecomesAStraight()
    {
        List<PieceModel> pieces = MainLine();
        pieces[1] = ChuteModels.Junction(2, At(1, 0), "-x", "+z", "+x");

        ReducedChute reduced = Assert.IsType<ReducedChute>(Of(Plan(pieces, MainPorts()), 2));

        Assert.Equal(new[] { "-x", "+x" }, EndCleanup.DirectionsOf(reduced.Plain.Ends));
    }

    [Fact]
    public void AnOverflowWhoseSpillBranchLeadsNowhereBecomesAStraight()
    {
        List<PieceModel> pieces = MainLine();
        pieces[1] = Overflow(2, At(1, 0), "-x", "+x", "+z");
        pieces.Add(ChuteModels.Plain(31, At(1, 1), "-z", "+z"));
        pieces.Add(ChuteModels.Plain(32, At(1, 2), "-z", "+z"));

        ChuteCleanupResult result = Plan(pieces, MainPorts());

        Assert.Equal(DeadChuteReason.NoConsumer, Assert.IsType<RemovedChute>(Of(result, 31)).Reason);
        Assert.IsType<RemovedChute>(Of(result, 32));
        ReducedChute reduced = Assert.IsType<ReducedChute>(Of(result, 2));
        Assert.Equal(new[] { "-x", "+x" }, EndCleanup.DirectionsOf(reduced.Plain.Ends));
    }

    [Fact]
    public void AnOverflowWhoseMainOutputLeadsNowhereBecomesACornerOntoItsSpill()
    {
        // The consumer is on the spill side: (1,1) then into a device beyond +z; the straight output runs open.
        List<PieceModel> pieces = new List<PieceModel>
        {
            ChuteModels.Plain(1, At(0, 0), "-x", "+x"),
            Overflow(2, At(1, 0), "-x", "+x", "+z"),
            ChuteModels.Plain(3, At(2, 0), "-x", "+x"),
            ChuteModels.Plain(4, At(1, 1), "-z", "+z")
        };
        List<DevicePort> ports = new List<DevicePort>
        {
            Pushes(Feeder, At(0, 0), "-x"),
            Takes(Consumer, At(1, 1), "+z")
        };

        ChuteCleanupResult result = Plan(pieces, ports);

        Assert.IsType<RemovedChute>(Of(result, 3));
        ReducedChute reduced = Assert.IsType<ReducedChute>(Of(result, 2));
        Assert.Equal(new[] { "-x", "+z" }, EndCleanup.DirectionsOf(reduced.Plain.Ends));
    }

    [Fact]
    public void AnOverflowWithNoPlainPieceKeepsItsSpillBranchSoNothingFallsOut()
    {
        List<PieceModel> pieces = MainLine();
        pieces[1] = Overflow(2, At(1, 0), "-x", "+x", "+z");
        pieces.Add(ChuteModels.Plain(31, At(1, 1), "-z", "+z"));
        pieces.Add(ChuteModels.Plain(32, At(1, 2), "-z", "+z"));

        ChuteCleanupResult result = Plan(pieces, MainPorts(), plainPieces: false);

        KeptDeadChute first = Assert.IsType<KeptDeadChute>(Of(result, 31));
        Assert.Equal(ChuteCleanupNames.WouldDropItems, first.HeldBy);
        Assert.Equal(ChuteCleanupNames.WouldDropItems, Assert.IsType<KeptDeadChute>(Of(result, 32)).HeldBy);
        Assert.IsType<LiveChute>(Of(result, 2));
    }

    [Fact]
    public void APieceWithAnItemRidingStaysAndKeepsWhatTheItemMovesInto()
    {
        // A feeder's branch to nowhere: (0,2) -> (1,2) -> (2,2) open; an item rides in (1,2).
        List<PieceModel> pieces = MainLine();
        pieces.Add(ChuteModels.Plain(11, At(0, 2), "-x", "+x"));
        pieces.Add(ChuteModels.Plain(12, At(1, 2), "-x", "+x"));
        pieces.Add(ChuteModels.Plain(13, At(2, 2), "-x", "+x"));
        List<DevicePort> ports = MainPorts();
        ports.Add(Pushes(300, At(0, 2), "-x"));
        Dictionary<long, string> held = new Dictionary<long, string> { [12] = ChuteCleanupNames.ItemRiding };

        ChuteCleanupResult result = Plan(pieces, ports, held, riding: new long[] { 12 });

        Assert.IsType<RemovedChute>(Of(result, 11));
        Assert.Equal(ChuteCleanupNames.ItemRiding, Assert.IsType<KeptDeadChute>(Of(result, 12)).HeldBy);
        Assert.Equal(ChuteCleanupNames.WouldDropItems, Assert.IsType<KeptDeadChute>(Of(result, 13)).HeldBy);
    }

    [Fact]
    public void AnItemRidingInAJunctionKeepsItAsItIs()
    {
        List<PieceModel> pieces = MainLine();
        pieces[1] = ChuteModels.Junction(2, At(1, 0), "-x", "+z", "+x");
        pieces.Add(ChuteModels.Plain(21, At(1, 1), "-z", "+z"));
        Dictionary<long, string> held = new Dictionary<long, string> { [2] = ChuteCleanupNames.ItemRiding };

        ChuteCleanupResult result = Plan(pieces, MainPorts(), held, riding: new long[] { 2 });

        // The side branch only feeds the junction: removing it opens an input, out of which nothing falls.
        Assert.IsType<RemovedChute>(Of(result, 21));
        Assert.Equal(ChuteCleanupNames.ItemRiding, Assert.IsType<OpenEndedChute>(Of(result, 2)).HeldBy);
    }

    [Fact]
    public void AChuteDeviceThatWouldDropItemsKeepsThePieceItFeeds()
    {
        // Device 300 is a chute digital valve: with its output open it would drop items on the floor.
        List<PieceModel> pieces = MainLine();
        pieces.Add(ChuteModels.Plain(11, At(0, 2), "-x", "+x"));
        pieces.Add(ChuteModels.Plain(12, At(1, 2), "-x", "+x"));
        List<DevicePort> ports = MainPorts();
        ports.Add(Pushes(300, At(0, 2), "-x"));

        ChuteCleanupResult result = Plan(pieces, ports, outletDevices: new long[] { 300 });

        Assert.Equal(ChuteCleanupNames.WouldDropItems, Assert.IsType<KeptDeadChute>(Of(result, 11)).HeldBy);
        Assert.Equal(ChuteCleanupNames.WouldDropItems, Assert.IsType<KeptDeadChute>(Of(result, 12)).HeldBy);
    }

    [Fact]
    public void AClosedLoopNothingFeedsIsRemoved()
    {
        List<PieceModel> loop = new List<PieceModel>
        {
            ChuteModels.Plain(41, At(5, 5), "+x", "+z"),
            ChuteModels.Plain(42, At(6, 5), "-x", "+z"),
            ChuteModels.Plain(43, At(6, 6), "-x", "-z"),
            ChuteModels.Plain(44, At(5, 6), "+x", "-z")
        };
        List<PieceModel> pieces = MainLine();
        pieces.AddRange(loop);

        ChuteCleanupResult result = Plan(pieces, MainPorts());

        foreach (PieceModel piece in loop)
        {
            Assert.Equal(DeadChuteReason.NoConsumer, Assert.IsType<RemovedChute>(Of(result, piece.Id)).Reason);
        }
    }

    [Fact]
    public void ARecirculatingLoopOnAPathIsLeftAlone()
    {
        // Feeder -> (0,0) -> junction (1,0) -> (2,0) -> overflow (3,0) -> (4,0) -> consumer; the overflow spills
        // back round (3,1) (2,1) (1,1) into the junction's side input.
        List<PieceModel> pieces = new List<PieceModel>
        {
            ChuteModels.Plain(1, At(0, 0), "-x", "+x"),
            ChuteModels.Junction(2, At(1, 0), "-x", "+z", "+x"),
            ChuteModels.Plain(3, At(2, 0), "-x", "+x"),
            Overflow(4, At(3, 0), "-x", "+x", "+z"),
            ChuteModels.Plain(5, At(4, 0), "-x", "+x"),
            ChuteModels.Plain(6, At(3, 1), "-z", "-x"),
            ChuteModels.Plain(7, At(2, 1), "+x", "-x"),
            ChuteModels.Plain(8, At(1, 1), "+x", "-z")
        };
        List<DevicePort> ports = new List<DevicePort>
        {
            Pushes(Feeder, At(0, 0), "-x"),
            Takes(Consumer, At(4, 0), "+x")
        };

        ChuteCleanupResult result = Plan(pieces, ports);

        Assert.All(result.Verdicts, verdict => Assert.IsType<LiveChute>(verdict));
    }

    [Fact]
    public void KeepIdsSparesADeadPieceAndWhatItWouldSpillInto()
    {
        List<PieceModel> pieces = MainLine();
        pieces.Add(ChuteModels.Plain(11, At(0, 2), "-x", "+x"));
        pieces.Add(ChuteModels.Plain(12, At(1, 2), "-x", "+x"));
        List<DevicePort> ports = MainPorts();
        ports.Add(Pushes(300, At(0, 2), "-x"));
        Dictionary<long, string> held = new Dictionary<long, string> { [11] = ChuteCleanupNames.KeepIds };

        ChuteCleanupResult result = Plan(pieces, ports, held);

        Assert.Equal(ChuteCleanupNames.KeepIds, Assert.IsType<KeptDeadChute>(Of(result, 11)).HeldBy);
        Assert.Equal(ChuteCleanupNames.WouldDropItems, Assert.IsType<KeptDeadChute>(Of(result, 12)).HeldBy);
    }

    [Fact]
    public void OnlySelectedPiecesGetAVerdict()
    {
        List<PieceModel> pieces = MainLine();
        pieces.Add(ChuteModels.Plain(20, At(5, 5), "-x", "+x"));

        ChuteCleanupResult result = ChuteCleanup.Plan(new ChuteCleanupScene(pieces, MainPorts(), new long[] { 20 },
            new Dictionary<long, string>(), new long[0], new long[0], _ => true));

        Assert.IsType<RemovedChute>(Assert.Single(result.Verdicts));
    }
}
