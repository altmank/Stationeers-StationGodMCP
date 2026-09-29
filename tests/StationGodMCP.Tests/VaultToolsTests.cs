#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// vault_withdraw's placement (StackPlacement): matching stacks first, then empty slots, the ground only when allowed,
/// and what nothing takes reported rather than dropped. And the vault tools' reply shapes.
/// </summary>
public sealed class VaultToolsTests
{
    private static string Describe(StackPlan plan) =>
        string.Join(" ", plan.Steps.ConvertAll(step => step switch
        {
            PlacementStep.TopUp topUp => $"top{topUp.Stack}:{step.Quantity}",
            PlacementStep.NewStack newStack => $"slot{newStack.Slot}:{step.Quantity}",
            _ => $"ground:{step.Quantity}"
        }));

    [Fact]
    public void StacksAreToppedUpBeforeNewStacksAreMade()
    {
        // 120 g of iron ingot, a 450 g stack in the backpack (room 50), full stack 500, two empty slots.
        StackPlan plan = StackPlacement.Plan(120, 500, new List<double> { 50 }, 2, allowGround: false);
        Assert.Equal("top0:50 slot0:70", Describe(plan));
        Assert.True(plan.Fits);
        Assert.Equal(120, plan.Placed);
    }

    [Fact]
    public void NewStacksNeverPassAFullStack()
    {
        StackPlan plan = StackPlacement.Plan(120, 50, new List<double>(), 3, allowGround: false);
        Assert.Equal("slot0:50 slot1:50 slot2:20", Describe(plan));
    }

    [Fact]
    public void WhatDoesNotFitIsReportedNotDroppedUnlessTheGroundIsAllowed()
    {
        StackPlan refused = StackPlacement.Plan(120, 50, new List<double> { 10 }, 1, allowGround: false);
        Assert.False(refused.Fits);
        Assert.Equal(60, refused.Unplaced);
        Assert.Equal("top0:10 slot0:50", Describe(refused));

        StackPlan grounded = StackPlacement.Plan(120, 50, new List<double> { 10 }, 1, allowGround: true);
        Assert.True(grounded.Fits);
        Assert.Equal("top0:10 slot0:50 ground:50 ground:10", Describe(grounded));
    }

    [Fact]
    public void FractionalGramsStayWhole()
    {
        StackPlan plan = StackPlacement.Plan(12.5, 500, new List<double> { 0.25 }, 1, allowGround: false);
        Assert.Equal("top0:0.25 slot0:12.25", Describe(plan));
        Assert.Equal(12.5, plan.Placed, 9);
    }

    [Fact]
    public void FullStacksWithNoRoomAreSkipped()
    {
        StackPlan plan = StackPlacement.Plan(5, 50, new List<double> { 0, 3 }, 0, allowGround: false);
        Assert.Equal("top1:3", Describe(plan));
        Assert.Equal(2, plan.Unplaced);
    }

    [Fact]
    public void ContentsShape()
    {
        VaultItemView iron = new VaultItemView("ingot", "ItemIronIngot", "Iron Ingot", "Iron", 500);
        VaultItemView ore = new VaultItemView("ore", "ItemIronOre", "Iron Ore", null, 50);
        VaultContentsView view = new VaultContentsView(
            new List<VaultContentsEntryView>
            {
                new VaultContentsEntryView(new ThingView(new ThingId(132000), "StructureIngotVault", "Ingot Vault"),
                    new PositionView(1.04, 2, 3), true, true,
                    new List<VaultStockView> { new VaultStockView(iron, 1234.5000004), new VaultStockView(ore, 7) }, 0)
            },
            new List<RemoteVaultView>
            {
                new RemoteVaultView(new ThingView(new ThingId(132001), "StructureRemoteVault", "Remote Vault"),
                    new ThingId(132000), "None")
            });
        Assert.Equal(
            "{\"vaults\":[{\"vault\":{\"reference_id\":\"132000\",\"prefab_name\":\"StructureIngotVault\"," +
            "\"display_name\":\"Ingot Vault\"},\"position\":{\"x\":1.0,\"y\":2.0,\"z\":3.0},\"on_off\":true," +
            "\"powered\":true,\"stock\":[{\"kind\":\"ingot\",\"prefab_name\":\"ItemIronIngot\"," +
            "\"display_name\":\"Iron Ingot\",\"reagent\":\"Iron\",\"max_stack\":500.0,\"quantity\":1234.5}," +
            "{\"kind\":\"ore\",\"prefab_name\":\"ItemIronOre\",\"display_name\":\"Iron Ore\",\"reagent\":null," +
            "\"max_stack\":50.0,\"quantity\":7.0}],\"pending_vends\":0}]," +
            "\"remote_vaults\":[{\"remote\":{\"reference_id\":\"132001\",\"prefab_name\":\"StructureRemoteVault\"," +
            "\"display_name\":\"Remote Vault\"},\"vault_id\":\"132000\",\"connection\":\"None\"}],\"count\":1}",
            WireCheck.New(view));
    }

    [Fact]
    public void DepositShape()
    {
        VaultRefView vault = new VaultRefView(new ThingView(new ThingId(132000), "StructureIngotVault", "Ingot Vault"),
            null, true, true);
        BatchBuilder batch = new BatchBuilder(2);
        batch.Succeeded(new DepositedView(0, new ThingView(new ThingId(5), "ItemIronOre", "Iron Ore"), "ore",
            new SlotRefView(new ThingId(9), 1), 30, 20));
        batch.Failed(new NotDepositedView(1, new ThingId(6), "ItemKitWall",
            ApiErrors.Refused("not_vault_material", "no")));
        VaultDepositView view = new VaultDepositView(true, vault, 2, 1, false, batch.Build(),
            new List<StockChangeView>
            {
                new StockChangeView(new VaultItemView("ore", "ItemIronOre", "Iron Ore", null, 50), 10, 30, 40)
            });
        Assert.Equal(
            "{\"dry_run\":true,\"vault\":{\"reference_id\":\"132000\",\"prefab_name\":\"StructureIngotVault\"," +
            "\"display_name\":\"Ingot Vault\"},\"via\":null,\"matched\":2,\"skipped\":1,\"truncated\":false," +
            "\"items\":{\"results\":[{\"index\":0,\"ok\":true,\"reference_id\":\"5\",\"prefab_name\":\"ItemIronOre\"," +
            "\"display_name\":\"Iron Ore\",\"kind\":\"ore\",\"from\":{\"id\":\"9\",\"slot\":1},\"quantity\":30.0," +
            "\"left_in_source\":20.0},{\"index\":1,\"ok\":false,\"reference_id\":\"6\",\"prefab_name\":\"ItemKitWall\"," +
            "\"error\":{\"code\":\"not_vault_material\",\"message\":\"no\"}}],\"count\":2,\"success_count\":1," +
            "\"error_count\":1},\"stock\":[{\"kind\":\"ore\",\"prefab_name\":\"ItemIronOre\",\"display_name\":\"Iron Ore\"," +
            "\"reagent\":null,\"before\":10.0,\"change\":30.0,\"after\":40.0}]}",
            WireCheck.New(view));
    }

    [Fact]
    public void WithdrawShape()
    {
        VaultRefView vault = new VaultRefView(new ThingView(new ThingId(132000), "StructureIngotVault", "Ingot Vault"),
            new ThingView(new ThingId(132001), "StructureRemoteVault", "Remote Vault"), true, true);
        VaultWithdrawView view = new VaultWithdrawView(false, vault, new ThingView(new ThingId(273), "Human", "Player"), 5,
            new List<PlacedView>
            {
                new PlacedView(PlacedView.Merged, new SlotRefView(new ThingId(300), 2), 3, new ThingId(301)),
                new PlacedView(PlacedView.InSlot, new SlotRefView(new ThingId(300), 3), 2, new ThingId(302))
            },
            new List<StockChangeView>
            {
                new StockChangeView(new VaultItemView("ingot", "ItemIronIngot", "Iron Ingot", "Iron", 500), 100, -5, 95)
            });
        Assert.Equal(
            "{\"dry_run\":false,\"vault\":{\"reference_id\":\"132000\",\"prefab_name\":\"StructureIngotVault\"," +
            "\"display_name\":\"Ingot Vault\"},\"via\":{\"reference_id\":\"132001\"," +
            "\"prefab_name\":\"StructureRemoteVault\",\"display_name\":\"Remote Vault\"},\"to\":{\"reference_id\":\"273\"," +
            "\"prefab_name\":\"Human\",\"display_name\":\"Player\"},\"quantity\":5.0,\"placed\":[{\"where\":\"merged\"," +
            "\"slot\":{\"id\":\"300\",\"slot\":2},\"quantity\":3.0,\"reference_id\":\"301\"},{\"where\":\"slot\"," +
            "\"slot\":{\"id\":\"300\",\"slot\":3},\"quantity\":2.0,\"reference_id\":\"302\"}],\"stock\":[{\"kind\":\"ingot\"," +
            "\"prefab_name\":\"ItemIronIngot\",\"display_name\":\"Iron Ingot\",\"reagent\":\"Iron\",\"before\":100.0," +
            "\"change\":-5.0,\"after\":95.0}]}",
            WireCheck.New(view));
    }
}
