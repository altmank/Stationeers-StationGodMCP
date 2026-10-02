#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;
using StationGodMCP.Server;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// Fixes from round 11 of the headless live test (2026-09-29): a job that takes nothing needs no player or from_id;
/// a skipped refund target says which case it is; a from_id the request removes is refused when the refund goes into
/// it; an empty acknowledge_gas_lost is refused; the buy ledger's stock message names earlier lines only when they
/// bought from that entry; a network a lone in-line tank leaves in one piece is kept whole.
/// </summary>
public sealed class Round11FixesTests
{
    private static RefundRoute Chain(params string[] words) => RefundRoute.OfWords(words, out _)!;

    private static List<string> Skipped(RefundRoute route, RefundReach reach)
    {
        List<string> skipped = new List<string>();
        RefundChainRule.Usable(((RefundRoute.Chain)route).Targets, reach, skipped);
        return skipped;
    }

    // cables-1, pipes-structures note: clean_cables remove_dead_ends and a zero-charge replace_walls refused
    // no_local_player on a dedicated server although they take nothing and refund_to named a container.
    [Fact]
    public void OnlyAChargeOrTheSingleWordSourceNeedsAPlayer()
    {
        Assert.False(RefundChainRule.NeedsPlayer(0, Chain("268")));
        Assert.False(RefundChainRule.NeedsPlayer(0, RefundRoute.Default));
        Assert.False(RefundChainRule.NeedsPlayer(0, RefundRoute.WherePieceStood));
        Assert.False(RefundChainRule.NeedsPlayer(0, RefundRoute.Nothing));
        Assert.True(RefundChainRule.NeedsPlayer(0, RefundRoute.Holder));
        Assert.True(RefundChainRule.NeedsPlayer(1, Chain("268")));
    }

    // cables-2: storage with a from_id that has no slots (a cable piece) was kept silently and fell to the ground.
    [Fact]
    public void StorageWithAFromIdWithoutSlotsIsSkippedWithTheReason()
    {
        List<string> skipped = Skipped(Chain("storage"), new RefundReach(false, false, false, RefundFrom.Found));

        Assert.Equal("storage skipped: from_id has no slot a refund could go into, and is stored in nothing that has " +
                     "one.", Assert.Single(skipped));
    }

    // cables-4 and ps-4: the skip text said "there is no from_id" for one that does not exist, and "from_id is not a
    // stack" when no from_id was passed.
    [Fact]
    public void TheSkipTextTellsNoFromIdFromOneThatNamesNothing()
    {
        Assert.Equal("source skipped: there is no from_id to top up.",
            Assert.Single(Skipped(Chain("source"), new RefundReach(false, false, false, RefundFrom.Absent))));
        Assert.Equal(new[] { "source skipped: from_id names no thing.", "storage skipped: from_id names no thing." },
            Skipped(Chain("source", "storage"), new RefundReach(true, false, false, RefundFrom.Missing)));
        Assert.Equal("storage skipped: there is no from_id and no local player whose slots could take it.",
            Assert.Single(Skipped(Chain("storage"), new RefundReach(false, false, false, RefundFrom.Absent))));
        Assert.Equal("source skipped: from_id is not a stack to top up.",
            Assert.Single(Skipped(Chain("source"), new RefundReach(false, false, true, RefundFrom.Found))));
    }

    // cables-3: a from_id the request removes is refused when refund_to gives into it.
    [Fact]
    public void TheRefundAsksForTheHolderWhenItNamesSourceOrStorage()
    {
        Assert.True(RefundChainRule.AsksForHolder(RefundRoute.Default));
        Assert.True(RefundChainRule.AsksForHolder(RefundRoute.Holder));
        Assert.True(RefundChainRule.AsksForHolder(Chain("storage")));
        Assert.True(RefundChainRule.AsksForHolder(Chain("268", "source")));
        Assert.False(RefundChainRule.AsksForHolder(Chain("268", "ground")));
        Assert.False(RefundChainRule.AsksForHolder(Chain("inventory")));
        Assert.False(RefundChainRule.AsksForHolder(RefundRoute.WherePieceStood));
        Assert.False(RefundChainRule.AsksForHolder(RefundRoute.Nothing));
    }

    [Fact]
    public void AnEmptyAcknowledgementIsRefusedNotTakenAsLeftOut()
    {
        Assert.Null(GasHoldRule.BlankRefusal(null));
        Assert.Null(GasHoldRule.BlankRefusal("run-7"));
        Assert.Contains("acknowledge_gas_lost is empty", GasHoldRule.BlankRefusal(""));
        Assert.NotNull(GasHoldRule.BlankRefusal("  "));

        Assert.Equal("invalid_argument", Assert.Throws<ApiException>(() =>
            GasHoldArgs.Acknowledgement(new Args(JObject.Parse("{\"acknowledge_gas_lost\":\"\"}")))).Code);
        Assert.Null(GasHoldArgs.Acknowledgement(new Args(JObject.Parse("{}"))));
        Assert.Equal("run-7",
            GasHoldArgs.Acknowledgement(new Args(JObject.Parse("{\"acknowledge_gas_lost\":\"run-7\"}"))));
    }

    // ct-2: a stock refusal said "after the earlier lines" when no earlier line bought from that entry.
    [Fact]
    public void TheStockMessageNamesEarlierLinesOnlyWhenTheyBoughtFromThatEntry()
    {
        BuyLedger<string> ledger = new BuyLedger<string>(100f, 10);
        Assert.IsType<BuyVerdict.Bought>(ledger.Take("frames", 5, 3, 1f, BuyRoom.OnePerSlot));

        BuyVerdict.Refused glass = Assert.IsType<BuyVerdict.Refused>(
            ledger.Take("glass", 0, 1, 1f, BuyRoom.OnePerSlot));
        BuyVerdict.Refused frames = Assert.IsType<BuyVerdict.Refused>(
            ledger.Take("frames", 5, 3, 1f, BuyRoom.OnePerSlot));

        Assert.Equal("The trader has 0 in stock.", glass.Message);
        Assert.Equal("The trader has 2 in stock after the earlier lines of this call.", frames.Message);
    }

    // ps-1: a lone in-line tank at the end of a network leaves one part holding every mole, so the planner keeps it
    // whole (the tank leaves before it goes) and the network keeps its id: both models then agree.
    [Fact]
    public void ALoneTankAtTheEndLeavesOnePartTheKeptWholeModelMatches()
    {
        List<TakedownMember> members = new List<TakedownMember>
        {
            new TakedownMember(1318, 10.0, 60000.0), new TakedownMember(1319, 10.0, 60000.0),
            new TakedownMember(1317, 100.0, 60000.0)
        };
        List<Link> links = new List<Link>
        {
            new Link(1318, 1319), new Link(1319, 1318), new Link(1319, 1317), new Link(1317, 1319)
        };

        TakedownOutcome chain = PipeTakedown.Run(members, links, new long[] { 1317 }, false, 1100.0);
        TakedownOutcome whole = PipeTakedown.Run(members, links, new long[] { 1317 }, true, 1100.0);

        TakedownPart part = Assert.Single(chain.Parts);
        Assert.Equal(new long[] { 1318, 1319 }, part.Members);
        Assert.Equal(Assert.Single(whole.Parts).Moles, part.Moles, 6);
        Assert.Equal(0.0, chain.LostMol, 6);
    }

    [Fact]
    public void TheDescriptionsSayWhatRound11Found()
    {
        string all = ToolCatalogue.Tools.GetRawText();

        Assert.Contains("not capped by wanted", all);
        Assert.Contains("or pipe pieces alone, which remove_pipes refuses outright", all);
        Assert.Contains("an in-line tank or passive vent removed alone too", all);
        Assert.Contains("as does every poll of that job with job_id", all);
        Assert.Contains("credits_spent (signed", all);
        Assert.Contains("compile_error_line is an integer", all);
        Assert.Contains("a swap that charges nothing", all);
        Assert.Contains("refund_holder_removed) when refund_to gives into it", all);
    }
}
