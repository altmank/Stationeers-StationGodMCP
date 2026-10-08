#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// vault_transfer without the game: its arguments, which amounts move off the source's lines (all by default, partial
/// when more is asked than held, later entries taking what earlier ones left), both vaults' amounts around each entry,
/// the refusals, and the reply.
/// </summary>
public sealed class VaultTransferTests
{
    private const int IronOreHash = 1758427767;
    private const int IceHash = 1217489948;

    private static Args Of(string json) => new Args(JObject.Parse(json));

    private static TransferLine Iron(double grams, double target = 0) =>
        new TransferLine("ItemIronIngot", -1301215609, "Iron", "Iron", countsWhole: false, grams, target);

    private static TransferLine IronOre(double count, double target = 0) =>
        new TransferLine("ItemIronOre", IronOreHash, null, null, countsWhole: true, count, target);

    private static TransferLine Ice(double count, double target = 0) =>
        new TransferLine("ItemIce", IceHash, null, null, countsWhole: true, count, target);

    private static TransferAsk Named(string prefab, double? quantity = null) => new TransferAsk(prefab, null, null, quantity);

    private static TransferAsk Reagent(string reagent, double? quantity = null) =>
        new TransferAsk(null, null, reagent, quantity);

    private static string Describe(List<TransferOutcome> outcomes) =>
        string.Join(" ", outcomes.ConvertAll(outcome => outcome switch
        {
            TransferOutcome.Move move =>
                $"{move.Index}:L{move.Line}:{move.Quantity}{(move.Partial ? "p" : "")}" +
                $"[{move.SourceBefore}>{move.SourceAfter}|{move.TargetBefore}>{move.TargetAfter}]",
            TransferOutcome.Refusal refusal => $"{refusal.Index}:{refusal.Code}",
            _ => "?"
        }));

    // ---- what moves ----

    [Fact]
    public void WithoutItemsEveryStoredLineMovesWhole()
    {
        List<TransferLine> lines = new List<TransferLine> { Iron(1234.5, 100), IronOre(40), Ice(0) };
        Assert.Equal("0:L0:1234.5[1234.5>0|100>1334.5] 1:L1:40[40>0|0>40]",
            Describe(VaultTransferRule.Plan(lines, null)));
    }

    [Fact]
    public void AnEmptySourceMovesNothing()
    {
        Assert.Empty(VaultTransferRule.Plan(new List<TransferLine>(), null));
    }

    [Fact]
    public void PartOfALineLeavesTheRestInTheSource()
    {
        List<TransferOutcome> plan = VaultTransferRule.Plan(new List<TransferLine> { Iron(500), Ice(30, 5) },
            new List<TransferAsk> { Reagent("iron", 120.25), Named("itemice", 10) });
        Assert.Equal("0:L0:120.25[500>379.75|0>120.25] 1:L1:10[30>20|5>15]", Describe(plan));
    }

    [Fact]
    public void AskingForMoreThanTheSourceHoldsMovesWhatItHoldsAsPartial()
    {
        List<TransferOutcome> plan = VaultTransferRule.Plan(new List<TransferLine> { Ice(30) },
            new List<TransferAsk> { Named("ItemIce", 50) });
        TransferOutcome.Move move = Assert.IsType<TransferOutcome.Move>(Assert.Single(plan));
        Assert.True(move.Partial);
        Assert.Equal(50, move.Requested);
        Assert.Equal(30, move.Quantity);
        Assert.Equal(0, move.SourceAfter);
    }

    [Fact]
    public void AllOfALineIsNotPartial()
    {
        TransferOutcome.Move move = Assert.IsType<TransferOutcome.Move>(Assert.Single(
            VaultTransferRule.Plan(new List<TransferLine> { Ice(30) }, new List<TransferAsk> { Named("ItemIce", 30) })));
        Assert.False(move.Partial);
        Assert.False(Assert.IsType<TransferOutcome.Move>(Assert.Single(
            VaultTransferRule.Plan(new List<TransferLine> { Ice(30) }, new List<TransferAsk> { Named("ItemIce") }))).Partial);
    }

    [Fact]
    public void EntriesNamingOneLineTakeWhatTheEarlierOnesLeft()
    {
        List<TransferOutcome> plan = VaultTransferRule.Plan(new List<TransferLine> { IronOre(50, 7) },
            new List<TransferAsk>
            {
                Named("ItemIronOre", 20),
                new TransferAsk(null, IronOreHash, null, 40),
                Named("ItemIronOre", 1)
            });
        Assert.Equal("0:L0:20[50>30|7>27] 1:L0:30p[30>0|27>57] 2:not_enough_stock", Describe(plan));
    }

    [Fact]
    public void ALineTheSourceDoesNotHoldIsRefused()
    {
        List<TransferOutcome> plan = VaultTransferRule.Plan(new List<TransferLine> { Iron(10), IronOre(3) },
            new List<TransferAsk> { Named("ItemGoldIngot"), Reagent("Gold") });
        Assert.Equal("0:not_in_vault 1:not_in_vault", Describe(plan));
        TransferOutcome.Refusal refusal = Assert.IsType<TransferOutcome.Refusal>(plan[0]);
        Assert.Equal("ItemGoldIngot", refusal.Asked);
        Assert.Contains("ItemIronIngot, ItemIronOre", refusal.Message);
    }

    [Fact]
    public void AReagentNamesOnlyIngotLines()
    {
        // An ore line has no reagent, so "Iron" names the ingot line, never the ore.
        List<TransferOutcome> plan = VaultTransferRule.Plan(new List<TransferLine> { IronOre(3), Iron(10) },
            new List<TransferAsk> { Reagent("Iron") });
        Assert.Equal(1, Assert.IsType<TransferOutcome.Move>(Assert.Single(plan)).Line);
    }

    [Fact]
    public void PartOfAnOreIsRefusedButGramsOfIngotAreNot()
    {
        List<TransferOutcome> plan = VaultTransferRule.Plan(new List<TransferLine> { Ice(30), Iron(10) },
            new List<TransferAsk> { Named("ItemIce", 2.5), Reagent("Iron", 2.5) });
        Assert.Equal("0:invalid_argument 1:L1:2.5[10>7.5|0>2.5]", Describe(plan));
    }

    // ---- arguments ----

    [Fact]
    public void ItemsAreOptionalAndEachNamesExactlyOneLine()
    {
        Assert.Null(VaultTransferRequest.Of(Of("""{"from_vault_id": "1", "to_vault_id": "2"}"""), true).Asks);

        VaultTransferRequest request = VaultTransferRequest.Of(Of(
            """{"from_vault_id": "1", "to_vault_id": "2", "items": [{"reagent": "Iron", "quantity": 5}, {"prefab_hash": 7}]}"""),
            false);
        Assert.False(request.DryRun);
        Assert.Equal(2, request.Asks!.Count);
        Assert.Equal("Iron", request.Asks[0].Asked);
        Assert.Equal(5, request.Asks[0].Quantity);
        Assert.Equal("7", request.Asks[1].Asked);
        Assert.Null(request.Asks[1].Quantity);

        Assert.Equal("invalid_argument", Assert.Throws<ApiException>(() => VaultTransferRequest.Of(Of(
            """{"from_vault_id": "1", "to_vault_id": "2", "items": [{"reagent": "Iron", "prefab_name": "ItemIronIngot"}]}"""),
            true)).Code);
        Assert.Equal("invalid_argument", Assert.Throws<ApiException>(() => VaultTransferRequest.Of(Of(
            """{"from_vault_id": "1", "to_vault_id": "2", "items": [{"quantity": 3}]}"""), true)).Code);
    }

    [Fact]
    public void OneVaultTwiceIsRefused()
    {
        Assert.Equal("same_vault", Assert.Throws<ApiException>(() =>
            VaultTransferRequest.Of(Of("""{"from_vault_id": "5", "to_vault_id": "5"}"""), true)).Code);
    }

    // ---- reply ----

    [Fact]
    public void TransferShape()
    {
        VaultRefView from = new VaultRefView(new ThingView(new ThingId(132000), "StructureIngotVault", "Ingot Vault"),
            null, true, true);
        VaultRefView to = new VaultRefView(new ThingView(new ThingId(140000), "StructureIngotVault", "Ingot Vault"),
            new ThingView(new ThingId(140001), "StructureRemoteVault", "Remote Vault"), true, true);
        BatchBuilder batch = new BatchBuilder(2);
        batch.Succeeded(new TransferredView(0, new VaultItemView("ore", "ItemIce", "Ice", null, 50), 50, 30, true,
            new TransferSideView(30, 0), new TransferSideView(5, 35)));
        batch.Failed(new NotTransferredView(1, "Gold", new ErrorView("not_in_vault", "no")));
        VaultTransferView view = new VaultTransferView(true, from, to, batch.Build(), 1);
        Assert.Equal(
            "{\"dry_run\":true,\"from_vault\":{\"reference_id\":\"132000\",\"prefab_name\":\"StructureIngotVault\"," +
            "\"display_name\":\"Ingot Vault\"},\"from_via\":null,\"to_vault\":{\"reference_id\":\"140000\"," +
            "\"prefab_name\":\"StructureIngotVault\",\"display_name\":\"Ingot Vault\"},\"to_via\":{\"reference_id\":" +
            "\"140001\",\"prefab_name\":\"StructureRemoteVault\",\"display_name\":\"Remote Vault\"},\"items\":{\"results\":" +
            "[{\"index\":0,\"ok\":true,\"kind\":\"ore\",\"prefab_name\":\"ItemIce\",\"display_name\":\"Ice\"," +
            "\"reagent\":null,\"requested\":50.0,\"quantity\":30.0,\"partial\":true,\"from\":{\"before\":30.0," +
            "\"after\":0.0},\"to\":{\"before\":5.0,\"after\":35.0}},{\"index\":1,\"ok\":false,\"asked\":\"Gold\"," +
            "\"error\":{\"code\":\"not_in_vault\",\"message\":\"no\"}}],\"count\":2,\"success_count\":1," +
            "\"error_count\":1},\"partial_count\":1}",
            WireCheck.New(view));
    }
}
