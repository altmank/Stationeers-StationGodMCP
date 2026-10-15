#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// move item without the game: which route a call takes, which calls act at once and which report until confirm, how
/// each route reads its options (every problem at once, missing_target when nothing is named), and the replies.
/// </summary>
public sealed class MoveItemTests
{
    private static MoveItemOptions Options(string json) =>
        MoveItemOptions.Bind(new OptionReader(MoveItemOptions.Spec, JObject.Parse(json)));

    private static VerbError? Check(string options) =>
        OptionCheck.Check(VerbSurface.Verb("move")!, JObject.Parse(options), out _);

    private static List<string> Paths(List<OptionProblem> problems) => problems.ConvertAll(problem => problem.Path);

    // ---- routes ----

    [Fact]
    public void SlotOrMovesMakeASlotMoveAndAStoreAsSourceOrToMakesAStoreMove()
    {
        List<OptionProblem> problems = new List<OptionProblem>();

        Assert.Equal("slot", ItemMoveRoutes.Of(true, null, StoreKind.Vault, problems));
        Assert.Equal("slot", ItemMoveRoutes.Of(false, null, StoreKind.None, problems));
        Assert.Equal("into_vault", ItemMoveRoutes.Of(false, null, StoreKind.Vault, problems));
        Assert.Equal("into_silo", ItemMoveRoutes.Of(false, null, StoreKind.Silo, problems));
        Assert.Equal("out_of_vault", ItemMoveRoutes.Of(false, StoreKind.Vault, StoreKind.None, problems));
        Assert.Equal("vault_to_vault", ItemMoveRoutes.Of(false, StoreKind.Vault, StoreKind.Vault, problems));
        Assert.Equal("out_of_silo", ItemMoveRoutes.Of(false, StoreKind.Silo, StoreKind.None, problems));
        Assert.Empty(problems);

        Assert.Null(ItemMoveRoutes.Of(false, StoreKind.Silo, StoreKind.Vault, problems));
        Assert.Null(ItemMoveRoutes.Of(false, StoreKind.Vault, StoreKind.Silo, problems));
        Assert.Equal(new[] { "to", "to" }, Paths(problems));
    }

    [Fact]
    public void AnOptionTheRouteDoesNotTakeIsAProblem()
    {
        MoveItemOptions options = Options("""{"id": "1", "to": "2", "prefab": "*Ore*", "reagent": "Iron"}""");
        List<OptionProblem> problems = new List<OptionProblem>();

        ItemMoveGiven.RouteProblems("slot", options, ItemMoveGiven.Of(options), problems);

        Assert.Equal(new[] { "prefab", "reagent" }, Paths(problems));
        Assert.Equal("picks things for a move into a store; a slot move takes ids", problems[0].Problem);
        Assert.Equal("is not for a slot move", problems[1].Problem);

        MoveItemOptions ground = Options("""{"to": "2", "id": "1", "allow": ["ground"]}""");
        problems.Clear();
        ItemMoveGiven.RouteProblems("into_silo", ground, ItemMoveGiven.Of(ground), problems);
        Assert.Equal("takes only in_use for a move into a silo", Assert.Single(problems).Problem);
    }

    // ---- act or report ----

    [Theory]
    [InlineData("slot", """{"id": "1", "to": "2"}""", true)]
    [InlineData("slot", """{"id": ["1", "3"], "to": "2", "slot": 4}""", true)]
    [InlineData("slot", """{"id": "1", "to": "2", "slot": 4, "force": true}""", false)]
    [InlineData("slot", """{"id": "1", "to": "2", "slot": 4, "force": true, "confirm": true}""", true)]
    [InlineData("into_vault", """{"to": "9", "prefab": "*Ore*"}""", false)]
    [InlineData("into_vault", """{"to": "9", "location": "player"}""", false)]
    [InlineData("into_vault", """{"to": "9", "id": ["5", "6"], "location": "any"}""", true)]
    [InlineData("out_of_vault", """{"source": "9", "prefab": "ItemIronIngot", "quantity": 5, "to": "player"}""", false)]
    [InlineData("out_of_vault", """{"source": "9", "reagent": "Iron", "quantity": 5, "to": "player"}""", false)]
    [InlineData("vault_to_vault", """{"source": "9", "to": "8", "items": "all"}""", false)]
    [InlineData("vault_to_vault", """{"source": "9", "to": "8", "items": [{"reagent": "Iron", "quantity": 5}]}""",
        true)]
    [InlineData("out_of_silo", """{"source": "9", "prefab": "ItemIronOre", "quantity": 5, "to": "player"}""", false)]
    [InlineData("into_vault", """{"to": "9", "items": [{"id": "5", "quantity": 2}]}""", true)]
    [InlineData("slot", """{"id": "1", "to": "2", "confirm": false}""", false)]
    [InlineData("into_vault", """{"to": "9", "id": ["5", "6"], "confirm": false}""", false)]
    [InlineData("into_vault", """{"to": "9", "items": [{"id": "5", "quantity": 2}], "confirm": false}""",
        false)]
    public void TypedIdsOrItemLinesActAndConfirmFalseOrAForceOrAFilterReports(string route, string json, bool acts)
    {
        MoveItemOptions options = Options(json);

        Assert.Equal(acts, ItemMoveGiven.Acts(route, options, ItemMoveGiven.Of(options)));
    }

    // The branch MoveItem.Slot takes for a call that lists its moves: an id list or moves[] answers rows when the call
    // acts, so the same call with confirm: false must take move item's report branch instead, planning every move and
    // applying none.
    [Fact]
    public void AListOfMovesWithConfirmFalseTakesTheReportBranchAndNotTheRowsBranch()
    {
        foreach (string json in new[]
                 {
                     """{"id": ["1", "2"], "to": "7", "confirm": false}""",
                     """{"moves": [{"id": "1", "to": "7"}], "confirm": false}"""
                 })
        {
            MoveItemOptions options = Options(json);
            SlotMoveOrders orders = SlotMoveOrders.Of(options, new List<OptionProblem>(), out _)!;
            ReportConfirm report = new ReportConfirm(true,
                ItemMoveGiven.Acts(ItemMoveRoutes.Slot, options, ItemMoveGiven.Of(options)), options.Report.Rows);

            Assert.True(options.Report.Asked);
            Assert.True(orders.AnswersRows);
            Assert.False(report.Acts);
        }
    }

    [Theory]
    [InlineData("into_vault", """{"to": "9"}""")]
    [InlineData("into_silo", """{"to": "9"}""")]
    [InlineData("into_vault", """{"to": "9", "location": "any", "category": "any"}""")]
    [InlineData("into_silo", """{"to": "9", "location": "any"}""")]
    public void ADepositWithNoIdsAndDefaultFiltersReportsUntilConfirm(string route, string json)
    {
        MoveItemOptions options = Options(json);

        Assert.False(ItemMoveGiven.Acts(route, options, ItemMoveGiven.Of(options)));
        MoveItemOptions confirmed = Options(json.Replace("{", """{"confirm": true, """));
        Assert.True(ItemMoveGiven.Acts(route, confirmed, ItemMoveGiven.Of(confirmed)));
    }

    [Theory]
    [InlineData("into_vault", """{"to": "9", "id": "5"}""")]
    [InlineData("into_silo", """{"to": "9", "id": ["5", "6"]}""")]
    [InlineData("into_silo", """{"to": "9", "items": [{"id": "5"}]}""")]
    public void ADepositByTypedIdsOrItemLinesActs(string route, string json)
    {
        MoveItemOptions options = Options(json);

        Assert.True(ItemMoveGiven.Acts(route, options, ItemMoveGiven.Of(options)));
    }

    // ---- slot moves ----

    [Fact]
    public void OneIdIsOneMoveAndAListIsRowsWithTheTopLevelOptions()
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        SlotMoveOrders one = SlotMoveOrders.Of(Options("""{"id": "1", "to": "player"}"""), problems, out _)!;
        Assert.False(one.AnswersRows);
        SlotMoveOrder move = Assert.Single(one.Orders);
        Assert.Equal(("1", "player", (int?)null, (int?)null, true, false),
            (move.Item, move.To, move.Slot, move.Quantity, move.Merge, move.AllowInUse));

        SlotMoveOrders list = SlotMoveOrders.Of(Options(
            """{"id": ["1", "2"], "to": "7", "slot": 3, "quantity": 2, "merge": false, "allow": ["in_use"]}"""),
            problems, out _)!;
        Assert.True(list.AnswersRows);
        Assert.Equal(new[] { 0, 1 }, list.Orders.ConvertAll(order => order.Index));
        Assert.Equal(("2", "7", (int?)3, (int?)2, false, true),
            (list.Orders[1].Item, list.Orders[1].To, list.Orders[1].Slot, list.Orders[1].Quantity,
                list.Orders[1].Merge, list.Orders[1].AllowInUse));
        Assert.Empty(problems);
    }

    [Fact]
    public void MovesEntriesTakeTheTopLevelToSlotMergeAndAllowWhenTheyLeaveThemOut()
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        SlotMoveOrders orders = SlotMoveOrders.Of(Options(
            """{"to": "7", "slot": "auto", "moves": [{"id": "1"}, {"id": "2", "to": "8", "slot": 0, "quantity": 5}]}"""),
            problems, out _)!;

        Assert.Empty(problems);
        Assert.True(orders.AnswersRows);
        Assert.Equal(("7", (int?)null, (int?)null), (orders.Orders[0].To, orders.Orders[0].Slot, orders.Orders[0].Quantity));
        Assert.Equal(("8", (int?)0, (int?)5), (orders.Orders[1].To, orders.Orders[1].Slot, orders.Orders[1].Quantity));
    }

    [Fact]
    public void EveryProblemWithTheSlotOptionsIsListedAtOnce()
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        Assert.Null(SlotMoveOrders.Of(Options(
            """{"id": "1", "quantity": 1.5, "force": true, "moves": [{"id": "", "to": "2", "force": true}, {"to": "3", "slot": -1}, {"id": "4"}]}"""),
            problems, out _));

        Assert.Equal(new[]
        {
            "quantity", "id", "quantity", "force", "moves[0].force", "moves[0].id", "moves[1].id", "moves[1].slot",
            "moves[2].to"
        }, Paths(problems));
        Assert.Equal("is a slot index (0 or more) or \"auto\"", problems[7].Problem);
    }

    [Fact]
    public void ForceIsOneMoveIntoASlotByIndex()
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        Assert.Null(SlotMoveOrders.Of(Options("""{"id": "1", "to": "2", "force": true}"""), problems, out _));
        Assert.Equal("needs slot as an index, not \"auto\"", Assert.Single(problems).Problem);

        problems.Clear();
        Assert.Null(SlotMoveOrders.Of(Options("""{"id": ["1", "3"], "to": "2", "slot": 0, "force": true}"""), problems,
            out _));
        Assert.Equal("is for one move by id, with slot as an index", Assert.Single(problems).Problem);

        Assert.NotNull(SlotMoveOrders.Of(Options("""{"id": "1", "to": "2", "slot": 0, "force": true}"""),
            new List<OptionProblem>(), out _));
    }

    [Fact]
    public void NoItemOrNoHolderIsMissingTarget()
    {
        Assert.Null(SlotMoveOrders.Of(Options("""{"to": "2"}"""), new List<OptionProblem>(), out VerbError? none));
        Assert.Equal("missing_target", none!.Code);
        Assert.Equal("""["id","moves"]""", none.Data!["options"]!.ToString(Formatting.None));

        Assert.Null(SlotMoveOrders.Of(Options("""{"id": "1"}"""), new List<OptionProblem>(), out VerbError? noTo));
        Assert.Equal("""["to"]""", noTo!.Data!["options"]!.ToString(Formatting.None));
    }

    // ---- deposits ----

    [Fact]
    public void ADepositNamesThingsByIdItemsOrFiltersOneWay()
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        MoveItemOptions items = Options("""{"to": "9", "items": [{"id": "10", "quantity": 5}, {"id": "11"}]}""");
        StoreDepositOrder byItems = StoreDepositOrder.Of(items, ItemMoveGiven.Of(items), problems, out _)!;
        Assert.Equal((5.0, (double?)null), (byItems.Lines![0].Quantity!.Value, byItems.Lines[1].Quantity));

        MoveItemOptions filter = Options("""{"to": "9", "prefab": "*Ore*", "near": "player", "radius": 10}""");
        Assert.True(StoreDepositOrder.Of(filter, ItemMoveGiven.Of(filter), problems, out _)!.Filtered);
        Assert.Empty(problems);

        MoveItemOptions mixed = Options("""{"to": "9", "id": "3", "items": [{"id": "10", "qty": 1}], "name": "Ore"}""");
        Assert.Null(StoreDepositOrder.Of(mixed, ItemMoveGiven.Of(mixed), problems, out _));
        Assert.Equal(new[] { "items", "id", "items[0].qty" }, Paths(problems));
    }

    [Fact]
    public void ADepositThatNamesNothingIsMissingTargetAndAllIsForVaultToVault()
    {
        MoveItemOptions none = Options("""{"to": "9", "location": "any"}""");
        Assert.Null(StoreDepositOrder.Of(none, ItemMoveGiven.Of(none), new List<OptionProblem>(), out VerbError? missing));
        Assert.Equal("missing_target", missing!.Code);

        List<OptionProblem> problems = new List<OptionProblem>();
        MoveItemOptions all = Options("""{"to": "9", "items": "all"}""");
        Assert.Null(StoreDepositOrder.Of(all, ItemMoveGiven.Of(all), problems, out _));
        Assert.Equal("items", Assert.Single(problems).Path);
    }

    [Fact]
    public void ADepositTakesAtMost256ItemsALine()
    {
        JArray items = new JArray();
        for (int index = 0; index < 257; index++)
        {
            items.Add(new JObject { ["id"] = (1000 + index).ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }

        Assert.NotNull(Check(new JObject { ["what"] = "item", ["to"] = "9", ["items"] = items }.ToString()));
    }

    // ---- out of a store ----

    [Fact]
    public void AMoveFromAVaultToItselfIsRefusedSameVault()
    {
        VerbError same = VaultTransferOrder.SameVault(Options("""{"source": "5", "to": " 5", "items": "all"}"""))!;

        Assert.Equal("same_vault", same.Code);
        Assert.Equal("source and to name the same vault; nothing to move.", same.Message);
        Assert.Null(VaultTransferOrder.SameVault(Options("""{"source": "5", "to": "6", "items": "all"}""")));
    }

    [Fact]
    public void AMoveOutOfAStoreNamesOneThingAQuantityAndAHolder()
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        StoreTakeOrder named = StoreTakeOrder.Of(Options(
            """{"source": "9", "prefab": " ItemIronIngot ", "quantity": 120, "to": "player"}"""), true, problems, out _)!;
        Assert.Equal(("ItemIronIngot", 120.0), (Assert.IsType<PrefabPattern.Whole>(named.Prefab).Name, named.Quantity));
        StoreTakeOrder hashed = StoreTakeOrder.Of(Options(
            """{"source": "9", "prefab": -42, "quantity": 1, "to": "7"}"""), false, problems, out _)!;
        Assert.Equal(-42, Assert.IsType<PrefabPattern.Hash>(hashed.Prefab).Value);
        Assert.Equal("Iron", StoreTakeOrder.Of(Options(
            """{"source": "9", "reagent": "Iron", "quantity": 5, "to": "7"}"""), true, problems, out _)!.Reagent);
        Assert.Empty(problems);

        Assert.Null(StoreTakeOrder.Of(Options("""{"source": "9", "prefab": ["*Ore*"], "reagent": "Iron", "to": "7"}"""),
            true, problems, out _));
        Assert.Equal(new[] { "prefab", "quantity" }, Paths(problems));
        Assert.Equal("is needed out of a vault, above 0: grams of ingot or a count of ore or ice", problems[1].Problem);
        problems.Clear();
        Assert.Null(StoreTakeOrder.Of(Options("""{"source": "9", "prefab": "ItemIronIngot", "reagent": "Iron", "quantity": 1, "to": "7"}"""),
            true, problems, out _));
        Assert.Equal("reagent", Assert.Single(problems).Path);
    }

    [Fact]
    public void ToHasNoDefaultOutOfAStore()
    {
        Assert.Null(StoreTakeOrder.Of(Options("""{"source": "9", "prefab": "ItemIronIngot", "quantity": 1}"""), false,
            new List<OptionProblem>(), out VerbError? missing));
        Assert.Equal("missing_target", missing!.Code);
        Assert.Equal("""["to"]""", missing.Data!["options"]!.ToString(Formatting.None));

        Assert.Null(StoreTakeOrder.Of(Options("""{"source": "9", "quantity": 1, "to": "7"}"""), false,
            new List<OptionProblem>(), out VerbError? noPrefab));
        Assert.Equal("""["prefab"]""", noPrefab!.Data!["options"]!.ToString(Formatting.None));
    }

    // ---- replies ----

    [Fact]
    public void OneMoveIsItemMovedWithRouteFirstAndTheItemsName()
    {
        ItemMovedView moved = new ItemMovedView(0, new ThingId(110045), "Iron Ore", new SlotRefView(new ThingId(57267), 2),
            new SlotRefView(new ThingId(273), 3), 100, null, new ThingId(110045));

        Assert.Equal(
            "{\"route\":\"slot\",\"index\":0,\"ok\":true,\"reference_id\":\"110045\",\"display_name\":\"Iron Ore\"," +
            "\"from\":{\"id\":\"57267\",\"slot\":2},\"to\":{\"id\":\"273\",\"slot\":3},\"quantity_moved\":100," +
            "\"merged_into\":null,\"destination_reference_id\":\"110045\",\"warning\":null}",
            ItemMoveReplies.Routed("slot", moved).ToString(Formatting.None));
    }

    [Fact]
    public void MovesAreRowsWithRouteFirstEachRefusedOneFailingAlone()
    {
        BatchBuilder batch = new BatchBuilder(2);
        batch.Succeeded(new ItemMovedView(0, new ThingId(1), "Kit (Battery Large)", null,
            new SlotRefView(new ThingId(273), 0), 1, new ThingId(5), new ThingId(5)));
        batch.Failed(new ItemNotMovedView(1, new ThingId(2), ApiErrors.Refused("stack_full", "full")));

        JObject reply = ItemMoveReplies.Routed("slot", batch.Build());

        Assert.Equal("route", ((JProperty)reply.First!).Name);
        Assert.Equal("5", (string?)reply["results"]![0]!["merged_into"]);
        Assert.Equal(("2", false, "stack_full"), ((string?)reply["results"]![1]!["reference_id"],
            (bool)reply["results"]![1]!["ok"]!, (string?)reply["results"]![1]!["error"]!["code"]));
        Assert.Equal(1, (int)reply["error_count"]!);
    }

    [Fact]
    public void ASlotMovesReportTargetMarksAMergeAndForceAsCannotUndo()
    {
        SlotRefView to = new SlotRefView(new ThingId(273), 3);

        Assert.Equal("[1, Iron Ore, 57 slot 2 -> 273 slot 3] [undo]",
            ItemMoveReplies.SlotTarget(new ThingId(1), "Iron Ore", new SlotRefView(new ThingId(57), 2), to, null, false)
                .Text(ApiJson.Serializer));
        Assert.Equal("[1, Iron Ore, world -> 273 slot 3] [cannot undo: merged into 9]",
            ItemMoveReplies.SlotTarget(new ThingId(1), "Iron Ore", null, to, new ThingId(9), false).Text(ApiJson.Serializer));
        JObject report = Replies.Tree(ItemMoveReplies.SlotReport(new[]
        {
            ItemMoveReplies.SlotTarget(new ThingId(1), "Payload", null, to, null, true)
        }, new List<ReportProblem>()));
        Assert.Equal(new[] { "route", "problems", "report_only", "targets" }, Keys(report));
        Assert.Equal("[cannot undo: force]", (string?)report["targets"]![0]!["undo"]);
    }

    [Fact]
    public void ADepositsReportListsEachThingAndEachRefusalWithoutItsRows()
    {
        BatchBuilder batch = new BatchBuilder(2);
        batch.Succeeded(new DepositedView(0, new ThingView(new ThingId(5), "ItemIronOre", "Iron Ore"), "ore",
            new SlotRefView(new ThingId(9), 1), 30, 20));
        batch.Failed(new NotDepositedView(1, new ThingId(6), "ItemKitWall", ApiErrors.Refused("not_vault_material", "no")));
        VaultDepositView view = new VaultDepositView(
            new VaultRefView(new ThingView(new ThingId(132000), "StructureIngotVault", "Ingot Vault"), null, true, true),
            2, 1, batch.Build(), new List<StockChangeView>());

        JObject report = Replies.Tree(ItemMoveReplies.DepositReport(ItemMoveReplies.Routed("into_vault", view)));

        Assert.Equal(new[] { "route", "vault", "via", "matched", "skipped", "stock", "problems",
            "report_only", "targets" }, Keys(report));
        Assert.Equal("[5, Iron Ore, 50 -> 20] [cannot undo: store move]", (string?)report["targets"]![0]!["text"]);
        Assert.Equal((1, "6", "not_vault_material"), ((int)report["problems"]![0]!["index"]!,
            (string?)report["problems"]![0]!["reference_id"], (string?)report["problems"]![0]!["code"]));
    }

    [Fact]
    public void AStoreTakeReportsTheStoreAndATransferEachLineOnTheSource()
    {
        VaultRefView vault = new VaultRefView(new ThingView(new ThingId(132000), "StructureIngotVault", "Ingot Vault"),
            null, true, true);
        VaultWithdrawView withdraw = new VaultWithdrawView(vault, new ThingView(new ThingId(273), "Human", "Player"), 5,
            new List<PlacedView>(), new List<StockChangeView>
            {
                new StockChangeView(new VaultItemView("ingot", "ItemIronIngot", "Iron Ingot", "Iron", 500), 100, -5, 95)
            });
        JObject take = Replies.Tree(ItemMoveReplies.TakeReport(ItemMoveReplies.Routed("out_of_vault", withdraw), "vault"));
        Assert.Equal("[132000, Ingot Vault (Iron Ingot), 100 -> 95] [cannot undo: store move]",
            (string?)take["targets"]![0]!["text"]);
        Assert.Equal("route", ((JProperty)take.First!).Name);

        BatchBuilder lines = new BatchBuilder(1);
        lines.Succeeded(new TransferredView(0, new VaultItemView("ore", "ItemIce", "Ice", null, 50), null, 30, false,
            new TransferSideView(30, 0), new TransferSideView(5, 35)));
        JObject transfer = Replies.Tree(ItemMoveReplies.TransferReport(ItemMoveReplies.Routed("vault_to_vault",
            new VaultTransferView(vault, vault, lines.Build(), 0))));
        Assert.Equal("[132000, Ingot Vault (Ice), 30 -> 0] [cannot undo: store move]",
            (string?)transfer["targets"]![0]!["text"]);
        Assert.Null(transfer["items"]);
    }

    private static List<string> Keys(JObject reply)
    {
        List<string> keys = new List<string>();
        foreach (JProperty key in reply.Properties())
        {
            keys.Add(key.Name);
        }

        return keys;
    }
}
