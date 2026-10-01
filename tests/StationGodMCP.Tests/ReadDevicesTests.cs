#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.DeviceReads;
using StationGodMCP.Server;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// read_devices (1.10.0): the request parser (shape, strict keys inside items, bounds, names kept as sent) and the
/// reply's wire (keys by the string sent, per-value, per-part and per-item errors in the single tools' format).
/// </summary>
public sealed class ReadDevicesTests
{
    private static DeviceReadParse Parse(string items, string? include = null) =>
        DeviceReadParse.Of(JToken.Parse(items), include == null ? null : JToken.Parse(include));

    private static DeviceReadRequest Parsed(string items, string? include = null) =>
        Assert.IsType<DeviceReadParse.Parsed>(Parse(items, include)).Request;

    private static string Refused(string items, string? include = null) =>
        Assert.IsType<DeviceReadParse.Refused>(Parse(items, include)).Message;

    // ---- parsing ----

    [Fact]
    public void ParsesEveryPart()
    {
        DeviceReadRequest request = Parsed("""
            [{"reference_id": "811234", "logic": ["Temperature", "Pressure"],
              "slots": [{"index": 0, "logic": ["Occupied", "Quantity"]}, {"index": 1}],
              "atmosphere": {"of": "internal"}, "reagents": true},
             {"reference_id": "900077", "atmosphere": {}},
             {"reference_id": 811410, "atmosphere": {"port": 2}}]
            """, """["clock"]""");

        Assert.True(request.Clock);
        Assert.Equal(3, request.Items.Count);
        DeviceReadItem furnace = request.Items[0];
        Assert.Equal(811234L, furnace.ReferenceId);
        Assert.Equal(new[] { "Temperature", "Pressure" }, furnace.Logic!.Select(name => name.Key));
        Assert.Equal(2, furnace.Slots!.Count);
        Assert.Equal(new[] { "Occupied", "Quantity" }, furnace.Slots[0].Logic!.Select(name => name.Key));
        Assert.Null(furnace.Slots[1].Logic);
        Assert.IsType<AtmosphereTarget.Internal>(furnace.Atmosphere);
        Assert.True(furnace.Reagents);
        Assert.IsType<AtmosphereTarget.Own>(request.Items[1].Atmosphere);
        Assert.Null(request.Items[1].Logic);
        Assert.False(request.Items[1].Reagents);
        Assert.Equal(811410L, request.Items[2].ReferenceId);
        Assert.Equal(2, Assert.IsType<AtmosphereTarget.AtPort>(request.Items[2].Atmosphere).Port);
    }

    [Fact]
    public void ClockOnlyWhenIncluded()
    {
        Assert.False(Parsed("""[{"reference_id": "1", "logic": ["On"]}]""").Clock);
        Assert.False(Parsed("""[{"reference_id": "1", "logic": ["On"]}]""", "null").Clock);
        Assert.True(Parsed("""[{"reference_id": "1", "logic": ["On"]}]""", """["Clock"]""").Clock);
    }

    [Fact]
    public void NullIsAnAbsentPart()
    {
        DeviceReadItem item = Parsed("""[{"reference_id": "1", "logic": ["On"], "slots": null, "reagents": null}]""")
            .Items[0];

        Assert.Null(item.Slots);
        Assert.False(item.Reagents);
    }

    // ---- names kept as sent ----

    [Fact]
    public void KeysAreTheStringsSent()
    {
        DeviceReadItem item = Parsed("""[{"reference_id": "1", "logic": ["pressure", "Pressure", "280", 281, " On "]}]""")
            .Items[0];

        Assert.Equal(new[] { "pressure", "Pressure", "280", "281", " On " }, item.Logic!.Select(name => name.Key));
        Assert.Equal(JTokenType.String, item.Logic![2].Token.Type);
        Assert.Equal(JTokenType.Integer, item.Logic[3].Token.Type);
    }

    [Fact]
    public void ANumberSentAndTheSameTextSentAreTheSameKey()
    {
        string message = Refused("""[{"reference_id": "1", "logic": ["280", 280]}]""");

        Assert.Contains("items[0].logic names '280' twice", message);
    }

    [Fact]
    public void ANameSentTwiceIsRefused()
    {
        Assert.Contains("names 'On' twice", Refused("""[{"reference_id": "1", "logic": ["On", "On"]}]"""));
        Assert.Contains("items[0].slots[0].logic names 'Quantity' twice",
            Refused("""[{"reference_id": "1", "slots": [{"index": 0, "logic": ["Quantity", "Quantity"]}]}]"""));
    }

    [Fact]
    public void SameNameOnTwoItemsIsFine()
    {
        Assert.Equal(2, Parsed("""[{"reference_id": "1", "logic": ["On"]}, {"reference_id": "1", "logic": ["On"]}]""")
            .Items.Count);
    }

    // ---- strict keys ----

    [Theory]
    [InlineData("""[{"reference_id": "1", "logic": ["On"], "id": "1"}]""", "items[0] has an unknown key 'id'")]
    [InlineData("""[{"reference_id": "1", "health": true}]""", "items[0] has an unknown key 'health'")]
    [InlineData("""[{"reference_id": "1", "pipe_health": {}}]""", "items[0] has an unknown key 'pipe_health'")]
    [InlineData("""[{"reference_id": "1", "slots": [{"index": 0, "logic_type": "On"}]}]""",
        "items[0].slots[0] has an unknown key 'logic_type'")]
    [InlineData("""[{"reference_id": "1", "atmosphere": {"detail": true}}]""",
        "items[0].atmosphere has an unknown key 'detail'")]
    [InlineData("""[{"reference_id": "1", "atmosphere": {"port": "Input"}}]""",
        "items[0].atmosphere.port must be an integer from 0 to 64")]
    [InlineData("""[{"reference_id": "1", "atmosphere": {"of": "pipe"}}]""", "items[0].atmosphere.of must be")]
    [InlineData("""[{"reference_id": "1", "atmosphere": {"of": "internal", "port": 0}}]""", "takes of or port, not both")]
    [InlineData("""[{"reference_id": "1", "atmosphere": true}]""", "items[0].atmosphere must be an object")]
    [InlineData("""[{"reference_id": "1", "reagents": "yes"}]""", "items[0].reagents must be true or false")]
    [InlineData("""[{"logic": ["On"]}]""", "items[0].reference_id is required")]
    [InlineData("""[{"reference_id": "abc", "logic": ["On"]}]""", "items[0].reference_id must be a reference id")]
    [InlineData("""[{"reference_id": "1"}]""", "items[0] asks for nothing")]
    [InlineData("""[{"reference_id": "1", "reagents": false}]""", "items[0] asks for nothing")]
    [InlineData("""["1"]""", "items[0] must be an object")]
    [InlineData("""[{"reference_id": "1", "logic": []}]""", "items[0].logic must be an array of 1 to 64")]
    [InlineData("""[{"reference_id": "1", "logic": [true]}]""", "items[0].logic[0] must be a logic type name or number")]
    [InlineData("""[{"reference_id": "1", "slots": [{"logic": ["On"]}]}]""", "items[0].slots[0].index is required")]
    [InlineData("""[{"reference_id": "1", "slots": [{"index": -1}]}]""", "items[0].slots[0].index must be an integer")]
    [InlineData("""[{"reference_id": "1", "slots": [{"index": 0}, {"index": 0}]}]""", "names slot 0 twice")]
    [InlineData("""[]""", "Argument 'items' must be an array of 1 to 128 entries")]
    [InlineData("""{}""", "Argument 'items' must be an array of 1 to 128 entries")]
    public void RefusesWhatTheItemsDoNotTake(string items, string expected)
    {
        Assert.Contains(expected, Refused(items));
    }

    [Theory]
    [InlineData("""["weather"]""", "include[0] must be \"clock\"")]
    [InlineData("""[1]""", "include[0] must be \"clock\"")]
    [InlineData("\"clock\"", "Argument 'include' must be an array")]
    public void IncludeTakesOnlyTheClock(string include, string expected)
    {
        Assert.Contains(expected, Refused("""[{"reference_id": "1", "logic": ["On"]}]""", include));
    }

    // ---- bounds ----

    private static string Items(int count, string item) =>
        "[" + string.Join(",", Enumerable.Repeat(item, count)) + "]";

    private static string Names(int count) =>
        "[" + string.Join(",", Enumerable.Range(0, count).Select(index => $"\"{index}\"")) + "]";

    [Fact]
    public void ItemsAreBounded()
    {
        string item = """{"reference_id": "1", "logic": ["On"]}""";

        Assert.Equal(DeviceReadBounds.MaximumItems, Parsed(Items(DeviceReadBounds.MaximumItems, item)).Items.Count);
        Assert.Contains("1 to 128", Refused(Items(DeviceReadBounds.MaximumItems + 1, item)));
    }

    [Fact]
    public void LogicPerItemIsBounded()
    {
        Assert.Equal(64, Parsed($$"""[{"reference_id": "1", "logic": {{Names(64)}}}]""").Items[0].Logic!.Count);
        Assert.Contains("items[0].logic must be an array of 1 to 64",
            Refused($$"""[{"reference_id": "1", "logic": {{Names(65)}}}]"""));
    }

    [Fact]
    public void SlotsPerItemAreBounded()
    {
        string Slots(int count) =>
            "[" + string.Join(",", Enumerable.Range(0, count).Select(index => $"{{\"index\": {index}}}")) + "]";

        Assert.Equal(16, Parsed($$"""[{"reference_id": "1", "slots": {{Slots(16)}}}]""").Items[0].Slots!.Count);
        Assert.Contains("1 to 16 slots", Refused($$"""[{"reference_id": "1", "slots": {{Slots(17)}}}]"""));
    }

    [Fact]
    public void ValuesInAllAreBounded()
    {
        // 16 items of 64 names: exactly 1,024 values.
        string full = $$"""{"reference_id": "1", "logic": {{Names(64)}}}""";
        Assert.Equal(16, Parsed(Items(16, full)).Items.Count);

        string message = Refused(Items(16, full).TrimEnd(']') + """,{"reference_id": "2", "logic": ["On"]}]""");
        Assert.Contains("reads 1025 values; at most 1024", message);
    }

    [Fact]
    public void ASlotWithoutLogicCountsThirtyTwo()
    {
        // 2 slots without logic = 64 values, the same as 64 names.
        DeviceReadItem item = Parsed("""[{"reference_id": "1", "slots": [{"index": 0}, {"index": 1, "logic": ["On"]}]}]""")
            .Items[0];

        Assert.Equal(DeviceReadBounds.AllSlotValuesWeight + 1, item.Weight);

        string slots = "[" + string.Join(",", Enumerable.Range(0, 16).Select(index => $"{{\"index\": {index}}}")) + "]";
        string item16 = $$"""{"reference_id": "1", "slots": {{slots}}}""";
        // 2 items x 16 slots x 32 = 1,024: allowed; a third value is not.
        Assert.Equal(2, Parsed(Items(2, item16)).Items.Count);
        Assert.Contains("a slot without logic counts 32",
            Refused(Items(2, item16).TrimEnd(']') + """,{"reference_id": "2", "logic": ["On"]}]"""));
    }

    [Fact]
    public void PortIsBounded()
    {
        Assert.Equal(64, Assert.IsType<AtmosphereTarget.AtPort>(
            Parsed("""[{"reference_id": "1", "atmosphere": {"port": 64}}]""").Items[0].Atmosphere).Port);
        Assert.Contains("from 0 to 64", Refused("""[{"reference_id": "1", "atmosphere": {"port": 65}}]"""));
    }

    // ---- the reply's wire ----

    private static JObject Wire(object view) => JObject.Parse(WireCheck.New(view));

    [Fact]
    public void ValuesComeBackUnderTheNameSent()
    {
        LogicReadings logic = new LogicReadings(
            new Dictionary<string, double> { ["pressure"] = 101.5, ["280"] = 3.0, ["Temperature"] = double.NaN },
            new Dictionary<string, ErrorView>
            {
                ["Mode"] = new ErrorView("logic_not_readable", "Device 5 does not expose Mode (3) as readable.")
            });
        DeviceReadItemView item = new DeviceReadItemView(0, new ThingId(5),
            new DeviceReadParts(logic, null, null, null, new Dictionary<string, ErrorView>()));

        JObject wire = Wire(item);

        Assert.Equal(
            """{"index":0,"ok":true,"reference_id":"5","logic":{"pressure":101.5,"280":3.0,"Temperature":"NaN"},"logic_errors":{"Mode":{"code":"logic_not_readable","message":"Device 5 does not expose Mode (3) as readable."}}}""",
            wire.ToString(Newtonsoft.Json.Formatting.None));
    }

    [Fact]
    public void PartsNotAskedAndEmptyErrorsAreLeftOut()
    {
        DeviceReadItemView item = new DeviceReadItemView(2, new ThingId(7),
            new DeviceReadParts(new LogicReadings(new Dictionary<string, double> { ["On"] = 1 },
                new Dictionary<string, ErrorView>()), null, null, null, new Dictionary<string, ErrorView>()));

        Assert.Equal("""{"index":2,"ok":true,"reference_id":"7","logic":{"On":1.0}}""",
            Wire(item).ToString(Newtonsoft.Json.Formatting.None));
    }

    [Fact]
    public void APartThatFailsWholeIsInErrors()
    {
        ErrorView outOfScope = new ErrorView("device_not_found",
            "Device 9 is not visible on a data network connected to gateway 3 and is not worn or held by a player.");
        DeviceReadItemView item = new DeviceReadItemView(1, new ThingId(9),
            new DeviceReadParts(null, null, null, new ReagentsReadView(0.0, new List<ReagentView>()),
                new Dictionary<string, ErrorView> { ["logic"] = outOfScope, ["slots"] = outOfScope }));

        JObject wire = Wire(item);

        Assert.Null(wire["logic"]);
        Assert.Equal("device_not_found", (string?)wire["errors"]!["logic"]!["code"]);
        Assert.Equal("device_not_found", (string?)wire["errors"]!["slots"]!["code"]);
        Assert.Equal(0.0, (double)wire["reagents"]!["total"]!);
    }

    [Fact]
    public void AnItemThatNamesNothingFailsAlone()
    {
        DeviceReadFailedView failed = new DeviceReadFailedView(3, new ThingId(42),
            ApiErrors.ThingNotFound(new ThingId(42)));
        BatchBuilder batch = new BatchBuilder(2);
        batch.Succeeded(new DeviceReadItemView(0, new ThingId(5), new DeviceReadParts(null, null, null, null, null)));
        batch.Failed(failed);
        ReadDevicesView view = new ReadDevicesView("world", null, batch.Build());

        JObject wire = Wire(view);

        Assert.Equal("""{"index":3,"ok":false,"reference_id":"42","error":{"code":"thing_not_found","message":"No thing with reference id 42."}}""",
            wire["results"]![1]!.ToString(Newtonsoft.Json.Formatting.None));
        Assert.Equal(new[] { "gateway_id", "count", "success_count", "error_count", "results" },
            wire.Properties().Select(property => property.Name));
        Assert.Equal(1, (int)wire["error_count"]!);
    }

    [Fact]
    public void ClockComesSecondWhenAsked()
    {
        ReadDevicesView view = new ReadDevicesView("world", new GameClockView(12.5f, false, 0.25f, 3),
            new BatchBuilder(0).Build());

        JObject wire = Wire(view);

        Assert.Equal("clock", wire.Properties().ElementAt(1).Name);
        Assert.Equal(12.5, (double)wire["clock"]!["game_time_s"]!);
    }

    [Fact]
    public void SlotsReadTheirValuesOrFailAlone()
    {
        List<SlotReadView> slots = new List<SlotReadView>
        {
            SlotReadView.Read(0, new LogicReadings(new Dictionary<string, double> { ["Occupied"] = 1, ["Quantity"] = 50 },
                new Dictionary<string, ErrorView>())),
            SlotReadView.Failed(9, ApiErrors.Refused("slot_not_found", "Device 5 does not expose slot index 9."))
        };
        DeviceReadItemView item = new DeviceReadItemView(0, new ThingId(5),
            new DeviceReadParts(null, slots, null, null, null));

        Assert.Equal(
            """[{"index":0,"logic":{"Occupied":1.0,"Quantity":50.0}},{"index":9,"error":{"code":"slot_not_found","message":"Device 5 does not expose slot index 9."}}]""",
            Wire(item)["slots"]!.ToString(Newtonsoft.Json.Formatting.None));
    }

    [Fact]
    public void AtmosphereIsCompact()
    {
        AtmosphereReadView atmosphere = new AtmosphereReadView("pipe_network", new ThingId(70), new ThingId(71),
            new AtmosphereState(100.0, 2604.1, 881.2, 353.0, 0.5),
            new List<CompactGasView>
            {
                new CompactGasView("Methane", false, 7.0, null),
                new CompactGasView("LiquidCarbonDioxide", true, 1.5, 0.5)
            });

        Assert.Equal(
            """{"source":"pipe_network","atmosphere_id":"70","network_id":"71","volume_l":100.0,"pressure_kpa":2604.1,"temperature_k":881.2,"total_mol":353.0,"liquid_volume_l":0.5,"contents":[{"gas":"Methane","state":"gas","amount_mol":7.0},{"gas":"LiquidCarbonDioxide","state":"liquid","amount_mol":1.5,"liquid_l":0.5}]}""",
            WireCheck.New(atmosphere));
    }

    [Fact]
    public void AnInternalAtmosphereHasNoNetworkId()
    {
        AtmosphereReadView atmosphere = new AtmosphereReadView("internal", new ThingId(70), null,
            new AtmosphereState(1000.0, 0.0, 0.0, 0.0, 0.0), new List<CompactGasView>());

        Assert.Null(JObject.Parse(WireCheck.New(atmosphere))["network_id"]);
    }

    [Fact]
    public void ThingHealthNetworkWire()
    {
        HealthNetworkView view = new HealthNetworkView(new ThingId(900120), "pipe", 214, true,
            Slice<HealthView>.Page(new List<HealthView>(), PageRequestFor(0, 200), 0));

        Assert.Equal(
            """{"network_id":"900120","kind":"pipe","pieces":214,"damaged_only":true,"things":[],"count":0,"total":0,"offset":0,"limit":200,"has_more":false}""",
            WireCheck.New(view));
    }

    private static PageRequest PageRequestFor(int offset, int limit) =>
        PageRequest.From(new Args(new JObject { ["offset"] = offset, ["limit"] = limit }), limit, 500);

    // ---- the sidecar's schema ----

    private static IReadOnlyList<string> SidecarProblems(string tool, string arguments)
    {
        using JsonDocument document = JsonDocument.Parse(arguments);
        return ArgumentCheck.Problems(Program.InputSchemas[tool], document.RootElement);
    }

    [Fact]
    public void TheSidecarTakesTheSmeltersRequest()
    {
        Assert.Empty(SidecarProblems("read_devices", """
            {"include": ["clock"], "items": [
              {"reference_id": "811234", "logic": ["Temperature", 280], "reagents": true,
               "slots": [{"index": 0, "logic": ["Occupied", "OccupantHash", "Quantity"]}], "atmosphere": {"of": "internal"}},
              {"reference_id": "811410", "atmosphere": {"port": 1}},
              {"reference_id": "900077", "atmosphere": {}}]}
            """));
    }

    [Fact]
    public void TheSidecarRefusesUnknownKeysInsideItems()
    {
        Assert.NotEmpty(SidecarProblems("read_devices", """{"items": [{"reference_id": "1", "health": true}]}"""));
        Assert.NotEmpty(SidecarProblems("read_devices", """{"items": [{"reference_id": "1", "atmosphere": {"detail": true}}]}"""));
        Assert.NotEmpty(SidecarProblems("read_devices", """{"items": [{"reference_id": "1", "logic": ["On"]}], "include": ["weather"]}"""));
    }

    [Fact]
    public void ReadDevicesIsALargeReplyTool()
    {
        Assert.DoesNotContain("read_devices", ToolDefinitions.SmallReplies);
        Assert.Contains("read_devices", ToolDefinitions.Names);
    }

    [Fact]
    public void ThingHealthTakesANetwork()
    {
        Assert.Empty(SidecarProblems("thing_health", """{"network_id": {"reference_id": "811410", "port": 1}, "damaged_only": true}"""));
        Assert.Empty(SidecarProblems("thing_health", """{"network_id": "900120", "kind": "cable", "limit": 50}"""));
    }
}
