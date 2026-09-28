#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// 1.3.0's network-refactor tools on synthetic networks: which devices a split cuts off from the root, what
/// remove_redundant may take (loops through device ports, shared feeds, dead branches), taps for a run one cell short
/// of its trunk, network handles that survive id churn, the busy job slot and the created ids of a run.
/// </summary>
public sealed class SplitAnalysisTests
{
    private const long Network = 1000;
    private const long Apc = 500;
    private const long Bench = 600;
    private const long Computer = 601;
    private const long Light = 602;

    // A feed 1-2-3-4-5 on one network: the APC's output joins 1, and a shelf bench, a computer and a grow light share
    // the old feed beyond 2 (on 3, 4 and 5). Removing 2 cuts all three off the APC.
    private static NetworkEdit SharedFeed(long removed)
    {
        NetworkEdit edit = new NetworkEdit();
        for (long piece = 1; piece <= 5; piece++)
        {
            if (piece == removed)
            {
                edit.Remove(piece, Network);
                continue;
            }

            edit.AddPiece(piece, Network);
        }

        for (long piece = 1; piece < 5; piece++)
        {
            edit.Link(piece, piece + 1);
        }

        edit.Ports.Add(new ForecastPort(Apc, 1, true, Network, 1));
        edit.Ports.Add(new ForecastPort(Bench, 0, true, Network, 3));
        edit.Ports.Add(new ForecastPort(Computer, 0, true, Network, 4));
        edit.Ports.Add(new ForecastPort(Light, 0, true, Network, 5));
        return edit;
    }

    [Fact]
    public void DevicesSharingACutFeedAreAllCutOffFromTheRoot()
    {
        Forecast forecast = NetworkForecaster.Of(SharedFeed(2), out _);
        SplitDetail detail = Assert.Single(SplitAnalysis.Of(forecast, new HashSet<long> { Apc }));

        Assert.Equal(Network, detail.Network);
        Assert.Equal(new[] { Apc }, detail.Roots);
        Assert.Equal(new[] { Bench, Computer, Light }, detail.CutOff);
        Assert.Equal(2, detail.Parts.Count);
        SplitPart rooted = Assert.Single(detail.Parts, part => part.HoldsRoot);
        Assert.Equal(new[] { Apc }, rooted.Ports.ConvertAll(port => port.DeviceId));
    }

    [Fact]
    public void RemovingTheLastPieceCutsOnlyTheLight()
    {
        Forecast forecast = NetworkForecaster.Of(SharedFeed(5), out _);
        Assert.Empty(forecast.Splits);
        SplitDetail cut = Assert.Single(SplitAnalysis.Of(forecast, new HashSet<long> { Apc }));
        Assert.Null(cut.Network);
        Assert.Equal(new[] { Light }, cut.CutOff);
    }

    [Fact]
    public void WithoutARootNothingIsCalledCutOffButThePartsAreListed()
    {
        Forecast forecast = NetworkForecaster.Of(SharedFeed(2), out _);
        SplitDetail detail = Assert.Single(SplitAnalysis.Of(forecast, new HashSet<long>()));
        Assert.Null(detail.CutOff);
        Assert.Empty(detail.Roots);
        Assert.All(detail.Parts, part => Assert.False(part.HoldsRoot));
        Assert.Contains("No root is on it", SplitAnalysis.Describe(detail));
    }

    [Fact]
    public void TheWouldSplitMessageNamesTheCutOffDevices()
    {
        Forecast forecast = NetworkForecaster.Of(SharedFeed(2), out _);
        LayoutIssue issue = Assert.Single(EditGuards.Check(forecast, EditAllowance.Nothing,
            new HashSet<long> { Apc }), problem => problem.Code == EditGuards.WouldSplit);
        Assert.Contains($"Cut off from the root: {Bench}, {Computer}, {Light}.", issue.Message);
        Assert.Contains("(root)", issue.Message);
    }

    [Fact]
    public void ARootOnAnotherPartKeepsItsDevices()
    {
        // The root is the far end this time: the APC's side is what is cut off.
        Forecast forecast = NetworkForecaster.Of(SharedFeed(2), out _);
        SplitDetail detail = Assert.Single(SplitAnalysis.Of(forecast, new HashSet<long> { Light }));
        Assert.Equal(new[] { Apc }, detail.CutOff);
    }

    [Fact]
    public void TheSplitViewSerialisesComponentsRootAndCutOff()
    {
        ThingView bench = new ThingView(new ThingId(Bench), "StructureShelf", "Shelf");
        RunSplitView view = new RunSplitView(new ThingId(Network), new List<int> { 0, 1 }, new List<RunPortView>(),
            false, new RunSplitDevicesView(
                new List<RunSplitPartView>
                {
                    new RunSplitPartView(1, new List<RunPortView> { new RunPortView(bench, 0, true, new ThingId(Network)) },
                        false)
                },
                new List<ThingView> { new ThingView(new ThingId(Apc), "StructureAreaPowerControl", "APC") },
                new List<ThingView> { bench }));
        JObject json = JObject.Parse(WireCheck.New(view));
        Assert.Equal("600", (string?)json["cut_off"]![0]!["reference_id"]);
        Assert.Equal("500", (string?)json["root"]![0]!["reference_id"]);
        Assert.False((bool)json["components"]![0]!["holds_root"]!);
        Assert.Equal(1, (int)json["components"]![0]!["index"]!);

        RunSplitView noRoot = new RunSplitView(new ThingId(Network), new List<int>(), new List<RunPortView>(), false,
            new RunSplitDevicesView(new List<RunSplitPartView>(), new List<ThingView>(), null));
        Assert.Equal(JTokenType.Null, JObject.Parse(WireCheck.New(noRoot))["cut_off"]!.Type);
    }
}

public sealed class RedundantPiecesTests
{
    private const long Apc = 500;
    private const long Bench = 600;
    private const long Computer = 601;

    private static List<Link> Chain(params long[] ids)
    {
        List<Link> links = new List<Link>();
        for (int index = 1; index < ids.Length; index++)
        {
            links.Add(new Link(ids[index - 1], ids[index]));
        }

        return links;
    }

    private static RedundancyResult Find(List<long> pieces, List<Link> links, long[] devices, List<long> candidates,
        long[]? keep = null, Dictionary<long, string>? blocked = null, long[]? roots = null) =>
        RedundantPieces.Find(pieces, links, new HashSet<long>(devices), candidates, new HashSet<long>(keep ?? new long[0]),
            blocked ?? new Dictionary<long, string>(), new HashSet<long>(roots ?? new[] { Apc }));

    // Trunk 10-11-12 fed by the APC at 10. The bench's port piece 20 has an old feed 1-2-3 from trunk piece 11 and a
    // new drop 101-102 from trunk piece 12: a loop that runs through the bench's port piece. remove_loops never cuts
    // there (a piece linked to a device); remove_redundant takes the old feed, oldest first, and keeps the new drop.
    [Fact]
    public void ALoopThroughADevicePortLosesTheOldFeed()
    {
        List<long> pieces = new List<long> { 10, 11, 12, 1, 2, 3, 20, 101, 102 };
        List<Link> links = Chain(10, 11, 12);
        links.AddRange(Chain(11, 1, 2, 3, 20));
        links.AddRange(Chain(12, 101, 102, 20));
        links.Add(new Link(Apc, 10));
        links.Add(new Link(20, Bench));
        List<long> candidates = new List<long> { 1, 2, 3, 20, 101, 102 };

        RedundancyResult result = Find(pieces, links, new[] { Apc, Bench }, candidates);

        Assert.Equal(new long[] { 1, 2, 3 }, result.Removed);
        KeptPiece port = Assert.Single(result.Kept, kept => kept.Id == 20);
        Assert.Equal(RedundantPieces.DevicePort, port.Reason);
        Assert.Equal(new[] { Bench }, port.Devices);
        KeptPiece drop = Assert.Single(result.Kept, kept => kept.Id == 101);
        Assert.Equal(RedundantPieces.Needed, drop.Reason);
        Assert.Equal(new[] { Bench }, drop.Devices);
    }

    [Fact]
    public void KeepIdsHoldTheOldFeedAndTheNewDropGoesInstead()
    {
        List<long> pieces = new List<long> { 10, 11, 12, 1, 2, 3, 20, 101, 102 };
        List<Link> links = Chain(10, 11, 12);
        links.AddRange(Chain(11, 1, 2, 3, 20));
        links.AddRange(Chain(12, 101, 102, 20));
        links.Add(new Link(Apc, 10));
        links.Add(new Link(Bench, 20));

        RedundancyResult result = Find(pieces, links, new[] { Apc, Bench }, new List<long> { 1, 2, 3, 101, 102 },
            keep: new long[] { 1, 2, 3 });

        Assert.Equal(new long[] { 101, 102 }, result.Removed);
        Assert.All(result.Kept, kept => Assert.Equal(RedundantPieces.KeepIds, kept.Reason));
        Assert.Equal(3, result.Kept.Count);
    }

    // Devices sharing one feed: 1-2-3 from the APC, bench on 3, computer on 4 beyond it; nothing is redundant and
    // piece 2 is needed by both.
    [Fact]
    public void ASharedFeedIsNeededByEveryDeviceBeyondIt()
    {
        List<long> pieces = new List<long> { 1, 2, 3, 4 };
        List<Link> links = Chain(1, 2, 3, 4);
        links.Add(new Link(Apc, 1));
        links.Add(new Link(3, Bench));
        links.Add(new Link(4, Computer));

        RedundancyResult result = Find(pieces, links, new[] { Apc, Bench, Computer }, new List<long> { 2 });

        Assert.Empty(result.Removed);
        KeptPiece needed = Assert.Single(result.Kept);
        Assert.Equal(RedundantPieces.Needed, needed.Reason);
        Assert.Equal(new[] { Bench, Computer }, needed.Devices);
        Assert.Equal(2, needed.Pieces);
    }

    [Fact]
    public void ADeadBranchGoesWholeWhateverTheOrder()
    {
        // 1-2 feed the bench; 30-31-32-33 hang off 2 and reach no device. Candidates in id order put the branch's
        // inner pieces first: each is tried again once its outer neighbour has gone.
        List<long> pieces = new List<long> { 1, 2, 30, 31, 32, 33 };
        List<Link> links = Chain(1, 2);
        links.AddRange(Chain(2, 30, 31, 32, 33));
        links.Add(new Link(Apc, 1));
        links.Add(new Link(2, Bench));

        RedundancyResult result = Find(pieces, links, new[] { Apc, Bench }, new List<long> { 30, 31, 32, 33 });

        Assert.Equal(4, result.Removed.Count);
        Assert.Empty(result.Kept);
    }

    [Fact]
    public void BlockedAndPortPiecesNeverGo()
    {
        List<long> pieces = new List<long> { 1, 2, 3 };
        List<Link> links = Chain(1, 2, 3);
        links.Add(new Link(Apc, 1));

        RedundancyResult result = Find(pieces, links, new[] { Apc }, new List<long> { 1, 2, 3 },
            blocked: new Dictionary<long, string> { [3] = "device_mounted" });

        // 1 is the APC's port piece, 3 has a fuse; 2 holds 3 on the network, so it stays too.
        Assert.Empty(result.Removed);
        Assert.Equal(RedundantPieces.DevicePort, result.Kept.Find(kept => kept.Id == 1)!.Reason);
        Assert.Equal("blocked:device_mounted", result.Kept.Find(kept => kept.Id == 3)!.Reason);
        KeptPiece middle = result.Kept.Find(kept => kept.Id == 2)!;
        Assert.Equal(RedundantPieces.Needed, middle.Reason);
        Assert.Empty(middle.Devices);
        Assert.Equal(1, middle.Pieces);
    }

    [Fact]
    public void WithoutARootTheSideWithMoreDevicesIsTheNetworksOwn()
    {
        List<long> pieces = new List<long> { 1, 2, 3 };
        List<Link> links = Chain(1, 2, 3);
        links.Add(new Link(1, Bench));
        links.Add(new Link(1, Computer));
        links.Add(new Link(3, 700));

        RedundancyResult result = Find(pieces, links, new[] { Bench, Computer, 700L }, new List<long> { 2 },
            roots: new long[0]);

        Assert.Equal(new long[] { 700 }, Assert.Single(result.Kept).Devices);
    }

    [Fact]
    public void AnIsolatedCandidateGoes()
    {
        RedundancyResult result = Find(new List<long> { 1, 9 }, new List<Link> { new Link(Apc, 1) },
            new[] { Apc }, new List<long> { 9 });
        Assert.Equal(new long[] { 9 }, result.Removed);
    }
}

public sealed class TapTests
{
    private static GridCell At(int x, int y, int z) => RunModels.At(x, y, z);

    // A run along +x ending at (2,0,0); the trunk runs along z at x = 4: one free cell short.
    [Fact]
    public void ARunOneCellShortOfItsTrunkIsFoundStraightOn()
    {
        Dictionary<GridCell, long> trunk = new Dictionary<GridCell, long>
        {
            [At(4, 0, -1)] = 50, [At(4, 0, 0)] = 51, [At(4, 0, 1)] = 52
        };
        List<NearMiss> misses = Taps.FromTip(At(2, 0, 0), At(1, 0, 0), Lookup(trunk), Occupied(trunk));

        NearMiss miss = misses[0];
        Assert.Equal(1, miss.Gap);
        Assert.True(miss.Outward);
        Assert.Equal(51, miss.Piece);
        Assert.Equal(new[] { At(3, 0, 0), At(4, 0, 0) }, miss.Tap);
    }

    [Fact]
    public void AnAdjacentUnmatchedEndComesFirst()
    {
        Dictionary<GridCell, long> pieces = new Dictionary<GridCell, long>
        {
            [At(4, 0, 0)] = 51, [At(2, 0, 1)] = 60
        };
        List<NearMiss> misses = Taps.FromTip(At(2, 0, 0), At(1, 0, 0), Lookup(pieces), Occupied(pieces));
        Assert.Equal(60, misses[0].Piece);
        Assert.Equal(0, misses[0].Gap);
        Assert.Equal("+z", misses[0].Step.Name);
        Assert.Equal(51, misses[1].Piece);
    }

    [Fact]
    public void NothingBackIntoTheRunAndNothingThroughAPiece()
    {
        Dictionary<GridCell, long> interesting = new Dictionary<GridCell, long> { [At(4, 0, 0)] = 51, [At(0, 0, 0)] = 70 };
        Dictionary<GridCell, long> all = new Dictionary<GridCell, long>(interesting) { [At(3, 0, 0)] = 99 };
        List<NearMiss> misses = Taps.FromTip(At(2, 0, 0), At(1, 0, 0), Lookup(interesting), Occupied(all));
        Assert.Empty(misses);
    }

    [Fact]
    public void BesideListsRunCellsNextToTargetPieces()
    {
        Dictionary<GridCell, long> trunk = new Dictionary<GridCell, long> { [At(1, -1, 0)] = 80, [At(2, -1, 0)] = 80 };
        List<NearMiss> beside = Taps.Beside(new[] { At(0, 0, 0), At(1, 0, 0), At(2, 0, 0) }, Lookup(trunk));
        NearMiss miss = Assert.Single(beside);
        Assert.Equal(At(1, 0, 0), miss.From);
        Assert.Equal("-y", miss.Step.Name);
    }

    [Fact]
    public void BestPrefersFewerFreeCellsThenStraightOn()
    {
        NearMiss far = new NearMiss(At(0, 0, 0), GridStep.All[0], 1, 1, new List<GridCell>(), true);
        NearMiss sideways = new NearMiss(At(0, 0, 0), GridStep.All[4], 0, 2, new List<GridCell>(), false);
        NearMiss ahead = new NearMiss(At(5, 0, 0), GridStep.All[0], 0, 3, new List<GridCell>(), true);
        Assert.Same(ahead, Taps.Best(new List<List<NearMiss>>
            { new List<NearMiss> { far, sideways }, new List<NearMiss> { ahead } }));
        Assert.Null(Taps.Best(new List<List<NearMiss>>()));
    }

    [Fact]
    public void TheTapExtendsTheRightEndOfTheShape()
    {
        RunShape shape = RunShape.Of(new[] { At(0, 0, 0), At(1, 0, 0), At(2, 0, 0) },
            new List<RunBranch> { new RunBranch(new[] { At(1, 0, 2), At(1, 0, 1) }, null) }, out _)!;

        RunShape last = shape.WithTap(At(2, 0, 0), new[] { At(3, 0, 0), At(4, 0, 0) }, out string? error)!;
        Assert.Null(error);
        Assert.Equal(new[] { At(0, 0, 0), At(1, 0, 0), At(2, 0, 0), At(3, 0, 0), At(4, 0, 0) }, last.Main);
        Assert.Contains(last.Tips, tip => tip.Cell.Equals(At(4, 0, 0)));

        RunShape first = shape.WithTap(At(0, 0, 0), new[] { At(-1, 0, 0) }, out _)!;
        Assert.Equal(At(-1, 0, 0), first.Main[0]);

        RunShape branch = shape.WithTap(At(1, 0, 2), new[] { At(1, 0, 3) }, out _)!;
        Assert.Equal(new[] { At(1, 0, 3), At(1, 0, 2), At(1, 0, 1) }, branch.Branches[0].Cells);
        Assert.Equal("branch 0", branch.PartOf(At(1, 0, 3)));

        Assert.Null(shape.WithTap(At(2, 0, 0), new[] { At(4, 0, 0) }, out string? gap));
        Assert.NotNull(gap);
        Assert.Null(shape.WithTap(At(1, 0, 0), new[] { At(1, 1, 0) }, out string? notTip));
        Assert.NotNull(notTip);
    }

    [Fact]
    public void PartOfNamesRunBranchAndNothingElse()
    {
        RunShape shape = RunShape.Of(new[] { At(0, 0, 0), At(1, 0, 0) },
            new List<RunBranch> { new RunBranch(new[] { At(1, 0, 1) }, null) }, out _)!;
        Assert.Equal("run", shape.PartOf(At(0, 0, 0)));
        Assert.Equal("branch 0", shape.PartOf(At(1, 0, 1)));
        Assert.Null(shape.PartOf(At(9, 0, 0)));
    }

    private static System.Func<GridCell, long?> Lookup(Dictionary<GridCell, long> pieces) =>
        cell => pieces.TryGetValue(cell, out long id) ? id : (long?)null;

    private static System.Func<GridCell, bool> Occupied(Dictionary<GridCell, long> pieces) => pieces.ContainsKey;
}

public sealed class FaceSealTests
{
    private const long Plate = 1;
    private const long OtherPlate = 2;
    private const long Frame = 3;

    [Fact]
    public void APlateOnAFinishedFramesFaceOpensNothing()
    {
        Assert.True(FaceSeal.Sealed(new AirBlocker[0], new AirBlocker(Frame, true), null,
            new HashSet<long> { Plate }));
    }

    [Fact]
    public void TwoPlatesBackToBackBreachOnlyWhenBothGo()
    {
        AirBlocker[] face = { new AirBlocker(OtherPlate, true) };
        Assert.True(FaceSeal.Sealed(face, null, null, new HashSet<long> { Plate }));
        Assert.False(FaceSeal.Sealed(face, null, null, new HashSet<long> { Plate, OtherPlate }));
    }

    [Fact]
    public void AnUnfinishedFrameOrAWindowGapSealsNothing()
    {
        Assert.False(FaceSeal.Sealed(new[] { new AirBlocker(9, false) }, new AirBlocker(Frame, false), null,
            new HashSet<long> { Plate }));
    }

    [Fact]
    public void AFrameRemovedWithThePlateNoLongerSeals()
    {
        Assert.False(FaceSeal.Sealed(new AirBlocker[0], new AirBlocker(Frame, true), null,
            new HashSet<long> { Plate, Frame }));
    }
}

public sealed class NetworkHandleTests
{
    [Fact]
    public void AnIdReadsAsAPlainHandle()
    {
        NetworkHandle handle = NetworkHandle.Read(new JValue("135796"), "network_id");
        Assert.IsType<NetworkHandle.ById>(handle);
        Assert.Equal(135796L, handle.Id.Value);
        Assert.IsType<NetworkHandle.ById>(NetworkHandle.Read(new JValue(42), "network_id"));
    }

    [Fact]
    public void ADevicePortReadsWithItsPort()
    {
        NetworkHandle handle = NetworkHandle.Read(JObject.Parse("{\"reference_id\": \"1378\", \"port\": 1}"), "join_to");
        NetworkHandle.ByPort port = Assert.IsType<NetworkHandle.ByPort>(handle);
        Assert.Equal(1378L, port.Id.Value);
        Assert.Equal(1, port.Port);
        Assert.Null(Assert.IsType<NetworkHandle.ByPort>(
            NetworkHandle.Read(JObject.Parse("{\"reference_id\": \"7\"}"), "x")).Port);
    }

    [Theory]
    [InlineData("{\"port\": 1}")]
    [InlineData("{\"reference_id\": \"1\", \"network_id\": \"2\"}")]
    [InlineData("true")]
    [InlineData("[1]")]
    public void AnythingElseIsRefusedByName(string json)
    {
        ApiException error = Assert.Throws<ApiException>(() => NetworkHandle.Read(JToken.Parse(json), "join_to"));
        Assert.Equal(ApiErrors.InvalidArgumentCode, error.Code);
        Assert.Contains("join_to", error.Message);
    }

    // Id churn: the network's id changed after an edit; the handle named a device port, and the reply says which id
    // it stood for this time.
    [Fact]
    public void TheReplyListsWhatEachHandleResolvedTo()
    {
        ResolvedNetworks.Begin();
        NetworkHandle handle = NetworkHandle.Read(JObject.Parse("{\"reference_id\": \"1378\", \"port\": 1}"), "join_to");
        ResolvedNetworks.Record("join_to", handle, new ThingId(140001));
        ResolvedNetworks.Record("join_to", handle, new ThingId(140001));
        object result = ResolvedNetworks.Attach(new GameClockView(1f, false, 0.5f, 3), ResolvedNetworks.Take());

        JObject json = JObject.Parse(WireCheck.New(result));
        JToken entry = Assert.Single(json["resolved_networks"]!);
        Assert.Equal("join_to", (string?)entry["argument"]);
        Assert.Equal("140001", (string?)entry["network_id"]);
        Assert.Equal("1378", (string?)entry["given"]!["reference_id"]);
        Assert.Equal(1, (int)entry["given"]!["port"]!);
        Assert.Equal(1f, (float)json["game_time_s"]!);
        Assert.Null(ResolvedNetworks.Take());
    }

    [Fact]
    public void NothingResolvedLeavesTheResultAlone()
    {
        GameClockView clock = new GameClockView(1f, false, 0.5f, 3);
        ResolvedNetworks.Begin();
        Assert.Same(clock, ResolvedNetworks.Attach(clock, ResolvedNetworks.Take()));
    }
}

public sealed class JobSlotTests
{
    [Fact]
    public void JobsWaitInOrderUpToTheCapacity()
    {
        JobLine<string> line = new JobLine<string>(2);
        Assert.True(line.Add("run-2", "a"));
        Assert.True(line.Add("run-3", "b"));
        Assert.False(line.Add("run-4", "c"));
        Assert.Equal(1, line.PositionOf("run-2"));
        Assert.Equal(2, line.PositionOf("run-3"));
        Assert.Null(line.PositionOf("run-4"));

        Assert.True(line.TryTake(out string id, out string start));
        Assert.Equal("run-2", id);
        Assert.Equal("a", start);
        Assert.Equal(1, line.PositionOf("run-3"));
        Assert.Equal(new List<string> { "run-3" }, line.Clear());
        Assert.False(line.TryTake(out _, out _));
    }

    [Fact]
    public void BusyNamesTheRunningJob()
    {
        JObject json = JObject.Parse(WireCheck.New(new JobBusyView("place_cables", "run-7", 1, "Job run-7 runs.")));
        Assert.Equal("busy", (string?)json["status"]);
        Assert.Equal("run-7", (string?)json["running_job_id"]);
        Assert.Equal(1, (int)json["waiting"]!);
    }

    [Fact]
    public void QueuedHasItsOwnJobId()
    {
        JObject json = JObject.Parse(WireCheck.New(new JobQueuedView("run-8", "remove_cables", 1, "run-7", null)));
        Assert.Equal("queued", (string?)json["status"]);
        Assert.Equal("run-8", (string?)json["job_id"]);
        Assert.Equal(1, (int)json["position"]!);
        Assert.Null(json["preflight"]);
    }
}

public sealed class CreatedIdsTests
{
    [Fact]
    public void ARunLogListsWhatItBuiltByPart()
    {
        RunLogView log = new RunLogView();
        log.AddCreated("run", new ThingId(10));
        log.AddCreated("branch 0", new ThingId(11));
        log.AddCreated("run", new ThingId(12));
        log.AddCreated("joined", new ThingId(13));

        JObject json = JObject.Parse(WireCheck.New(log));
        Assert.Equal(new[] { "10", "11", "12", "13" }, json["created_ids"]!.ToObject<string[]>());
        JArray parts = (JArray)json["created_by_part"]!;
        Assert.Equal(3, parts.Count);
        Assert.Equal("run", (string?)parts[0]["part"]);
        Assert.Equal(new[] { "10", "12" }, parts[0]["ids"]!.ToObject<string[]>());
    }

    [Fact]
    public void RemoveRedundantRunsAfterLoopsAndBeforeTheStraights()
    {
        List<string>? ordered = CleanOperationSet.Parse(
            new[] { "simplify_junctions", "remove_redundant", "remove_dead_ends", "merge_straights" }, out string? error);
        Assert.Null(error);
        Assert.Equal(new List<string> { "remove_dead_ends", "remove_redundant", "merge_straights", "simplify_junctions" },
            ordered);
    }

    [Fact]
    public void ArgsWithAddsADerivedDefaultWithoutTouchingTheRequest()
    {
        JObject raw = JObject.Parse("{\"to\": {\"network_id\": \"5\"}}");
        Args args = new Args(raw);
        Args more = args.With("join_to", JToken.Parse("\"5\""));
        Assert.True(more.Has("join_to"));
        Assert.False(args.Has("join_to"));
        Assert.Null(raw["join_to"]);
        _ = JsonConvert.SerializeObject(raw);
    }
}
