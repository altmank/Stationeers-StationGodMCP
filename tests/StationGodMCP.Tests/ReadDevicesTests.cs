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
/// read logic's items, the reply's wire (DeviceReads' views): keys by the string sent, and per-value, per-part and per-item
/// errors. The request itself is ReadLogicTests'.
/// </summary>
public sealed class ReadDevicesTests
{
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
        DeviceReadFailedView failed = new DeviceReadFailedView(3, "42",
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
            Slice<HealthView>.Page(new List<HealthView>(), PageRequestFor(0, 200), 0), BrokenNeighbourReport.None);

        Assert.Equal(
            """{"network_id":"900120","kind":"pipe","pieces":214,"damaged_only":true,"broken_neighbour_count":0,"things":[],"count":0,"total":0,"offset":0,"limit":200,"has_more":false}""",
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
        Assert.DoesNotContain("read_devices", ToolCatalogue.SmallReplies);
        Assert.Contains("read_devices", ToolCatalogue.Names);
    }

    [Fact]
    public void ThingHealthTakesANetwork()
    {
        Assert.Empty(SidecarProblems("thing_health", """{"network_id": {"reference_id": "811410", "port": 1}, "damaged_only": true}"""));
        Assert.Empty(SidecarProblems("thing_health", """{"network_id": "900120", "kind": "cable", "limit": 50}"""));
    }
}
