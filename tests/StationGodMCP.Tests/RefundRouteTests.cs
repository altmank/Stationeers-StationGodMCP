#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Server;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// refund_to (LU 2026-09-29): the single words keep their meaning; a list of targets is tried in turn per item until
/// it fits; left out, the default is inventory, source, storage, ground.
/// </summary>
public sealed class RefundRouteTests
{
    private static Args Of(string json) => new Args(JObject.Parse(json));

    private static string CodeOf(System.Action action) => Assert.Throws<ApiException>(action).Code;

    private static RefundRoute Route(string json) => RefundArgs.Route(Of(json));

    private static RefundRoute Flagged(string json) => RefundArgs.RouteWithFlag(Of(json));

    [Fact]
    public void LeftOutTheRouteIsTheDefaultChain()
    {
        RefundRoute route = Route("{}");
        Assert.Same(RefundRoute.Default, route);
        Assert.Equal(new[] { "inventory", "source", "storage", "ground" }, route.Words);
        Assert.True(route.GivesBack);
        Assert.False(route.NeedsHolder);
    }

    [Fact]
    public void TheSingleWordsKeepTheirOldMeaning()
    {
        Assert.Same(RefundRoute.Holder, Route("{\"refund_to\":\"source\"}"));
        Assert.Same(RefundRoute.WherePieceStood, Route("{\"refund_to\":\" Ground \"}"));
        Assert.Same(RefundRoute.Nothing, Route("{\"refund_to\":\"none\"}"));
        Assert.True(RefundRoute.Holder.NeedsHolder);
        Assert.False(RefundRoute.Nothing.GivesBack);
    }

    [Fact]
    public void AnotherSingleWordOrAnIdIsAChainOfOne()
    {
        RefundRoute.Chain inventory = Assert.IsType<RefundRoute.Chain>(Route("{\"refund_to\":\"inventory\"}"));
        Assert.Equal(new[] { "inventory" }, inventory.Words);
        RefundRoute.Chain locker = Assert.IsType<RefundRoute.Chain>(Route("{\"refund_to\":\"2047\"}"));
        Assert.Equal(2047, Assert.IsType<RefundTarget.Container>(locker.Targets[0]).Id);
        RefundRoute.Chain number = Assert.IsType<RefundRoute.Chain>(Route("{\"refund_to\":2047}"));
        Assert.Equal("container", number.Targets[0].Kind);
    }

    [Fact]
    public void AListIsAChainInTheOrderGiven()
    {
        RefundRoute.Chain chain = Assert.IsType<RefundRoute.Chain>(
            Route("{\"refund_to\":[\"storage\",\"2047\",99,\"Inventory\",\"ground\"]}"));
        Assert.Equal(new[] { "storage", "container", "container", "inventory", "ground" },
            chain.Targets.Select(target => target.Kind));
        Assert.Equal(new[] { "storage", "2047", "99", "inventory", "ground" }, chain.Words);
        Assert.Equal("[storage, 2047, 99, inventory, ground]", chain.Name);
    }

    [Theory]
    [InlineData("{\"refund_to\":\"bin\"}")]
    [InlineData("{\"refund_to\":[]}")]
    [InlineData("{\"refund_to\":[\"none\"]}")]
    [InlineData("{\"refund_to\":[\"inventory\",\"inventory\"]}")]
    [InlineData("{\"refund_to\":[\"2047\",2047]}")]
    [InlineData("{\"refund_to\":[\"inventory\",true]}")]
    [InlineData("{\"refund_to\":[\"-5\"]}")]
    [InlineData("{\"refund_to\":{\"inventory\":1}}")]
    [InlineData("{\"refund_to\":[\"a\",\"b\",\"c\",\"d\",\"e\",\"f\",\"g\",\"h\",\"i\"]}")]
    public void ABadRouteIsAnInvalidArgument(string json) =>
        Assert.Equal(ApiErrors.InvalidArgumentCode, CodeOf(() => Route(json)));

    [Fact]
    public void TheRefundFlagStillWorksAndCannotContradictTheRoute()
    {
        Assert.Same(RefundRoute.Default, Flagged("{}"));
        Assert.Same(RefundRoute.Default, Flagged("{\"refund\":true}"));
        Assert.Same(RefundRoute.Nothing, Flagged("{\"refund\":false}"));
        Assert.Same(RefundRoute.Nothing, Flagged("{\"refund\":false,\"refund_to\":\"none\"}"));
        Assert.Same(RefundRoute.Holder, Flagged("{\"refund\":true,\"refund_to\":\"source\"}"));
        Assert.Equal(ApiErrors.InvalidArgumentCode,
            CodeOf(() => Flagged("{\"refund\":false,\"refund_to\":[\"ground\"]}")));
        Assert.Equal(ApiErrors.InvalidArgumentCode, CodeOf(() => Flagged("{\"refund\":true,\"refund_to\":\"none\"}")));
    }

    [Fact]
    public void TheRouteIsWrittenBackAsItWasGiven()
    {
        Assert.Equal("\"ground\"", RefundArgs.Wire(RefundRoute.WherePieceStood).ToString(Formatting.None));
        Assert.Equal("[\"inventory\",\"2047\"]",
            RefundArgs.Wire(Route("{\"refund_to\":[\"inventory\",2047]}")).ToString(Formatting.None));
        Assert.Equal("none", RefundArgs.View(RefundRoute.Nothing));
        Assert.Equal(new List<string> { "inventory", "source", "storage", "ground" },
            Assert.IsType<List<string>>(RefundArgs.View(RefundRoute.Default)));
        // The undo passes it on: the list parses back to the same chain.
        Assert.Equal(RefundRoute.Default.Words,
            RefundArgs.RouteOf(RefundArgs.Wire(RefundRoute.Default))!.Words);
    }

    [Fact]
    public void ATargetWithoutWhatItNeedsIsSkippedWithTheReason()
    {
        List<string> skipped = new List<string>();
        RefundRoute.Chain chain = (RefundRoute.Chain)RefundRoute.Default;
        // A dedicated server, no from_id: only the ground is left.
        List<RefundTarget> usable = RefundChainRule.Usable(chain.Targets, new RefundReach(false, false, false), skipped);
        Assert.Equal(new[] { "ground" }, usable.ConvertAll(target => target.Kind));
        Assert.Equal(3, skipped.Count);
        Assert.StartsWith("inventory skipped", skipped[0]);

        skipped.Clear();
        usable = RefundChainRule.Usable(chain.Targets, new RefundReach(true, true, true), skipped);
        Assert.Equal(4, usable.Count);
        Assert.Empty(skipped);

        // A container id is kept; the game checks it is one.
        skipped.Clear();
        usable = RefundChainRule.Usable(new List<RefundTarget> { new RefundTarget.Container(5) },
            new RefundReach(false, false, false), skipped);
        Assert.Single(usable);
    }

    [Fact]
    public void RefundedNothingReadsTheRouteOrTheFlag()
    {
        Assert.True(new JobSource(null, false, RefundRoute.Nothing, null).RefundedNothing);
        Assert.True(new JobSource(null, false, null, false).RefundedNothing);
        Assert.False(new JobSource(null, false, RefundRoute.Default, null).RefundedNothing);
        Assert.False(new JobSource(null, false, null, null).RefundedNothing);
    }
}

/// <summary>The sidecar takes refund_to in every form on every tool that refunds.</summary>
public sealed class RefundToSchemaTests
{
    private static IReadOnlyList<string> Problems(string tool, string arguments)
    {
        using JsonDocument document = JsonDocument.Parse(arguments);
        return ArgumentCheck.Problems(Program.InputSchemas[tool], document.RootElement);
    }

    [Theory]
    [InlineData("remove_structure")]
    [InlineData("undo_job")]
    [InlineData("place_cables")]
    [InlineData("place_pipes")]
    [InlineData("place_chutes")]
    [InlineData("remove_cables")]
    [InlineData("remove_pipes")]
    [InlineData("remove_chutes")]
    [InlineData("upgrade_cables")]
    [InlineData("upgrade_pipes")]
    [InlineData("clean_cables")]
    [InlineData("clean_pipes")]
    [InlineData("replace_walls")]
    [InlineData("replace_frames")]
    [InlineData("plan_removal")]
    public void EveryRefundingToolTakesEveryForm(string tool)
    {
        string job = tool == "undo_job" ? "\"job_id\":\"remove-1\"," : string.Empty;
        foreach (string refundTo in new[] { "\"none\"", "\"source\"", "2047", "[\"inventory\",\"2047\",99,\"ground\"]" })
        {
            Assert.Empty(Problems(tool, "{" + job + "\"refund_to\":" + refundTo + "}"));
        }

        Assert.NotEmpty(Problems(tool, "{" + job + "\"refund_to\":true}"));
    }
}

/// <summary>RefundLedger: a refund planned along a chain of targets, item after item.</summary>
public sealed class RefundLedgerTests
{
    private static RefundOffer Offer(long[] stacks, int[] rooms, long[] slots) =>
        new RefundOffer(stacks, rooms, slots, false);

    private static string Describe(List<RefundChainStep> steps) =>
        string.Join(" ", steps.ConvertAll(step => step.Step switch
        {
            RefundStep.Merge => $"t{step.Target}:merge{step.Key}:{step.Step.Quantity}",
            RefundStep.IntoSlot => $"t{step.Target}:slot{step.Key}:{step.Step.Quantity}",
            _ => $"t{step.Target}:ground:{step.Step.Quantity}"
        }));

    [Fact]
    public void EachTargetTakesWhatItCanBeforeTheNext()
    {
        // 25 kits (stack 10): the inventory has one stack with room 4 and one slot; storage has two slots.
        List<RefundOffer> offers = new List<RefundOffer>
        {
            Offer(new long[] { 700 }, new[] { 4 }, new long[] { 1 }),
            Offer(new long[0], new int[0], new long[] { 2, 3 }),
            RefundOffer.OnGround
        };
        Assert.Equal("t0:merge700:4 t0:slot1:10 t1:slot2:10 t1:slot3:1",
            Describe(new RefundLedger().Plan(25, 10, offers)));
    }

    [Fact]
    public void TheGroundTargetTakesTheRestInFullStacks()
    {
        List<RefundOffer> offers = new List<RefundOffer>
        {
            Offer(new long[0], new int[0], new long[] { 1 }),
            RefundOffer.OnGround,
            Offer(new long[0], new int[0], new long[] { 2 })
        };
        // The ground comes before the second slot, so the slot is never used.
        Assert.Equal("t0:slot1:10 t1:ground:10 t1:ground:5", Describe(new RefundLedger().Plan(25, 10, offers)));
    }

    [Fact]
    public void WhatFitsNoTargetGoesOnTheGroundAsTheFallback()
    {
        List<RefundOffer> offers = new List<RefundOffer> { Offer(new long[0], new int[0], new long[] { 1 }) };
        List<RefundChainStep> steps = new RefundLedger().Plan(13, 10, offers);
        Assert.Equal("t0:slot1:10 t1:ground:3", Describe(steps));
        Assert.Null(steps[1].Key);
        Assert.Equal(offers.Count, steps[1].Target);
    }

    [Fact]
    public void ASlotTwoTargetsShareIsCountedOnce()
    {
        // inventory and storage of the same player offer the same slot 1 and the same stack 700.
        RefundOffer shared = Offer(new long[] { 700 }, new[] { 5 }, new long[] { 1 });
        List<RefundOffer> offers = new List<RefundOffer> { shared, shared, RefundOffer.OnGround };
        Assert.Equal("t0:merge700:5 t0:slot1:10 t2:ground:10 t2:ground:5",
            Describe(new RefundLedger().Plan(30, 10, offers)));
    }

    [Fact]
    public void LaterItemsDoNotReuseASlotAnEarlierItemFilled()
    {
        RefundLedger ledger = new RefundLedger();
        List<RefundOffer> offers = new List<RefundOffer>
        {
            Offer(new long[0], new int[0], new long[] { 1, 2 }), RefundOffer.OnGround
        };
        Assert.Equal("t0:slot1:1", Describe(ledger.Plan(1, 1, offers)));
        Assert.Equal("t0:slot2:4 t1:ground:2", Describe(ledger.Plan(6, 4, offers)));
    }

    [Fact]
    public void AnItemThatDoesNotStackNeverMerges()
    {
        List<RefundOffer> offers = new List<RefundOffer>
        {
            Offer(new long[] { 700 }, new[] { 9 }, new long[] { 1 }), RefundOffer.OnGround
        };
        Assert.Equal("t0:slot1:1 t1:ground:1 t1:ground:1", Describe(new RefundLedger().Plan(3, 1, offers)));
    }

    [Fact]
    public void EveryItemIsAccountedForOnce()
    {
        List<RefundOffer> offers = new List<RefundOffer>
        {
            Offer(new long[] { 700, 701 }, new[] { 3, 8 }, new long[] { 1, 2 }),
            Offer(new long[] { 701 }, new[] { 8 }, new long[] { 2, 3 })
        };
        int total = 0;
        new RefundLedger().Plan(57, 10, offers).ForEach(step => total += step.Step.Quantity);
        Assert.Equal(57, total);
    }

    [Fact]
    public void ThePlanViewCountsWhatWentToTheGroundAsTheFallback()
    {
        RefundPlanView view = new RefundPlanView(new List<string> { "inventory" }, new List<string>(),
            new List<RefundDestinationView>
            {
                new RefundDestinationView("ItemKitWall", 10, "inventory", "slot", new ThingId(7), false),
                new RefundDestinationView("ItemKitWall", 3, "ground", "ground", null, true)
            });
        Assert.Equal(3, view.OnGroundAsFallback);
        string json = JsonConvert.SerializeObject(view, ApiJson.Settings);
        Assert.Contains("\"refund_to\":[\"inventory\"]", json);
        Assert.Contains("\"target\":\"inventory\",\"where\":\"slot\",\"into\":\"7\"}", json);
        Assert.Contains("\"target\":\"ground\",\"where\":\"ground\",\"fallback\":true}", json);
        Assert.Contains("\"on_ground_as_fallback\":3", json);
    }
}
