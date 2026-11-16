#nullable enable

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// A slot named by its key: the closed request (auto, index or key), the key lookup (found, missing with the keys
/// there are, ambiguous with the indexes), the build item list planned as one (fits, or no room at one line with
/// nothing planned), the option check of the two forms, and the rows that show the keys and the worn items.
/// </summary>
public sealed class SlotKeyTests
{
    private static SlotRequest? Parse(JToken? slot, out List<OptionProblem> problems)
    {
        problems = new List<OptionProblem>();
        return SlotRequest.Parse(slot, "slot", problems);
    }

    [Fact]
    public void ASlotOptionIsAutoAnIndexOrAKeyNeverTwoAtOnce()
    {
        Assert.Equal(new SlotRequest.Auto(), Parse(null, out _));
        Assert.Equal(new SlotRequest.Auto(), Parse(new JValue("auto"), out _));
        Assert.Equal(new SlotRequest.Auto(), Parse(new JValue(" AUTO "), out _));
        Assert.Equal(new SlotRequest.Index(3), Parse(new JValue(3), out _));
        Assert.Equal(new SlotRequest.Key("SharedPower1"), Parse(new JValue("SharedPower1"), out _));
        Assert.Equal(new SlotRequest.Key("right_hand"), Parse(new JValue("right_hand"), out _));

        foreach (JToken bad in new JToken[] { new JValue(-1), new JValue(1.5), new JValue(""), new JValue(true), new JArray() })
        {
            Assert.Null(Parse(bad, out List<OptionProblem> problems));
            Assert.Equal("slot", Assert.Single(problems).Path);
        }
    }

    private static readonly string?[] Suit =
    {
        "SharedPower1", "SharedPower2", null, "", "ArmorStorage1", "ArmorStorage2"
    };

    [Fact]
    public void AKeyFindsTheOneSlotThatCarriesItExactly()
    {
        Assert.Equal(new KeyLookup.Found(1), KeyLookup.Of(Suit, "SharedPower2"));
        Assert.Equal(new KeyLookup.Found(4), KeyLookup.Of(Suit, "ArmorStorage1"));
        Assert.IsType<KeyLookup.Missing>(KeyLookup.Of(Suit, "sharedpower1"));
    }

    [Fact]
    public void AMissingKeyListsTheKeysThereAre()
    {
        KeyLookup.Missing missing = Assert.IsType<KeyLookup.Missing>(KeyLookup.Of(Suit, "Nope"));
        Assert.Equal(new[] { "SharedPower1", "SharedPower2", "ArmorStorage1", "ArmorStorage2" }, missing.Keys);

        Assert.Empty(Assert.IsType<KeyLookup.Missing>(KeyLookup.Of(new string?[] { null, "" }, "Nope")).Keys);
    }

    [Fact]
    public void ASharedKeyIsAmbiguousAndListsEveryIndex()
    {
        string?[] shared = { "Power", "Other", "Power", "Power" };
        KeyLookup.Ambiguous ambiguous = Assert.IsType<KeyLookup.Ambiguous>(KeyLookup.Of(shared, "Power"));
        Assert.Equal(new[] { 0, 2, 3 }, ambiguous.Indexes);
    }

    [Fact]
    public void AnEmptyKeyIsNeverMatchedByAnEmptyKey()
    {
        KeyLookup.Missing missing = Assert.IsType<KeyLookup.Missing>(KeyLookup.Of(Suit, ""));
        Assert.DoesNotContain("", missing.Keys);
        Assert.IsType<KeyLookup.Missing>(KeyLookup.Of(new string?[] { "", "" }, ""));
    }

    [Fact]
    public void APlayersHandsAreNamedLeftAndRightWhateverTheirOwnKeys()
    {
        string?[] body = { "", "Back", null, "Belt" };
        IReadOnlyList<string?> named = KeyLookup.WithHands(body, leftHand: 2, rightHand: 0);

        Assert.Equal(new KeyLookup.Found(0), KeyLookup.Of(named, "right_hand"));
        Assert.Equal(new KeyLookup.Found(2), KeyLookup.Of(named, "left_hand"));
        Assert.Equal(new KeyLookup.Found(1), KeyLookup.Of(named, "Back"));
        Assert.IsType<KeyLookup.Missing>(KeyLookup.Of(body, "right_hand"));
    }

    private static LineSurvey Line(int[] stacks, double[] rooms, int[] empties, double quantity, double full = 1.0) =>
        new LineSurvey(stacks, rooms, empties, quantity, full);

    [Fact]
    public void ALineTakesTheSlotsTheLinesBeforeItLeft()
    {
        ItemListPlan plan = ItemListPlanner.Plan(new[]
        {
            Line(new int[0], new double[0], new[] { 10, 11, 12 }, 2.0),
            Line(new int[0], new double[0], new[] { 10, 11, 12 }, 1.0)
        });

        ItemListPlan.Fits fits = Assert.IsType<ItemListPlan.Fits>(plan);
        Assert.Equal(new[] { 0, 1 }, fits.Lines[0].Steps.OfType<PlacementStep.NewStack>().Select(step => step.Slot));
        Assert.Equal(new[] { 2 }, fits.Lines[1].Steps.OfType<PlacementStep.NewStack>().Select(step => step.Slot));
    }

    [Fact]
    public void AListThatDoesNotFitNamesTheLineAndPlansNothing()
    {
        ItemListPlan plan = ItemListPlanner.Plan(new[]
        {
            Line(new int[0], new double[0], new[] { 10, 11 }, 1.0),
            Line(new int[0], new double[0], new[] { 10, 11 }, 1.0),
            Line(new int[0], new double[0], new[] { 10, 11 }, 1.0)
        });

        ItemListPlan.NoRoom noRoom = Assert.IsType<ItemListPlan.NoRoom>(plan);
        Assert.Equal(2, noRoom.Line);
        Assert.Equal(1.0, noRoom.Unplaced);
    }

    [Fact]
    public void ALineThatNamesAFilledSlotHasNoRoomLeft()
    {
        ItemListPlan plan = ItemListPlanner.Plan(new[]
        {
            Line(new int[0], new double[0], new[] { 7 }, 1.0),
            Line(new int[0], new double[0], new[] { 7 }, 1.0)
        });

        Assert.Equal(1, Assert.IsType<ItemListPlan.NoRoom>(plan).Line);
    }

    [Fact]
    public void AStackToppedUpByOneLineHasThatMuchLessRoomForTheNext()
    {
        ItemListPlan plan = ItemListPlanner.Plan(new[]
        {
            Line(new[] { 5 }, new[] { 30.0 }, new int[0], 20.0, 50.0),
            Line(new[] { 5 }, new[] { 30.0 }, new[] { 6 }, 20.0, 50.0)
        });

        ItemListPlan.Fits fits = Assert.IsType<ItemListPlan.Fits>(plan);
        Assert.Equal(20.0, Assert.IsType<PlacementStep.TopUp>(fits.Lines[0].Steps[0]).Quantity);
        PlacementStep.TopUp second = Assert.IsType<PlacementStep.TopUp>(fits.Lines[1].Steps[0]);
        Assert.Equal((0, 10.0), (second.Stack, second.Quantity));
        PlacementStep.NewStack rest = Assert.IsType<PlacementStep.NewStack>(fits.Lines[1].Steps[1]);
        Assert.Equal((0, 10.0), (rest.Slot, rest.Quantity));
    }

    private static ItemSpawnOrder? Order(out List<OptionProblem> problems, string? prefab = null,
        string? to = "5200100", JToken? items = null, JToken? slot = null, double? quantity = null, JToken? at = null)
    {
        problems = new List<OptionProblem>();
        PrefabFilter? filter = prefab == null ? null : new PrefabFilter(new[] { PrefabPattern.Of(new JValue(prefab))! });
        return ItemSpawnOrder.Of(filter, quantity, to, slot, at, items, problems);
    }

    [Fact]
    public void AnItemsListIsLinesWithTheirOwnPrefabQuantityAndSlot()
    {
        ItemSpawnOrder order = Order(out _, items: JArray.Parse(
            """[{"prefab":"ItemIronFrames","quantity":2},{"prefab":"ItemWaterPacket","slot":"SuitWater"},{"prefab":9,"slot":4}]"""))!;

        Assert.True(order.FromList);
        Assert.Equal(3, order.Lines.Count);
        Assert.Equal(2, order.Lines[0].Quantity);
        Assert.Equal(new SlotRequest.Auto(), order.Lines[0].Slot);
        Assert.Equal(1, order.Lines[1].Quantity);
        Assert.Equal(new SlotRequest.Key("SuitWater"), order.Lines[1].Slot);
        Assert.Equal(new SlotRequest.Index(4), order.Lines[2].Slot);
    }

    [Fact]
    public void ItemsAndAPrefabTogetherAreRefusedAtTheOptionCheck()
    {
        Assert.Null(Order(out List<OptionProblem> both, prefab: "ItemIronFrames",
            items: JArray.Parse("""[{"prefab":"ItemIronSheets"}]""")));
        Assert.Contains(both, problem => problem.Path == "prefab");

        Assert.Null(Order(out List<OptionProblem> withQuantity, quantity: 2,
            items: JArray.Parse("""[{"prefab":"ItemIronSheets"}]""")));
        Assert.Contains(withQuantity, problem => problem.Path == "quantity");

        Assert.Null(Order(out List<OptionProblem> withSlot, slot: new JValue(1),
            items: JArray.Parse("""[{"prefab":"ItemIronSheets"}]""")));
        Assert.Contains(withSlot, problem => problem.Path == "slot");
    }

    [Fact]
    public void AnItemsListNeedsAHolderAndAtMostSixtyFourLinesEachWellFormed()
    {
        Assert.Null(Order(out List<OptionProblem> point, to: null, at: new JArray(0, 0, 0),
            items: JArray.Parse("""[{"prefab":"ItemIronSheets"}]""")));
        Assert.Contains(point, problem => problem.Path == "at");

        JArray tooMany = new JArray(Enumerable.Range(0, 65).Select(_ => new JObject { ["prefab"] = "ItemIronSheets" }));
        Assert.Null(Order(out List<OptionProblem> many, items: tooMany));
        Assert.Contains(many, problem => problem.Path == "items");
        Assert.NotNull(Order(out _, items: new JArray(Enumerable.Range(0, 64).Select(_ => new JObject { ["prefab"] = "ItemIronSheets" }))));

        Assert.Null(Order(out List<OptionProblem> empty, items: new JArray()));
        Assert.Contains(empty, problem => problem.Path == "items");

        Assert.Null(Order(out List<OptionProblem> lines, items: JArray.Parse(
            """[{"prefab":"ItemIronSheets","quantity":0},{"prefab":"*Sheets*"},{"slot":"x"},{"prefab":"A","qty":1},{"prefab":"B","slot":1.5},7]""")));
        Assert.Equal(new[] { "items[0].quantity", "items[1].prefab", "items[2].prefab", "items[3].qty", "items[4].slot", "items[5]" },
            lines.Select(problem => problem.Path));
    }

    [Fact]
    public void OnePrefabStaysOneLineAndAKeyedSlotNeedsAHolder()
    {
        ItemSpawnOrder order = Order(out _, prefab: "ItemWaterPacket", slot: new JValue("SuitWater"))!;
        Assert.False(order.FromList);
        Assert.Equal(new SlotRequest.Key("SuitWater"), Assert.Single(order.Lines).Slot);

        Assert.Null(Order(out List<OptionProblem> loose, prefab: "ItemWaterPacket", to: null, at: new JArray(0, 0, 0),
            slot: new JValue("SuitWater")));
        Assert.Contains(loose, problem => problem.Path == "slot");
    }

    [Fact]
    public void EverySlotRowShowsItsKeyCompactOrInDetail()
    {
        OccupantView packet = new OccupantView(new ThingView(new ThingId(8), "ItemWaterPacket", "Water Packet"), 1, null,
            null, null);
        List<SlotView> slots = new List<SlotView>
        {
            new SlotView(0, "Slot", "None", packet, "SuitEnhancementSuiteWater"),
            new SlotView(1, "Slot", "None", null, ""),
            new SlotView(2, "Slot", "None", null)
        };

        ContainerContentsView Contents(ShownSlots shown, SlotForm form) => new ContainerContentsView(
            new ThingView(new ThingId(40), "ItemSuitAegis", "Aegis Suit"), new PositionView(0, 0, 0), null, shown, null,
            form);

        JObject detail = JObject.Parse(ApiJson.WriteShared(Contents(ShownSlots.All(slots), SlotForm.Detail)));
        Assert.Equal(new[] { "SuitEnhancementSuiteWater", null, null },
            detail["slots"]!.Select(row => (string?)row["string_key"]).ToArray());
        JObject compact = JObject.Parse(ApiJson.WriteShared(Contents(ShownSlots.Occupied(slots), SlotForm.Compact)));
        Assert.Equal("SuitEnhancementSuiteWater", (string?)compact["slots"]![0]!["string_key"]);
    }

    [Fact]
    public void AMovedItemsToNamesTheKeyOfTheSlotItLandedIn()
    {
        Assert.Equal("""{"id":"273","slot":3,"slot_key":"SharedPower1"}""",
            ApiJson.WriteShared(new KeyedSlotRefView(new ThingId(273), 3, "SharedPower1")));
        Assert.Equal("""{"id":"273","slot":3,"slot_key":null}""",
            ApiJson.WriteShared(new KeyedSlotRefView(new ThingId(273), 3, "")));
    }

    [Fact]
    public void EveryItemsRowNamesItsLine()
    {
        SpawnedView row = new SpawnedView(1, "ItemIronSheets",
            new PlacedView(PlacedView.InSlot, new SlotRefView(new ThingId(9), 2), 6, new ThingId(70)));
        Assert.Equal("""{"line":1,"where":"slot","slot":{"id":"9","slot":2},"quantity":6.0,"reference_id":"70"}""",
            ApiJson.WriteShared(row).Replace("\"quantity\":6,", "\"quantity\":6.0,"));
    }

    [Fact]
    public void InspectPlayerNamesWhatIsWornAndNothingElseIsNull()
    {
        WornView worn = WornView.Nothing with
        {
            Suit = new ThingView(new ThingId(5), "ItemSuitAegis", "Aegis Suit"),
            Helmet = new ThingView(new ThingId(6), "ItemAegisHelmet", "Aegis Helmet")
        };

        JObject json = JObject.Parse(ApiJson.WriteShared(worn));
        Assert.Equal("5", (string?)json["suit"]!["reference_id"]);
        Assert.Equal("6", (string?)json["helmet"]!["reference_id"]);
        foreach (string empty in new[] { "backpack", "toolbelt", "uniform", "glasses", "left_hand", "right_hand" })
        {
            Assert.Equal(JTokenType.Null, json[empty]!.Type);
        }
    }

    // The binder fills a default into an option the call left out, so these bind real JSON arguments through the
    // generated options class, as the verb handler does, instead of passing a hand-made null.
    private static ItemSpawnOrder? BoundOrder(string json, out List<OptionProblem> problems)
    {
        BuildItemOptions options = BuildItemOptions.Bind(new OptionReader(BuildItemOptions.Spec, JObject.Parse(json)));
        problems = new List<OptionProblem>();
        return ItemSpawnOrder.Of(options.Selector.Prefab, options.Quantity, options.To, options.Slot, options.At,
            options.Items, problems);
    }

    [Fact]
    public void AnAbsentQuantityBindsAsOneAndTheItemsFormIsAcceptedWithoutOne()
    {
        ItemSpawnOrder single = BoundOrder("""{"prefab":"ItemIronSheets","to":"5200100"}""", out List<OptionProblem> none)!;
        Assert.Empty(none);
        Assert.Equal(1, Assert.Single(single.Lines).Quantity);

        ItemSpawnOrder list = BoundOrder(
            """{"to":"5200100","items":[{"prefab":"ItemIronFrames","quantity":2},{"prefab":"ItemIronSheets"}]}""",
            out List<OptionProblem> accepted)!;
        Assert.Empty(accepted);
        Assert.Equal(new[] { 2.0, 1.0 }, list.Lines.Select(line => line.Quantity));

        Assert.Null(BoundOrder("""{"prefab":"ItemIronSheets","to":"5200100","quantity":3,"items":[{"prefab":"X"}]}""",
            out List<OptionProblem> both));
        Assert.Contains(both, problem => problem.Path == "quantity");
    }
}
