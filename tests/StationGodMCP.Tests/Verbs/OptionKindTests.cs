#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// The shared option kinds, each parsed from a what's options as its generated class binds it: the selector (ids and
/// the per-id list form, near, box, network, prefab), fields, paging, report and confirm, and the materials source.
/// A value a kind cannot use is a problem in verb names, and an option the what does not take reads as absent.
/// </summary>
public sealed class OptionKindTests
{
    private static OptionReader Reader(WhatSpec what, string options) => new(what, JObject.Parse(options));

    // ---- the per-id list form ----

    [Fact]
    public void OneIdAnswersTheSingleShapeAndAListAnswersRows()
    {
        PerIdList one = ReadLogicOptions.Bind(Reader(ReadLogicOptions.Spec, """{"id": " 42 "}""")).Selector.Ids!;
        PerIdList many = ReadLogicOptions.Bind(Reader(ReadLogicOptions.Spec, """{"id": ["1", "2"]}""")).Selector.Ids!;

        Assert.Equal(new[] { "42" }, one.Ids);
        Assert.False(one.AnswersRows);
        Assert.Equal(new[] { "1", "2" }, many.Ids);
        Assert.True(many.AnswersRows);
        Assert.Null(many.FillsList);
    }

    [Fact]
    public void AnIdListFillsTheListOptionIdListNames()
    {
        PerIdList moves = MoveItemOptions.Bind(Reader(MoveItemOptions.Spec, """{"id": ["7", "8"]}""")).Selector.Ids!;

        Assert.Equal("moves", MoveItemOptions.Spec.IdList);
        Assert.Equal("moves", moves.FillsList);
        Assert.False(moves.AnswersRows);
        Assert.Equal("""[{"id":"7"},{"id":"8"}]""", moves.Entries().ToString(Newtonsoft.Json.Formatting.None));
        Assert.Equal("reads", ReadSampleOptions.Spec.IdList);
    }

    [Fact]
    public void AnEmptyIdIsAProblemAtItsPlaceInTheList()
    {
        OptionReader reader = Reader(ReadLogicOptions.Spec, """{"id": ["1", " "]}""");

        ReadLogicOptions.Bind(reader);

        OptionProblem problem = Assert.Single(reader.Problems);
        Assert.Equal("id[1]", problem.Path);
    }

    // ---- defaults the catalogue states per form (x-default-when) ----

    [Theory]
    [InlineData("""{}""", 30)]
    [InlineData("""{"box": [[0, 0, 0], [4, 4, 4]]}""", 25)]
    [InlineData("""{"summarize": true}""", 10)]
    [InlineData("""{"summarize": false}""", 30)]
    [InlineData("""{"limit": 7, "box": [[0, 0, 0], [4, 4, 4]]}""", 7)]
    public void ALimitLeftOutIsTheDefaultOfTheFormTheCallTakes(string options, int limit)
    {
        Assert.Equal(limit, SurveyNetworkOptions.Bind(Reader(SurveyNetworkOptions.Spec, options)).Paging.Limit);
    }

    [Fact]
    public void AFormOnAFlagReadsTheFlagsOwnDefaultWhenItIsLeftOut()
    {
        Assert.Equal(12, FindItemOptions.Bind(Reader(FindItemOptions.Spec, """{"group_by": "place"}""")).Paging.Limit);
        Assert.Equal(10, FindItemOptions.Bind(Reader(FindItemOptions.Spec, """{}""")).Paging.Limit);
        // compact's own default is true, so self_test defaults to false unless compact is false.
        Assert.False(Reader(InspectRocketOptions.Spec, """{}""").DefaultFlag("self_test"));
        Assert.True(Reader(InspectRocketOptions.Spec, """{"compact": false}""").DefaultFlag("self_test"));
    }

    // ---- the cost a what states (x-cost) ----

    [Fact]
    public void TheCostCountsThePerListsAndTheDurationIsTheOptionAtMostItsMaximum()
    {
        Assert.Equal(StationGodMCP.Pure.Catalogue.CostClass.World, FindThingOptions.Spec.Cost.Class);
        Assert.Equal(3, ReadLogicOptions.Spec.Cost.At(JObject.Parse("""{"id": ["1", "2", "3"]}""")).Items);
        Assert.Equal(1, ReadLogicOptions.Spec.Cost.At(JObject.Parse("""{"id": "1"}""")).Items);
        Assert.Equal(5000, ReadSampleOptions.Spec.Cost.DurationMs(JObject.Parse("""{"seconds": 5}""")));
        Assert.Equal(30000, ReadSampleOptions.Spec.Cost.DurationMs(JObject.Parse("""{"seconds": 300}""")));
        Assert.Equal(30000, ReadSampleOptions.Spec.Cost.DurationMs(new JObject()));
        Assert.Equal(0, ReadLogicOptions.Spec.Cost.DurationMs(new JObject()));
        Assert.Equal("find.thing", FindThingOptions.Spec.Key);
        Assert.Equal("undo", UndoOptions.Spec.Key);
    }

    // ---- the selector ----

    [Theory]
    [InlineData("\"player\"", typeof(NearPoint.Player))]
    [InlineData("\"Looking_At\"", typeof(NearPoint.LookingAt))]
    [InlineData("\"4711\"", typeof(NearPoint.Thing))]
    [InlineData("[1, 2.5, -3]", typeof(NearPoint.At))]
    public void NearIsAPointThePlayerTheCrosshairOrAThing(string near, Type expected)
    {
        Selector selector = FindThingOptions.Bind(Reader(FindThingOptions.Spec, $$"""{"near": {{near}}, "radius": 5}""")).Selector;

        Assert.IsType(expected, selector.Near);
        Assert.Equal(5, selector.Radius);
        Assert.True(selector.HasSpread);
    }

    [Fact]
    public void ANearPointWithARadiusIsASphereAndABoxItsCorners()
    {
        Selector near = FindThingOptions.Bind(Reader(FindThingOptions.Spec, """{"near": [0, 0, 0], "radius": 2}""")).Selector;
        Selector box = FindThingOptions.Bind(Reader(FindThingOptions.Spec, """{"box": [[2, 2, 2], [0, 0, 0]]}""")).Selector;

        Assert.True(near.AreaAround(null).Contains(new Vec3(1, 1, 1)));
        Assert.False(near.AreaAround(null).Contains(new Vec3(2, 2, 2)));
        Assert.True(box.AreaAround(null).Contains(new Vec3(2, 0, 1)));
        Assert.False(box.AreaAround(null).Contains(new Vec3(3, 0, 1)));
        Assert.True(Selector.None.AreaAround(null).Contains(new Vec3(1e6, 0, 0)));
    }

    [Theory]
    [InlineData("""{"near": [1, 2]}""", "near")]
    [InlineData("""{"near": "  "}""", "near")]
    [InlineData("""{"box": [[1, 2, 3]]}""", "box")]
    [InlineData("""{"box": [[1, 2, 3], [1, "y", 3]]}""", "box")]
    public void APlaceThatIsNoPlaceIsAProblem(string options, string path)
    {
        OptionReader reader = Reader(FindThingOptions.Spec, options);

        FindThingOptions.Bind(reader);

        Assert.Equal(path, Assert.Single(reader.Problems).Path);
    }

    [Fact]
    public void ANetworkIsAnIdOrAnIdWithAPort()
    {
        WhatSpec what = SurveyNetworkOptions.Spec;

        Assert.Equal(new NetworkTarget("88", null), Selector.Bind(Reader(what, """{"network": " 88 "}""")).Network);
        Assert.Equal(new NetworkTarget("88", "2"), Selector.Bind(Reader(what, """{"network": {"id": "88", "port": 2}}""")).Network);
        OptionReader bad = Reader(what, """{"network": {"port": 2}}""");
        Selector.Bind(bad);
        Assert.Equal("network", Assert.Single(bad.Problems).Path);
    }

    [Fact]
    public void APrefabIsANameAHashOrPartsInAnyOrder()
    {
        PrefabFilter filter = FindThingOptions.Bind(Reader(FindThingOptions.Spec,
            """{"prefab": ["StructureBattery", -1234, "*Tank*Insulated*"]}""")).Selector.Prefab!;

        Assert.True(filter.Keeps("structurebattery", 0));
        Assert.True(filter.Keeps("Anything", -1234));
        Assert.True(filter.Keeps("StructureInsulatedTankSmall", 0));
        Assert.False(filter.Keeps("StructureTankSmall", 0));
        Assert.False(filter.Keeps("StructureBatteryLarge", 0));
    }

    [Fact]
    public void AnOptionTheWhatDoesNotTakeReadsAsAbsent()
    {
        Selector selector = Selector.Bind(Reader(ReadClockOptions.Spec, """{"id": "1", "near": "player"}"""));

        Assert.Equal(Selector.None, selector);
    }

    // ---- fields ----

    [Fact]
    public void FieldsHoldsIncludeVerboseAndTheCallsShape()
    {
        List<string> problems = new();
        JObject options = JObject.Parse("""{"include": ["Slots"], "verbose": true, "fields": ["devices.reference_id"]}""");
        var shape = Fields.ShapeOf(null, options, null, problems);
        Fields fields = Fields.Bind(new OptionReader(BuildCableOptions.Spec, options, shape));

        Assert.Empty(problems);
        Assert.True(fields.Includes("slots"));
        Assert.True(fields.Verbose);
        Assert.NotNull(fields.Shape!.Fields);
    }

    [Fact]
    public void AFieldsOptionReplacesTheWireShapesFieldsAndKeepsItsOtherKeys()
    {
        List<string> problems = new();
        var shape = Fields.ShapeOf(JObject.Parse("""{"fields": ["x"], "max_bytes": 2048}"""),
            JObject.Parse("""{"fields": ["things.reference_id"]}"""), null, problems);

        Assert.Empty(problems);
        Assert.Equal(2048, shape!.MaxBytes);
        Assert.NotNull(shape.Fields!.Top.Child("things"));
    }

    // ---- paging ----

    [Fact]
    public void PagingTakesLimitOffsetAndTheOrderInVerbWords()
    {
        Pure.Verbs.Paging paging = Pure.Verbs.Paging.Bind(Reader(FindThingOptions.Spec, """{"limit": 2, "offset": 1, "order": "ID"}"""));
        Page<int> page = paging.Of(new[] { 10, 11, 12, 13 });

        Assert.Equal(ListOrder.ReferenceId, paging.Order);
        Assert.Equal(new[] { 11, 12 }, page.Entries);
        Assert.Equal(4, page.Total);
        Assert.True(page.HasMore);
        Assert.Equal(ListOrder.Nearest, Pure.Verbs.Paging.Bind(Reader(FindThingOptions.Spec, "{}")).Order);
    }

    [Fact]
    public void WithoutALimitAPageIsEveryEntryPastTheOffsetAndALeftOutLimitIsTheWhatsDefault()
    {
        Page<int> page = new Pure.Verbs.Paging(null, 3, ListOrder.Nearest).Of(new[] { 1, 2, 3, 4 });

        Assert.Equal(new[] { 4 }, page.Entries);
        Assert.False(page.HasMore);
        Assert.Null(page.Limit);
        Assert.Equal(8, Pure.Verbs.Paging.Bind(Reader(FindThingOptions.Spec, """{"offset": 3}""")).Limit);
    }

    [Fact]
    public void ACursorIsCarriedAsGiven()
    {
        Assert.Equal("k17", Pure.Verbs.Paging.Bind(Reader(FindThingOptions.Spec, """{"cursor": " k17 "}""")).Cursor);
        Assert.Null(Pure.Verbs.Paging.Bind(Reader(FindThingOptions.Spec, "{}")).Cursor);
        Assert.Null(Pure.Verbs.Paging.Bind(Reader(ReadClockOptions.Spec, """{"cursor": "k17"}""")).Cursor);
    }

    // ---- report and confirm ----

    [Fact]
    public void AWhatThatTakesConfirmActsOnlyWithIt()
    {
        Assert.False(ReportConfirm.Bind(Reader(BuildStructureOptions.Spec, "{}")).Acts);
        Assert.True(ReportConfirm.Bind(Reader(BuildStructureOptions.Spec, """{"confirm": true}""")).Acts);
        Assert.True(ReportConfirm.Bind(Reader(WriteChipOptions.Spec, "{}")).Acts);
        Assert.Equal(3, ReportConfirm.Bind(Reader(BuildCableOptions.Spec, """{"rows": 3}""")).Rows);
    }

    // ---- materials ----

    [Fact]
    public void MaterialsLeftOutAreThePlayersAndRefundsTakeTheDefaultChain()
    {
        MaterialsSource source = MaterialsSource.Bind(Reader(BuildStructureOptions.Spec, "{}"));

        Assert.True(source.FromPlayer);
        Assert.Same(RefundRoute.Default, source.RefundTo);
    }

    [Fact]
    public void MaterialsNameHoldersInOrderAndRefundToAWordOrAChain()
    {
        MaterialsSource holders = MaterialsSource.Bind(Reader(RemoveStructureOptions.Spec,
            """{"materials": ["501", "player"], "refund_to": ["storage", "9001"]}"""));
        MaterialsSource none = MaterialsSource.Bind(Reader(RemoveStructureOptions.Spec, """{"refund_to": "none"}"""));

        Assert.Equal(new Payer[] { new Payer.Holder("501"), new Payer.Player() }, holders.Payers);
        Assert.Equal(new[] { "storage", "9001" }, holders.RefundTo.Words);
        Assert.False(none.RefundTo.GivesBack);
        Assert.False(MaterialsSource.Bind(Reader(RemoveStructureOptions.Spec, """{"materials": "501"}""")).FromPlayer);
    }

    [Theory]
    [InlineData("""{"refund_to": "attic"}""")]
    [InlineData("""{"refund_to": ["storage", "STORAGE"]}""")]
    public void ARefundPlaceThatIsNoneIsAProblem(string options)
    {
        OptionReader reader = Reader(RemoveStructureOptions.Spec, options);

        MaterialsSource.Bind(reader);

        Assert.Equal("refund_to", Assert.Single(reader.Problems).Path);
    }

    // ---- the generated classes ----

    [Fact]
    public void EveryWhatsOwnOptionsAreTyped()
    {
        WriteLogicOptions write = WriteLogicOptions.Bind(Reader(WriteLogicOptions.Spec,
            """{"id": "5", "logic": "On", "value": 1, "allow": ["lint"], "gateway": "world", "confirm": true}"""));

        Assert.Equal("world", write.Gateway);
        Assert.Equal(new[] { "lint" }, write.Allow);
        Assert.Equal(1, (double)write.Value!);
        Assert.True(write.Report.Acts);
        Assert.Equal(new[] { "5" }, write.Selector.Ids!.Ids);
    }

    [Fact]
    public void TheSurfaceIsTheCatalogueVerbsWithoutRunScript()
    {
        Assert.Equal(VerbCatalogue.Verbs.Select(VerbCatalogue.NameOf).Where(name => name != "run_script"),
            VerbSurface.VerbNames);
        foreach ((string verb, string what, System.Text.Json.Nodes.JsonObject source) in VerbCatalogue.Whats())
        {
            if (verb == "run_script")
            {
                continue;
            }

            WhatSpec spec = VerbSurface.Verb(verb)!.What(what == VerbCatalogue.WhatlessKey ? null : what)!;
            Assert.Equal(VerbCatalogue.WireOptionsOf(source).Select(option => option.Name), spec.OptionNames);
            Assert.Equal((string?)source["id_list"], spec.IdList);
        }
    }
}
