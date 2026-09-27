#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// upgrade_cables and upgrade_pipes: the connection rule the preflight predicts with, and the reply shapes the tool
/// descriptions promise.
/// </summary>
public sealed class UpgradeConnectivityTests
{
    private const int Power = 1;
    private const int Data = 2;
    private const int PowerAndData = Power | Data;
    private const int Gas = 1;
    private const int Liquid = 2;

    // A one-cell piece at x with ends pointing along x: an end sits in the neighbour's cell and faces back into this
    // one, which is how the game's ends link (an end's cell holds the other piece; the other piece has an end in this
    // end's facing cell).
    private static PieceModel Straight(long id, int x, int type = PowerAndData, PipeContent? content = null) =>
        new PieceModel(id, new[] { new GridCell(x, 0, 0) },
            new[] { EndTowards(x, x + 1, type), EndTowards(x, x - 1, type) }, content);

    private static PieceEnd EndTowards(int from, int to, int type) =>
        new PieceEnd(new GridCell(to, 0, 0), new GridCell(from, 0, 0), type, 0);

    [Fact]
    public void NeighboursInARowLinkBothWays()
    {
        List<PieceModel> run = new List<PieceModel> { Straight(1, 0), Straight(2, 1), Straight(3, 2) };
        HashSet<Link> links = Connectivity.LinksTouching(run, new HashSet<long> { 2 });
        Assert.Equal(4, links.Count);
        Assert.Contains(new Link(1, 2), links);
        Assert.Contains(new Link(2, 1), links);
        Assert.Contains(new Link(2, 3), links);
        Assert.Contains(new Link(3, 2), links);
    }

    [Fact]
    public void AnIdenticalReplacementChangesNoLink()
    {
        List<PieceModel> before = new List<PieceModel> { Straight(1, 0), Straight(2, 1), Straight(3, 2) };
        PieceModel twin = new PieceModel(2, new[] { new GridCell(1, 0, 0) },
            new[] { EndTowards(1, 0, PowerAndData), EndTowards(1, 2, PowerAndData) }, null);
        List<PieceModel> after = new List<PieceModel> { before[0], twin, before[2] };
        HashSet<long> focus = new HashSet<long> { 2 };
        Assert.True(Connectivity.SameShape(before[1], twin));
        Assert.True(Connectivity.Compare(Connectivity.LinksTouching(before, focus),
            Connectivity.LinksTouching(after, focus)).Same);
    }

    [Fact]
    public void AReplacementWithAnExtraEndIsNotTheSameShapeAndAddsALink()
    {
        PieceModel side = new PieceModel(4, new[] { new GridCell(1, 1, 0) },
            new[] { new PieceEnd(new GridCell(1, 0, 0), new GridCell(1, 1, 0), PowerAndData, 0) }, null);
        List<PieceModel> before = new List<PieceModel> { Straight(1, 0), Straight(2, 1), Straight(3, 2), side };
        PieceModel tee = new PieceModel(2, new[] { new GridCell(1, 0, 0) },
            new[]
            {
                EndTowards(1, 0, PowerAndData), EndTowards(1, 2, PowerAndData),
                new PieceEnd(new GridCell(1, 1, 0), new GridCell(1, 0, 0), PowerAndData, 0)
            }, null);
        List<PieceModel> after = new List<PieceModel> { before[0], tee, before[2], side };
        HashSet<long> focus = new HashSet<long> { 2 };

        LinkDiff diff = Connectivity.Compare(Connectivity.LinksTouching(before, focus),
            Connectivity.LinksTouching(after, focus));

        Assert.False(Connectivity.SameShape(before[1], tee));
        Assert.Equal(new[] { new Link(2, 4), new Link(4, 2) }, diff.Added);
        Assert.Empty(diff.Lost);
    }

    [Fact]
    public void ARemovedEndLosesItsLinks()
    {
        List<PieceModel> before = new List<PieceModel> { Straight(1, 0), Straight(2, 1), Straight(3, 2) };
        PieceModel cap = new PieceModel(2, new[] { new GridCell(1, 0, 0) },
            new[] { EndTowards(1, 0, PowerAndData) }, null);
        List<PieceModel> after = new List<PieceModel> { before[0], cap, before[2] };
        HashSet<long> focus = new HashSet<long> { 2 };

        LinkDiff diff = Connectivity.Compare(Connectivity.LinksTouching(before, focus),
            Connectivity.LinksTouching(after, focus));

        Assert.Empty(diff.Added);
        Assert.Equal(new[] { new Link(2, 3), new Link(3, 2) }, diff.Lost);
    }

    [Fact]
    public void EndsWithoutASharedNetworkTypeDoNotLink()
    {
        List<PieceModel> run = new List<PieceModel> { Straight(1, 0, Power), Straight(2, 1, Data) };
        Assert.Empty(Connectivity.LinksTouching(run, new HashSet<long> { 1, 2 }));
    }

    [Fact]
    public void GasAndLiquidPipesNeverLinkButAnAnyContentPipeTakesBoth()
    {
        PipeContent gas = new PipeContent(Gas, false);
        PipeContent liquid = new PipeContent(Liquid, false);
        List<PieceModel> mixed = new List<PieceModel> { Straight(1, 0, 1, gas), Straight(2, 1, 1, liquid) };
        Assert.Empty(Connectivity.LinksTouching(mixed, new HashSet<long> { 1 }));

        PipeContent any = new PipeContent(3, true);
        List<PieceModel> withAny = new List<PieceModel> { Straight(1, 0, 1, gas), Straight(2, 1, 1, any) };
        HashSet<Link> links = Connectivity.LinksTouching(withAny, new HashSet<long> { 1 });
        Assert.Contains(new Link(1, 2), links);
        Assert.DoesNotContain(new Link(2, 1), links);
    }

    [Fact]
    public void SameShapeIgnoresOrderButNotContent()
    {
        PieceModel a = new PieceModel(1, new[] { new GridCell(0, 0, 0), new GridCell(1, 0, 0) },
            new[] { EndTowards(0, -1, Power), EndTowards(1, 2, Power) }, new PipeContent(Gas, false));
        PieceModel reordered = new PieceModel(9, new[] { new GridCell(1, 0, 0), new GridCell(0, 0, 0) },
            new[] { EndTowards(1, 2, Power), EndTowards(0, -1, Power) }, new PipeContent(Gas, false));
        PieceModel liquid = new PieceModel(9, a.Cells, a.Ends, new PipeContent(Liquid, false));
        PieceModel longer = new PieceModel(9, new[] { new GridCell(0, 0, 0), new GridCell(1, 0, 0),
            new GridCell(2, 0, 0) }, a.Ends, a.Content);
        Assert.True(Connectivity.SameShape(a, reordered));
        Assert.False(Connectivity.SameShape(a, liquid));
        Assert.False(Connectivity.SameShape(a, longer));
    }

    // A straight segment over `length` cells from x, as the Cable Gun lays a 3-, 5- or 10-long piece: an end at each
    // tip, none in between.
    private static PieceModel Long(long id, int x, int length)
    {
        GridCell[] cells = new GridCell[length];
        for (int index = 0; index < length; index++)
        {
            cells[index] = new GridCell(x + index, 0, 0);
        }

        return new PieceModel(id, cells,
            new[] { EndTowards(x, x - 1, PowerAndData), EndTowards(x + length - 1, x + length, PowerAndData) }, null);
    }

    [Fact]
    public void ALongStraightLinksOnlyAtItsTips()
    {
        List<PieceModel> run = new List<PieceModel> { Straight(1, 0), Long(2, 1, 3), Straight(3, 4) };
        HashSet<Link> links = Connectivity.LinksTouching(run, new HashSet<long> { 2 });
        Assert.Equal(4, links.Count);
        Assert.Contains(new Link(1, 2), links);
        Assert.Contains(new Link(2, 1), links);
        Assert.Contains(new Link(2, 3), links);
        Assert.Contains(new Link(3, 2), links);
    }

    [Fact]
    public void LongStraightTwinsNextToEachOtherChangeNoLink()
    {
        List<PieceModel> before =
            new List<PieceModel> { Straight(1, -1), Long(2, 0, 5), Long(3, 5, 3), Straight(4, 8) };
        PieceModel five = Long(2, 0, 5);
        PieceModel three = Long(3, 5, 3);
        List<PieceModel> after = new List<PieceModel> { before[0], five, three, before[3] };
        HashSet<long> focus = new HashSet<long> { 2, 3 };
        HashSet<Link> linksBefore = Connectivity.LinksTouching(before, focus);

        Assert.True(Connectivity.SameShape(before[1], five));
        Assert.True(Connectivity.SameShape(before[2], three));
        Assert.Contains(new Link(2, 3), linksBefore);
        Assert.Contains(new Link(3, 2), linksBefore);
        Assert.Equal(6, linksBefore.Count);
        Assert.True(Connectivity.Compare(linksBefore, Connectivity.LinksTouching(after, focus)).Same);
    }

    [Fact]
    public void AStraightOfAnotherLengthIsNoTwin()
    {
        Assert.False(Connectivity.SameShape(Long(2, 0, 3), Long(2, 0, 5)));
        Assert.False(Connectivity.SameShape(Long(2, 0, 3), Straight(2, 1)));
    }
}

/// <summary>Which plain cable prefabs a coil places (CablePieces).</summary>
public sealed class CablePiecesTests
{
    [Theory]
    [InlineData(false, true, 1, true)]
    [InlineData(false, false, 0, true)]
    [InlineData(true, true, 3, true)]
    [InlineData(true, true, 5, true)]
    [InlineData(true, true, 10, true)]
    [InlineData(true, true, 0, false)]
    [InlineData(true, false, 3, false)]
    public void MergingPiecesAndLongStraightSegmentsAreCoilPieces(bool blocksMerge, bool isStraight, int length,
        bool expected) =>
        Assert.Equal(expected, CablePieces.IsCoilPiece(blocksMerge, isStraight, length));
}

/// <summary>upgrade_cables and upgrade_pipes: reply shapes.</summary>
public sealed class UpgradeWireTests
{
    private static UpgradeReportView Report(List<UpgradeProblemView> problems)
    {
        UpgradeHeader header = new UpgradeHeader("upgrade_cables", "heavy", "dry_run", null, new List<string>());
        UpgradeTargetView target =
            new UpgradeTargetView("StructureCableStraightH", new RotationView(0, 90, 0), true, 1);
        UpgradePieceView piece = new UpgradePieceView(
            new ThingView(new ThingId(101), "StructureCableStraight", "Cable"), new PositionView(1.04, 2, 3),
            new RotationView(0, 90, 0), target, new ThingId(900));
        UpgradeMappingCount count =
            new UpgradeMappingCount("StructureCableStraight", "StructureCableStraightH", 3, 1, 1);
        UpgradeLists lists = new UpgradeLists(problems, new List<UpgradePieceView> { piece },
            new List<object> { new CableMappingView(count, 5000, 100000) }, new List<UpgradeSkippedView>(),
            new List<UpgradeSkippedView>());
        UpgradeResources resources = new UpgradeResources(new ThingView(new ThingId(7), "Character", "Player"),
            new List<UpgradeCoilView>
            {
                new UpgradeCoilView("ItemCableCoilHeavy", "Cable Coil (Heavy)", 3, 50,
                    new List<UpgradeStackView> { new UpgradeStackView(new ThingId(55), 50, new ThingId(7), 0) })
            },
            true, new List<UpgradeAmountView> { new UpgradeAmountView("ItemCableCoil", 3) }, new List<object>());
        UpgradeConnectivityView connectivity = new UpgradeConnectivityView(new UpgradeLinkCounts(6, 6),
            new List<UpgradeLinkView>(), new List<UpgradeLinkView>(), new List<UpgradeLinkView>(),
            new List<UpgradeMountedView>(), new List<UpgradeDeviceView>());
        return new UpgradeReportView(header, new UpgradeCounts(4, 3, 1, 0), lists, resources, connectivity);
    }

    [Fact]
    public void ADryRunWithoutProblemsIsReady()
    {
        string json = WireCheck.New(Report(new List<UpgradeProblemView>()));
        Assert.StartsWith(
            "{\"tool\":\"upgrade_cables\",\"target\":\"heavy\",\"status\":\"dry_run\",\"job_id\":null,\"ready\":true,"
            + "\"problems\":[],\"pieces_total\":4,\"to_swap\":3,\"kept\":1,\"unmatched\":0,\"pieces\":[{"
            + "\"reference_id\":\"101\",\"prefab_name\":\"StructureCableStraight\",\"position\":{\"x\":1.0,\"y\":2.0,"
            + "\"z\":3.0},\"rotation_deg\":{\"x\":0.0,\"y\":90.0,\"z\":0.0},"
            + "\"target_prefab_name\":\"StructureCableStraightH\",\"target_rotation_deg\":{\"x\":0.0,\"y\":90.0,"
            + "\"z\":0.0},\"rotation_kept\":true,\"cost\":1,\"network_id\":\"900\"}],\"pieces_listed\":1,"
            + "\"by_prefab\":[{\"prefab_name\":\"StructureCableStraight\","
            + "\"target_prefab_name\":\"StructureCableStraightH\",\"count\":3,\"cost_each\":1,\"cost_total\":3,"
            + "\"refund_each\":1,\"max_power_w_before\":5000.0,\"max_power_w_after\":100000.0}]",
            json);
        Assert.Contains(
            "\"coils\":[{\"prefab_name\":\"ItemCableCoilHeavy\",\"display_name\":\"Cable Coil (Heavy)\",\"needed\":3,"
            + "\"available\":50,\"stacks\":[{\"reference_id\":\"55\",\"quantity\":50,\"held_in\":\"7\","
            + "\"slot_index\":0}]}],\"refund_enabled\":true,\"refund\":[{\"prefab_name\":\"ItemCableCoil\","
            + "\"quantity\":3}]",
            json);
        Assert.Contains(
            "\"connectivity\":{\"links_before\":6,\"links_after_predicted\":6,\"model_matches_game\":true,"
            + "\"added\":[],\"lost\":[],\"model_differences\":[],\"mounted\":[]}",
            json);
    }

    [Fact]
    public void AReportWithNothingToSwapHasNoConnectivityRatherThanZeroLinks()
    {
        UpgradeHeader header = new UpgradeHeader("upgrade_cables", "heavy", "dry_run", null, new List<string>());
        UpgradeLists lists = new UpgradeLists(
            new List<UpgradeProblemView> { new UpgradeProblemView("nothing_to_swap", "none", null) },
            new List<UpgradePieceView>(), new List<object>(), new List<UpgradeSkippedView>(),
            new List<UpgradeSkippedView>());
        UpgradeResources resources = new UpgradeResources(null, new List<UpgradeCoilView>(), true,
            new List<UpgradeAmountView>(), new List<object>());
        string json = WireCheck.New(new UpgradeReportView(header, new UpgradeCounts(21, 0, 0, 21), lists, resources,
            null));
        Assert.Contains("\"devices\":[],\"connectivity\":null,", json);
    }

    [Fact]
    public void AProblemMakesItNotReadyAndNamesThePiece()
    {
        string json = WireCheck.New(Report(new List<UpgradeProblemView>
        {
            new UpgradeProblemView("link_added", "would connect", new ThingId(101))
        }));
        Assert.Contains(
            "\"ready\":false,\"problems\":[{\"code\":\"link_added\",\"message\":\"would connect\","
            + "\"reference_id\":\"101\"}]",
            json);
    }

    [Fact]
    public void APipeMappingCarriesRatingVolumeAndHeatExchange()
    {
        UpgradeMappingCount count = new UpgradeMappingCount("StructurePipeStraight", "StructureInsulatedPipeStraight",
            2, 1, 1);
        PipeMappingView view = new PipeMappingView(count, new PipePrefabNumbers(60795, 10, 0.1),
            new PipePrefabNumbers(60795, 10, 0));
        Assert.Equal(
            "{\"prefab_name\":\"StructurePipeStraight\",\"target_prefab_name\":\"StructureInsulatedPipeStraight\","
            + "\"count\":2,\"cost_each\":1,\"cost_total\":2,\"refund_each\":1,\"max_pressure_kpa_before\":60795.0,"
            + "\"max_pressure_kpa_after\":60795.0,\"volume_l_before\":10.0,\"volume_l_after\":10.0,"
            + "\"heat_exchange_factor_before\":0.1,\"heat_exchange_factor_after\":0.0}",
            WireCheck.New(view));
    }

    [Fact]
    public void AWaitingJobHasNoResultYet()
    {
        UpgradeJobView job = new UpgradeJobView("upgrade-1", "upgrade_pipes", "waiting",
            Report(new List<UpgradeProblemView>()), null);
        string json = WireCheck.New(job);
        Assert.StartsWith("{\"job_id\":\"upgrade-1\",\"tool\":\"upgrade_pipes\",\"status\":\"waiting\",", json);
        Assert.EndsWith(
            "\"final_check\":null,\"swapped\":[],\"swapped_count\":0,\"not_swapped\":[],\"stopped_at\":null,"
            + "\"used\":[],\"refund_delivered\":[],\"refund_error\":null,\"verification\":null,\"error\":null}",
            json);
    }

    [Fact]
    public void AStoppedJobSaysWhereAndWhetherThePieceIsIntact()
    {
        UpgradeSwapLog log = new UpgradeSwapLog();
        log.Swapped.Add(new UpgradeSwappedView(new ThingId(101), new ThingId(201), "StructureCableStraight",
            "StructureCableStraightH"));
        log.NotSwapped.Add(new ThingId(103));
        log.StoppedAt = new UpgradeStopView(new ThingId(102), new ErrorView("create_failed", "no"), true, null);
        UpgradeVerificationView verification = new UpgradeVerificationView(new List<UpgradeProblemView>(),
            new UpgradeLinkCounts(6, 6), new List<UpgradeLinkView>(), new List<UpgradeLinkView>(), new List<object>());
        UpgradeJobView job = new UpgradeJobView("upgrade-2", "upgrade_cables", "stopped",
            Report(new List<UpgradeProblemView>()), new UpgradeJobResult(null, log, verification, null));
        Assert.EndsWith(
            "\"final_check\":null,\"swapped\":[{\"old_reference_id\":\"101\",\"new_reference_id\":\"201\","
            + "\"prefab_name\":\"StructureCableStraight\",\"target_prefab_name\":\"StructureCableStraightH\"}],"
            + "\"swapped_count\":1,\"not_swapped\":[\"103\"],\"stopped_at\":{\"reference_id\":\"102\","
            + "\"error\":{\"code\":\"create_failed\",\"message\":\"no\"},\"piece_intact\":true,"
            + "\"replacement_id\":null},\"used\":[],\"refund_delivered\":[],\"refund_error\":null,"
            + "\"verification\":{\"ok\":true,"
            + "\"problems\":[],\"links_before\":6,\"links_after\":6,\"added\":[],\"lost\":[],\"networks\":[]},"
            + "\"error\":null}",
            WireCheck.New(job));
    }
}
