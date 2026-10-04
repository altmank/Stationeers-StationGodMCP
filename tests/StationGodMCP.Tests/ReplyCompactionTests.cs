#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Catalogue;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Shaping;
using StationGodMCP.Server;
using StationGodMCP.Tests.CatalogueChecks;
using StationGodMCP.Tests.Sidecar;
using Xunit;
using ModCatalogue = StationGodMCP.Pure.Catalogue.Catalogue;

namespace StationGodMCP.Tests;

/// <summary>
/// The general means a default reply stays small: a method's x-default-limits cut its top-level lists unless the call
/// limits them itself (the sidecar's limits), and the per-tool compact forms: a brief job log, chute items with the
/// network devices, plants in short, grid_survey kinds, mod_info's runtime methods.
/// </summary>
public sealed class ReplyCompactionTests
{
    [Fact]
    public void DefaultLimitsApplyToACallWithoutAShape()
    {
        ShapeRequest shape = ShapeRequest.WithDefaultLimits(null, new Dictionary<string, int> { ["devices"] = 2 })!;

        Assert.Equal("""{"devices":[1,2],"count":3,"shape_truncated":{"devices":3}}""",
            ShapingChecks.Mod(ShapingChecks.Parse("""{"devices":[1,2,3],"count":3}"""), shape).Json);
    }

    [Fact]
    public void TheCallsOwnLimitWinsAndItsFieldsStay()
    {
        ShapeRequest given = ShapeRequest.Lenient(JObject.Parse("""{"fields":["a"],"limit":{"devices":5}}"""))!;

        ShapeRequest shape = ShapeRequest.WithDefaultLimits(given,
            new Dictionary<string, int> { ["devices"] = 1, ["pieces"] = 1 })!;

        Assert.Equal(5, shape.LimitOf("devices"));
        Assert.Equal(1, shape.LimitOf("pieces"));
        Assert.Same(given.Fields, shape.Fields);
    }

    [Fact]
    public void AMethodWithoutDefaultsKeepsTheShapeAsGiven()
    {
        Assert.Null(ShapeRequest.WithDefaultLimits(null, new Dictionary<string, int>()));
        ShapeRequest given = ShapeRequest.Lenient(JObject.Parse("""{"fields":["a"]}"""))!;
        Assert.Same(given, ShapeRequest.WithDefaultLimits(given, new Dictionary<string, int>()));
    }

    [Fact]
    public void TheCatalogueCarriesDefaultLimitsAndRefusesOneThatNamesNoList()
    {
        ModCatalogue catalogue = ModCatalogue.Load(System.IO.File.ReadAllText(CatalogueFiles.AssembledPath));
        Assert.True(catalogue.TryGet("list_devices", out CatalogueMethod? devices));
        Assert.Equal(25, devices!.DefaultLimits["devices"]);

        JsonObject broken = CatalogueFiles.ReadJson(CatalogueFiles.AssembledPath).AsObject();
        JsonObject method = broken["methods"]!.AsArray().First(entry => (string?)entry!["name"] == "list_devices")!.AsObject();
        method["x-default-limits"] = new JsonObject { ["gateway_id"] = 1 };

        CatalogueException refused = Assert.Throws<CatalogueException>(() => ModCatalogue.Load(broken.ToJsonString()));
        Assert.Contains("x-default-limits.gateway_id", refused.Message);
    }

    [Fact]
    public async Task LimitsIsSentAsShapeLimit()
    {
        await using FakeGame game = FakeGame.OnPipe();
        game.Answer = call => Task.FromResult<string?>(call.Ok("""{"devices":[]}""", shaped: true));

        await Program.HandleMcpMessageAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"list_devices","arguments":{"limits":{"devices":200}}}}""",
            new Program.GameTransportSettings(game.Target));

        FakeCall forwarded = Assert.Single(game.CallsTo("list_devices"));
        Assert.Equal("{}", forwarded.Params.GetRawText());
        Assert.Equal("""{"limit":{"devices":200}}""", forwarded.Shape!.Value.GetRawText());
        Assert.True(Program.InputSchemas["list_devices"].GetProperty("properties").TryGetProperty("limits", out _));
    }

    [Fact]
    public void ABriefJobLogCountsThePlacedPieces()
    {
        RunLogView log = new RunLogView();
        log.PlacedPieces.Add(new ThingView(new ThingId(7), "StructureCableSuperHeavyStraight", "Cable"));
        log.AddCreated("run", new ThingId(7));

        JObject full = JObject.Parse(WireCheck.New(log));
        JObject brief = JObject.Parse(WireCheck.New(log.Brief()));

        Assert.Single((JArray)full["placed"]!);
        Assert.Null(brief["placed"]);
        Assert.Equal(1, (int)brief["placed_count"]!);
        Assert.Equal("7", (string?)brief["created_ids"]![0]);
    }

    [Fact]
    public void ChuteItemsGoWithTheNetworkDevices()
    {
        RunChuteNetworkView network = new RunChuteNetworkView(new ThingId(5), 12, 1,
            new List<RunChuteItemView> { new RunChuteItemView(new ThingId(6), new ThingView(new ThingId(8), "ItemIronOre", "Iron Ore")) },
            new List<ThingView>());

        JObject brief = JObject.Parse(WireCheck.New(network.WithoutDevices()));

        Assert.Null(brief["items"]);
        Assert.Null(brief["devices"]);
        Assert.Equal(1, (int)brief["items_riding"]!);
        Assert.NotNull(JObject.Parse(WireCheck.New(network))["items"]);
    }

    [Fact]
    public void APlantInShortKeepsWhatAListNeeds()
    {
        PlantView plant = (PlantView)PlantsWireTests.NewShape().Plants[0];

        JObject brief = JObject.Parse(WireCheck.New(new PlantBriefView(plant)));

        Assert.Equal(new[]
        {
            "reference_id", "prefab_name", "display_name", "planted", "tray", "stage", "stage_count", "maturity_ratio",
            "ready_to_harvest", "dead", "health_percent", "problems", "harvest_in_s"
        }, brief.Properties().Select(property => property.Name));
        Assert.True(WireCheck.New(new PlantBriefView(plant)).Length * 3 < WireCheck.New(plant).Length);
    }

    [Theory]
    [InlineData("""{"kinds":["chute"]}""", "chute", true)]
    [InlineData("""{"kinds":["chute"]}""", "cable", false)]
    [InlineData("""{"kinds":["Pipe"," cable "]}""", "pipe", true)]
    [InlineData("""{}""", "cable", true)]
    public void KindsKeepPiecesOfTheKindsNamed(string arguments, string kind, bool kept)
    {
        Assert.Equal(kept, SurveyKinds.Parse(new Args(JObject.Parse(arguments))).AdmitsPiece(kind));
    }

    [Fact]
    public void KindsKeepDevicesWithAPortOfAKindNamed()
    {
        SurveyKinds chutes = SurveyKinds.Parse(new Args(JObject.Parse("""{"kinds":["chute"]}""")));

        Assert.True(chutes.AdmitsDevice(new string?[] { "PowerAndData", "Chute" }));
        Assert.False(chutes.AdmitsDevice(new string?[] { "PowerAndData", "Pipe" }));
        Assert.True(SurveyKinds.Every.AdmitsDevice(new string?[0]));
        Assert.True(SurveyKinds.Parse(new Args(JObject.Parse("""{"kinds":["cable"]}"""))).AdmitsDevice(new string?[] { "Data" }));
    }

    [Fact]
    public void AnUnknownKindIsRefused()
    {
        ApiException refused = Assert.Throws<ApiException>(() =>
            SurveyKinds.Parse(new Args(JObject.Parse("""{"kinds":["wire"]}"""))));

        Assert.Equal("invalid_argument", refused.Code);
        Assert.Contains("cable, pipe, chute", refused.Message);
    }

    [Fact]
    public void ModInfoListsTheCostliestMethodsAndCountsThemAll()
    {
        MethodTimings timings = new MethodTimings();
        for (int index = 0; index < ReplyDefaults.RuntimeMethods + 5; index++)
        {
            timings.Record("method_" + index, true, index, 0.0, 0.0);
        }

        JObject runtime = JObject.Parse(WireCheck.New(new RuntimeView(0.0, 0, FrameBudget.For(4.0, false),
            new DispatchStats().Snapshot(), new MemoryView(0, 0, 0, null, null), timings.Called(),
            new List<DriftCount>())));

        Assert.Equal(ReplyDefaults.RuntimeMethods, ((JArray)runtime["methods"]!).Count);
        Assert.Equal(ReplyDefaults.RuntimeMethods + 5, (int)runtime["method_count"]!);
    }
}
