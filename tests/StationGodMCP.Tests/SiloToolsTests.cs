#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Shaping;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// The silo tools without the game: their arguments, which entries a withdrawal takes (front first, pooled stacks
/// with the last one keeping the rest, whole entries never split), where it puts them, the silo's rules (capacity,
/// food, DispenseSlot) and the replies.
/// </summary>
public sealed class SiloToolsTests
{
    private static Args Of(string json) => new Args(JObject.Parse(json));

    private static ApiException Refused(System.Action call) => Assert.Throws<ApiException>(call);

    private static SiloEntryFacts Stack(double quantity, bool matches = true) => new SiloEntryFacts(matches, quantity, true);

    private static SiloEntryFacts Whole(double quantity = 1, bool matches = true) => new SiloEntryFacts(matches, quantity, false);

    private static string Describe(SiloPick.Picked pick) =>
        string.Join(" ", pick.Takes.ConvertAll(take => $"{take.Index}:{take.Quantity}/{take.Left}{(take.Whole ? "w" : "")}"));

    // ---- which entries ----

    [Fact]
    public void EntriesAreTakenFrontFirstAndTheLastKeepsTheRest()
    {
        // Three 50-stacks of iron ingot with a backpack between: 120 takes the first two whole and 20 of the third.
        List<SiloEntryFacts> entries = new List<SiloEntryFacts>
        {
            Stack(50), Whole(1, matches: false), Stack(50), Stack(50)
        };

        SiloPick.Picked pick = Assert.IsType<SiloPick.Picked>(SiloPick.Choose(entries, 120));

        Assert.Equal("0:50/0 2:50/0 3:20/30", Describe(pick));
        Assert.Equal(120, pick.Pooled);
        Assert.Equal(0, pick.Whole);
        Assert.Equal(0, pick.FirstIndex);
    }

    [Fact]
    public void APartOfTheFirstStackLeavesTheOthersAlone()
    {
        SiloPick.Picked pick = Assert.IsType<SiloPick.Picked>(SiloPick.Choose(new List<SiloEntryFacts> { Stack(50), Stack(50) }, 7));
        Assert.Equal("0:7/43", Describe(pick));
    }

    [Fact]
    public void WholeEntriesCountAllTheyHoldAndAreNeverSplit()
    {
        // Rotten-on-export food stacks of 5 (whole), then a backpack-free suit (whole, 1).
        List<SiloEntryFacts> entries = new List<SiloEntryFacts> { Whole(5), Whole(5), Whole(5) };

        SiloPick.Picked ten = Assert.IsType<SiloPick.Picked>(SiloPick.Choose(entries, 10));
        Assert.Equal("0:5/0w 1:5/0w", Describe(ten));
        Assert.Equal(2, ten.Whole);
        Assert.Equal(0, ten.Pooled);

        SiloPick.SplitsEntry split = Assert.IsType<SiloPick.SplitsEntry>(SiloPick.Choose(entries, 7));
        Assert.Equal(1, split.Index);
        Assert.Equal(5, split.Below);
        Assert.Equal(10, split.Above);

        SiloPick.SplitsEntry first = Assert.IsType<SiloPick.SplitsEntry>(SiloPick.Choose(entries, 3));
        Assert.Equal(0, first.Below);
        Assert.Equal(5, first.Above);
    }

    [Fact]
    public void TooMuchOrNothingIsRefusedWithWhatTheSiloHolds()
    {
        List<SiloEntryFacts> entries = new List<SiloEntryFacts> { Stack(50), Stack(20), Stack(10, matches: false) };

        SiloPick.NotEnough tooMuch = Assert.IsType<SiloPick.NotEnough>(SiloPick.Choose(entries, 71));
        Assert.Equal(70, tooMuch.Available);
        Assert.IsType<SiloPick.Picked>(SiloPick.Choose(entries, 70));
        Assert.IsType<SiloPick.NoneStored>(SiloPick.Choose(new List<SiloEntryFacts> { Stack(10, matches: false) }, 1));
        Assert.IsType<SiloPick.NoneStored>(SiloPick.Choose(new List<SiloEntryFacts>(), 1));
    }

    [Fact]
    public void FractionalAmountsPoolExactly()
    {
        SiloPick.Picked pick = Assert.IsType<SiloPick.Picked>(SiloPick.Choose(new List<SiloEntryFacts> { Stack(0.5), Stack(2.25) }, 1.5));
        Assert.Equal("0:0.5/0 1:1/1.25", Describe(pick));
    }

    // ---- where they go ----

    [Fact]
    public void PooledItemsTopUpThenFillSlotsAndWholeEntriesTakeTheEmptySlotsLeft()
    {
        // 70 pooled (full stack 50; a stack in the holder with room 10) and two whole entries; four empty slots.
        SiloPlacement plan = SiloPlacement.Plan(70, 50, new List<double> { 10 }, 4, 2, allowGround: false);

        Assert.True(plan.Fits);
        Assert.Equal(2, plan.WholeSlotsFrom);
        Assert.Collection(plan.Whole.Steps,
            step => Assert.Equal(0, Assert.IsType<PlacementStep.NewStack>(step).Slot),
            step => Assert.Equal(1, Assert.IsType<PlacementStep.NewStack>(step).Slot));
    }

    [Fact]
    public void AWholeEntryWithoutAnEmptySlotIsNoRoomUnlessTheGroundIsAllowed()
    {
        SiloPlacement refused = SiloPlacement.Plan(0, 1, new List<double>(), 1, 3, allowGround: false);
        Assert.False(refused.Fits);
        Assert.Equal(2, refused.Whole.Unplaced);

        SiloPlacement grounded = SiloPlacement.Plan(0, 1, new List<double>(), 1, 3, allowGround: true);
        Assert.True(grounded.Fits);
        Assert.IsType<PlacementStep.NewStack>(grounded.Whole.Steps[0]);
        Assert.IsType<PlacementStep.Ground>(grounded.Whole.Steps[1]);
        Assert.IsType<PlacementStep.Ground>(grounded.Whole.Steps[2]);
    }

    // ---- the silo's rules ----

    [Fact]
    public void ASiloHolds600EntriesAndAnImportBeingSavedCounts()
    {
        Assert.Equal(600, SiloRules.Capacity);
        Assert.Equal(10, SiloRules.Room(590, importing: false));
        Assert.Equal(9, SiloRules.Room(590, importing: true));
        Assert.Equal(0, SiloRules.Room(600, importing: false));
        Assert.Equal(0, SiloRules.Room(600, importing: true));
    }

    [Fact]
    public void FoodThatDecaysComesOutRottenButSeedsDoNot()
    {
        Assert.True(SiloRules.RotsWhenTaken(isNutrition: true, canDecay: true, isSeed: false));
        Assert.False(SiloRules.RotsWhenTaken(isNutrition: true, canDecay: true, isSeed: true));
        Assert.False(SiloRules.RotsWhenTaken(isNutrition: true, canDecay: false, isSeed: false));
        Assert.False(SiloRules.RotsWhenTaken(isNutrition: false, canDecay: true, isSeed: false));
    }

    [Fact]
    public void OnlyAnEmptyStackThatKeepsIsPooled()
    {
        Assert.True(SiloRules.Pools(countsQuantity: true, children: 0, rots: false));
        Assert.False(SiloRules.Pools(countsQuantity: true, children: 0, rots: true));
        Assert.False(SiloRules.Pools(countsQuantity: true, children: 2, rots: false));
        Assert.False(SiloRules.Pools(countsQuantity: false, children: 0, rots: false));
    }

    [Fact]
    public void ADispenseSlotIsShiftedOnlyByTakingItOrAnEntryBeforeIt()
    {
        Assert.False(SiloRules.ShiftsDispenseSlot(-1, 0));
        Assert.True(SiloRules.ShiftsDispenseSlot(4, 4));
        Assert.True(SiloRules.ShiftsDispenseSlot(4, 0));
        Assert.False(SiloRules.ShiftsDispenseSlot(4, 5));
    }

    // ---- silo_withdraw's arguments ----

    [Fact]
    public void AWithdrawalNamesExactlyOnePrefabAndAQuantity()
    {
        SiloWithdrawRequest request = SiloWithdrawRequest.Of(
            Of("""{"silo_id": "5001", "prefab_name": " ItemIronIngot ", "quantity": 120}"""), dryRun: true);
        Assert.Equal(new ThingId(5001), request.Silo);
        Assert.Equal("ItemIronIngot", request.Prefab.Name);
        Assert.Equal(120, request.Quantity);
        Assert.Null(request.To);
        Assert.Null(request.ToSlot);
        Assert.False(request.AllowGround);
        Assert.True(request.DryRun);

        Assert.Equal(ApiErrors.InvalidArgumentCode, Refused(() => SiloWithdrawRequest.Of(
            Of("""{"silo_id": "5001", "quantity": 1}"""), true)).Code);
        Assert.Equal(ApiErrors.InvalidArgumentCode, Refused(() => SiloWithdrawRequest.Of(
            Of("""{"silo_id": "5001", "prefab_name": "ItemIronIngot", "prefab_hash": 12, "quantity": 1}"""), true)).Code);
        Assert.Equal(ApiErrors.InvalidArgumentCode, Refused(() => SiloWithdrawRequest.Of(
            Of("""{"silo_id": "5001", "prefab_name": "  ", "quantity": 1}"""), true)).Code);
        Assert.Contains("quantity", Refused(() => SiloWithdrawRequest.Of(
            Of("""{"silo_id": "5001", "prefab_name": "ItemIronIngot"}"""), true)).Message);
        Assert.Equal(ApiErrors.InvalidArgumentCode, Refused(() => SiloWithdrawRequest.Of(
            Of("""{"silo_id": "5001", "prefab_name": "ItemIronIngot", "quantity": 0}"""), true)).Code);
    }

    [Fact]
    public void AWithdrawalsSlotIsAnIndexOrAuto()
    {
        Assert.Equal(3, SiloWithdrawRequest.Of(
            Of("""{"silo_id": "1", "prefab_hash": -42, "quantity": 1, "to_id": "77", "to_slot": 3, "allow_ground": true}"""),
            false).ToSlot);
        Assert.Null(SiloWithdrawRequest.Of(
            Of("""{"silo_id": "1", "prefab_hash": -42, "quantity": 1, "to_slot": "auto"}"""), false).ToSlot);
        Assert.Equal(ApiErrors.InvalidArgumentCode, Refused(() => SiloWithdrawRequest.Of(
            Of("""{"silo_id": "1", "prefab_hash": -42, "quantity": 1, "to_slot": -1}"""), false)).Code);
    }

    [Fact]
    public void APrefabMatchesByNameInAnyCaseOrByHash()
    {
        SiloPrefabChoice name = SiloWithdrawRequest.Of(Of("""{"silo_id": "1", "prefab_name": "itemironingot", "quantity": 1}"""), true).Prefab;
        Assert.True(name.Matches("ItemIronIngot", 99));
        Assert.False(name.Matches("ItemIronOre", 99));
        SiloPrefabChoice hash = SiloWithdrawRequest.Of(Of("""{"silo_id": "1", "prefab_hash": 99, "quantity": 1}"""), true).Prefab;
        Assert.True(hash.Matches("Anything", 99));
        Assert.False(hash.Matches("ItemIronIngot", 98));
        Assert.Equal("99", hash.Asked);
    }

    [Fact]
    public void BothWriteToolsAreDryRunsUntilConfirmed()
    {
        Assert.True(WriteMode.IsDryRun(Of("{}")));
        Assert.False(WriteMode.IsDryRun(Of("""{"dry_run": false, "confirm": true}""")));
        Assert.Equal("confirm_required", Refused(() => WriteMode.IsDryRun(Of("""{"dry_run": false}"""))).Code);
        Assert.Equal(ApiErrors.InvalidArgumentCode, Refused(() => WriteMode.IsDryRun(Of("""{"confirm": true}"""))).Code);
    }

    // ---- silo_deposit's arguments ----

    [Fact]
    public void ADepositNamesThingsByIdOrByAFilter()
    {
        SiloDepositForm.ById items = Assert.IsType<SiloDepositForm.ById>(SiloDepositForm.Of(
            Of("""{"items": [{"reference_id": "10", "quantity": 5}, {"reference_id": "11"}]}""")));
        Assert.Equal(2, items.Picks.Count);
        Assert.Equal(5, items.Picks[0].Quantity);
        Assert.Null(items.Picks[1].Quantity);

        SiloDepositForm.ById ids = Assert.IsType<SiloDepositForm.ById>(SiloDepositForm.Of(Of("""{"reference_ids": ["10", "11"]}""")));
        Assert.Equal(new ThingId(11), ids.Picks[1].Id);

        SiloDepositForm.ByFilter filter = Assert.IsType<SiloDepositForm.ByFilter>(SiloDepositForm.Of(Of("""{"prefab_contains": "Ore"}""")));
        Assert.Equal(ReplyDefaults.SiloDepositItems, filter.Limit);
        Assert.Equal(600, Assert.IsType<SiloDepositForm.ByFilter>(
            SiloDepositForm.Of(Of("""{"location": "player", "limit": 600}"""))).Limit);
    }

    [Fact]
    public void ADepositRefusesMixedFormsNoFilterAndPlacesThatAreNotTheWorld()
    {
        Assert.Equal(ApiErrors.InvalidArgumentCode, Refused(() => SiloDepositForm.Of(
            Of("""{"items": [{"reference_id": "10"}], "reference_ids": ["11"]}"""))).Code);
        Assert.Equal(ApiErrors.InvalidArgumentCode, Refused(() => SiloDepositForm.Of(
            Of("""{"reference_ids": ["11"], "prefab_contains": "Ore"}"""))).Code);
        Assert.Equal(ApiErrors.InvalidArgumentCode, Refused(() => SiloDepositForm.Of(Of("""{"limit": 5}"""))).Code);
        Assert.Equal(ApiErrors.InvalidArgumentCode, Refused(() => SiloDepositForm.Of(Of("""{"location": "silo"}"""))).Code);
        Assert.Equal(ApiErrors.InvalidArgumentCode, Refused(() => SiloDepositForm.Of(Of("""{"location": "machine_stock"}"""))).Code);
        Assert.Equal(ApiErrors.InvalidArgumentCode, Refused(() => SiloDepositForm.Of(
            Of("""{"location": "ground", "limit": 601}"""))).Code);
    }

    [Fact]
    public void ADepositByIdTakesAtMost256()
    {
        List<string> ids = new List<string>();
        for (int index = 0; index < 257; index++)
        {
            ids.Add($"\"{1000 + index}\"");
        }

        Assert.Equal(ApiErrors.InvalidArgumentCode, Refused(() => SiloDepositForm.Of(
            Of("{\"reference_ids\": [" + string.Join(",", ids) + "]}"))).Code);
    }

    // ---- container_contents' page of entries ----

    [Fact]
    public void ASilosEntriesArePagedTenAtATimeUpTo600()
    {
        PageRequest page = SiloEntriesPage.Of(Of("{}"));
        Assert.Equal(ReplyDefaults.SiloEntries, page.Limit);
        Assert.Equal(0, page.Offset);
        Assert.Equal(600, SiloEntriesPage.Of(Of("""{"entries_limit": 600, "entries_offset": 20}""")).Limit);
        Assert.Equal(ApiErrors.InvalidArgumentCode, Refused(() => SiloEntriesPage.Of(Of("""{"entries_limit": 0}"""))).Code);
        Assert.Equal(ApiErrors.InvalidArgumentCode, Refused(() => SiloEntriesPage.Of(Of("""{"entries_limit": 601}"""))).Code);
    }

    [Fact]
    public void AHeldBackPageSaysHowToGetTheRest()
    {
        Truncations.Begin();
        SiloEntriesPage.Note(SiloEntriesPage.Of(Of("{}")), 10, 25);
        Truncation note = Assert.Single(Truncations.Take());
        Assert.Equal("silo.entries", note.List);
        Assert.Equal(25, note.Total);
        Assert.Contains("entries_offset 10", note.More);
    }

    // ---- replies ----

    [Fact]
    public void ASilosStoreShowsAfterItsSlots()
    {
        List<SiloEntryView> entries = new List<SiloEntryView>
        {
            new SiloEntryView(0, "ItemIronIngot", "Iron Ingot", 50, 50, 0, new List<SiloContentView>(), false),
            new SiloEntryView(1, "ItemBackpack", "Backpack", 1, null, 3,
                new List<SiloContentView> { new SiloContentView("ItemIronOre", 40) }, false)
        };
        ContainerContentsView view = new ContainerContentsView(new ThingView(new ThingId(5001), "StructureSDBSilo", "SDB Silo"),
            new PositionView(1, 2, 3), 4.5, new List<SlotView>(),
            new SiloContentsView(2, true, new SiloBusyView(false, true, -1),
                Slice<SiloEntryView>.Page(entries, SiloEntriesPage.Of(Of("{}")), 2)));
        Assert.Equal(
            "{\"reference_id\":\"5001\",\"prefab_name\":\"StructureSDBSilo\",\"display_name\":\"SDB Silo\"," +
            "\"position\":{\"x\":1.0,\"y\":2.0,\"z\":3.0},\"distance_m\":4.5,\"slots\":[],\"silo\":{\"count\":2," +
            "\"capacity\":600,\"entries_known\":true,\"importing\":false,\"exporting\":true,\"dispense_slot\":-1," +
            "\"total\":2,\"offset\":0,\"entries\":[{\"index\":0,\"reference_id\":null,\"prefab_name\":\"ItemIronIngot\"," +
            "\"display_name\":\"Iron Ingot\",\"quantity\":50.0,\"max_quantity\":50.0,\"children\":0,\"contents\":[]," +
            "\"rots_when_taken\":false},{\"index\":1,\"reference_id\":null,\"prefab_name\":\"ItemBackpack\"," +
            "\"display_name\":\"Backpack\",\"quantity\":1.0,\"max_quantity\":null,\"children\":3,\"contents\":[" +
            "{\"prefab_name\":\"ItemIronOre\",\"quantity\":40.0}],\"rots_when_taken\":false}]}}",
            WireCheck.New(view));
    }

    [Fact]
    public void AnythingElseHasNoSiloKey()
    {
        ContainerContentsView view = new ContainerContentsView(new ThingView(new ThingId(7), "StructureStorageLocker", "Locker"),
            new PositionView(0, 0, 0), null, new List<SlotView>());
        Assert.DoesNotContain("silo", WireCheck.New(view));
    }

    [Fact]
    public void OnAClientTheStoreIsUnknownAndOnlyTheCountShows()
    {
        SiloContentsView view = new SiloContentsView(12, false, null,
            Slice<SiloEntryView>.Page(new List<SiloEntryView>(), SiloEntriesPage.Of(Of("{}")), 0));
        Assert.Equal(
            "{\"count\":12,\"capacity\":600,\"entries_known\":false,\"importing\":null,\"exporting\":null," +
            "\"dispense_slot\":null,\"total\":0,\"offset\":0,\"entries\":[]}",
            WireCheck.New(view));
    }

    [Fact]
    public void FindItemsListsASilosStoreLikeAnItemWithNoId()
    {
        SiloItemView view = new SiloItemView("ItemIronOre", "Iron Ore", 40, 50,
            new List<HeldInView>
            {
                new HeldInView(new ThingView(new ThingId(5001), "StructureSDBSilo", "SDB Silo"), SiloItemView.NoSlot,
                    SiloItemView.StoreSlotName)
            },
            new PositionView(1, 2, 3), 4.5, new SiloPlaceView(3, "ItemBackpack"));
        Assert.Equal(
            "{\"reference_id\":null,\"prefab_name\":\"ItemIronOre\",\"display_name\":\"Iron Ore\",\"quantity\":40.0," +
            "\"max_quantity\":50.0,\"location\":\"silo\",\"carried_by\":null,\"held_in\":[{\"reference_id\":\"5001\"," +
            "\"prefab_name\":\"StructureSDBSilo\",\"display_name\":\"SDB Silo\",\"slot_index\":-1," +
            "\"slot_name\":\"silo store\"}],\"position\":{\"x\":1.0,\"y\":2.0,\"z\":3.0},\"distance_m\":4.5," +
            "\"silo\":{\"entry\":3,\"inside\":\"ItemBackpack\",\"movable\":false}}",
            WireCheck.New(view));
    }

    [Fact]
    public void WithdrawShape()
    {
        SiloWithdrawView view = new SiloWithdrawView(false,
            new SiloRefView(new ThingView(new ThingId(5001), "StructureSDBSilo", "SDB Silo"), true, true),
            new ThingView(new ThingId(273), "Human", "Player"), 60,
            new List<SiloTakenView> { new SiloTakenView(0, 50, 0, false, 0), new SiloTakenView(2, 10, 40, false, 0) },
            new List<PlacedView>
            {
                new PlacedView(PlacedView.Merged, new SlotRefView(new ThingId(300), 2), 10, new ThingId(301)),
                new PlacedView(PlacedView.InSlot, new SlotRefView(new ThingId(300), 3), 50, new ThingId(302))
            },
            new SiloStockView("ItemIronIngot", "Iron Ingot", 150, -60, 90), new SiloCountView(4, 3));
        Assert.Equal(
            "{\"dry_run\":false,\"silo\":{\"reference_id\":\"5001\",\"prefab_name\":\"StructureSDBSilo\"," +
            "\"display_name\":\"SDB Silo\"},\"on_off\":true,\"powered\":true,\"to\":{\"reference_id\":\"273\"," +
            "\"prefab_name\":\"Human\",\"display_name\":\"Player\"},\"quantity\":60.0,\"taken\":[{\"index\":0," +
            "\"quantity\":50.0,\"left\":0.0,\"whole\":false,\"children\":0},{\"index\":2,\"quantity\":10.0," +
            "\"left\":40.0,\"whole\":false,\"children\":0}],\"placed\":[{\"where\":\"merged\",\"slot\":{\"id\":\"300\"," +
            "\"slot\":2},\"quantity\":10.0,\"reference_id\":\"301\"},{\"where\":\"slot\",\"slot\":{\"id\":\"300\"," +
            "\"slot\":3},\"quantity\":50.0,\"reference_id\":\"302\"}],\"stock\":{\"prefab_name\":\"ItemIronIngot\"," +
            "\"display_name\":\"Iron Ingot\",\"before\":150.0,\"change\":-60.0,\"after\":90.0},\"count\":{\"before\":4," +
            "\"after\":3,\"capacity\":600}}",
            WireCheck.New(view));
    }

    [Fact]
    public void DepositShape()
    {
        BatchBuilder batch = new BatchBuilder(2);
        batch.Succeeded(new SiloDepositedView(0, new ThingView(new ThingId(10), "ItemBackpack", "Backpack"),
            new SlotRefView(new ThingId(273), 1), 1, 0, 4, 7));
        batch.Failed(new NotDepositedView(1, new ThingId(11), null, ApiErrors.Refused("silo_full", "full")));
        SiloDepositView view = new SiloDepositView(true,
            new SiloRefView(new ThingView(new ThingId(5001), "StructureSDBSilo", "SDB Silo"), true, false), null, 0,
            false, batch.Build(), new SiloCountView(7, 8));
        Assert.Equal(
            "{\"dry_run\":true,\"silo\":{\"reference_id\":\"5001\",\"prefab_name\":\"StructureSDBSilo\"," +
            "\"display_name\":\"SDB Silo\"},\"on_off\":true,\"powered\":false,\"matched\":null,\"skipped\":0," +
            "\"truncated\":false,\"items\":{\"results\":[{\"index\":0,\"ok\":true,\"reference_id\":\"10\"," +
            "\"prefab_name\":\"ItemBackpack\",\"display_name\":\"Backpack\",\"from\":{\"id\":\"273\",\"slot\":1}," +
            "\"quantity\":1.0,\"left_in_source\":0.0,\"children\":4,\"entry\":7},{\"index\":1,\"ok\":false," +
            "\"reference_id\":\"11\",\"prefab_name\":null,\"error\":{\"code\":\"silo_full\",\"message\":\"full\"}}]," +
            "\"count\":2,\"success_count\":1,\"error_count\":1},\"count\":{\"before\":7,\"after\":8,\"capacity\":600}}",
            WireCheck.New(view));
    }
}
