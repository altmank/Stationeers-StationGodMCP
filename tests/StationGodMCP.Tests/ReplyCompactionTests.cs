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
using StationGodMCP.Pure.Verbs;
using StationGodMCP.Pure.Shaping;
using StationGodMCP.Server;
using StationGodMCP.Tests.CatalogueChecks;
using StationGodMCP.Tests.Sidecar;
using Xunit;
using ModCatalogue = StationGodMCP.Pure.Catalogue.Catalogue;

namespace StationGodMCP.Tests;

/// <summary>
/// The general means a default reply stays small: a method's x-default-limits cut its top-level lists unless the call
/// limits them itself (the sidecar's list_limits), and the per-tool compact forms: a brief job log, chute items with the
/// network devices, plants in short, grid_survey kinds, mod_info's runtime methods.
/// </summary>
public sealed class ReplyCompactionTests
{
    [Fact]
    public void DefaultLimitsApplyToACallWithoutAShape()
    {
        ShapeRequest shape = ShapeRequest.WithDefaultLimits(null, new Dictionary<string, int> { ["devices"] = 2 })!;

        Assert.Equal("""{"devices":[1,2],"count":3,"truncated":[{"list":"devices","returned":2,"total":3,"more":"pass list_limits {\"devices\": 3} (shape.limit on the pipe; max 100000)"}]}""",
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
    public async Task ListLimitsIsSentAsShapeLimit()
    {
        await using FakeGame game = FakeGame.OnPipe();
        game.Answer = call => Task.FromResult<string?>(call.Ok("""{"devices":[]}""", shaped: true));

        await Program.HandleMcpMessageAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"find","arguments":{"what":"device","list_limits":{"devices":200}}}}""",
            new Program.GameTransportSettings(game.Target));

        FakeCall forwarded = Assert.Single(game.CallsTo("find"));
        Assert.Equal("""{"what":"device"}""", forwarded.Params.GetRawText());
        Assert.Equal("""{"limit":{"devices":200}}""", forwarded.Shape!.Value.GetRawText());
        Assert.True(Program.InputSchemas["list_devices"].GetProperty("properties").TryGetProperty("list_limits", out _));
    }

    [Fact]
    public void NoMethodTakesAnArgumentTheSidecarTakesForItself()
    {
        foreach (JsonElement method in StationGodMCP.Client.GameCatalogue.BuiltIn.Document.GetProperty("methods").EnumerateArray())
        {
            JsonElement properties = method.GetProperty("params").GetProperty("properties");
            foreach (string name in SidecarArguments.Names)
            {
                Assert.False(properties.TryGetProperty(name, out _),
                    $"{method.GetProperty("name").GetString()} takes '{name}', which the sidecar would take out.");
            }
        }
    }

    [Fact]
    public async Task RocketForecastsOwnLimitsReachTheGame()
    {
        await using FakeGame game = FakeGame.OnPipe();
        game.Answer = call => Task.FromResult<string?>(call.Ok("""{"legs":[]}""", shaped: false));

        await Program.HandleMcpMessageAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"plan","arguments":{"what":"flight","node":"pad","limits":true}}}""",
            new Program.GameTransportSettings(game.Target));

        FakeCall forwarded = Assert.Single(game.CallsTo("plan"));
        Assert.True(forwarded.Params.GetProperty("limits").GetBoolean());
        Assert.Null(forwarded.Shape);
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
        PlantView plant = Tomato();

        JObject brief = JObject.Parse(WireCheck.New(new PlantBriefView(plant)));

        Assert.Equal(new[]
        {
            "reference_id", "prefab_name", "display_name", "planted", "tray", "stage", "stage_count", "maturity_ratio",
            "ready_to_harvest", "dead", "health_percent", "problems", "harvest_in_s"
        }, brief.Properties().Select(property => property.Name));
        Assert.True(WireCheck.New(new PlantBriefView(plant)).Length * 3 < WireCheck.New(plant).Length);
    }

    [Theory]
    [InlineData("""{"kind":["chute"]}""", "chute", true)]
    [InlineData("""{"kind":["chute"]}""", "cable", false)]
    [InlineData("""{"kind":["Pipe"," cable "]}""", "pipe", true)]
    [InlineData("""{}""", "cable", true)]
    public void KindsKeepPiecesOfTheKindsNamed(string options, string kind, bool kept)
    {
        Assert.Equal(kept, KindsOf(options).AdmitsPiece(kind));
    }

    [Fact]
    public void KindsKeepDevicesWithAPortOfAKindNamed()
    {
        SurveyKinds chutes = KindsOf("""{"kind":["chute"]}""");

        Assert.True(chutes.AdmitsDevice(new string?[] { "PowerAndData", "Chute" }));
        Assert.False(chutes.AdmitsDevice(new string?[] { "PowerAndData", "Pipe" }));
        Assert.True(SurveyKinds.Every.AdmitsDevice(new string?[0]));
        Assert.True(KindsOf("""{"kind":["cable"]}""").AdmitsDevice(new string?[] { "Data" }));
    }

    [Fact]
    public void AnUnknownKindIsRefused()
    {
        VerbError? refused = Pure.Verbs.OptionCheck.Check(VerbSurface.Verb("survey")!,
            JObject.Parse("""{"what":"area","room":"28","kind":["wire"]}"""), out _);

        Assert.Equal("invalid_argument", refused?.Code);
        Assert.Contains("kind", refused!.Message);
    }

    // survey area's kind as its handler reads it.
    private static SurveyKinds KindsOf(string options) =>
        SurveyKinds.Of(SurveyAreaOptions.Bind(new OptionReader(SurveyAreaOptions.Spec, JObject.Parse(options))).Kind);

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

    // A planted tomato in full, as find plant reads one.
    private static PlantView Tomato()
    {
        PlantIdentity identity = new PlantIdentity(new ThingView(new ThingId(70), "SeedBag_Tomato", "Tomato_Tray 1"),
            "Tomato", true,
            new PlantTrayView(new ThingView(new ThingId(71), "StructureHydroponicsTray", "Tray 1"), true));
        PlantGrowth growth = new PlantGrowth(
            new PlantStageView(2, 5, new StageMarks(3, 4, -1, 3, 4), 12.5f, 60f),
            new List<StageView> { new StageView(0, 1f, false, false, false) }, 0.5, 0.0,
            new PlantPhase(false, false, false, false, false));
        PlantCare care = new PlantCare(
            new PlantEfficiencyView(0.9f, 90, new PlantFactors(1f, 1f, 0.9f, 1f, 1f, 1.01f), 1f),
            new PlantStates(new List<string> { "dry" }, new List<string> { "dry", "in light" }),
            new List<PlantConditionView> { new PlantConditionView("Dehydrated", "dry", true, 30f, 120f, false) },
            false,
            new PlantHealthView(0.25, 75, 25f, 100f, new DamagePartsView(0f, 0f, 0f, 25f, 0f, 0f, 0f, 0f, 0f)));
        PlantSupply supply = new PlantSupply(
            new PlantHarvestView(new HarvestCounts(0, 3, 1, 0f), new HarvestForecastView(2.8, 1.0), 12f),
            new PlantFertiliserView(false, 1f, 0f,
                new WaitingFertiliserView(new ThingId(72), "Fertilizer", 1.5f, 1f, 2f)),
            new PlantLightView(0.8f, true, 0.5f, 0.5f, 0f),
            new PlantAirView(101f, 22f, 40.0,
                new Dictionary<string, float?> { ["oxygen"] = 0.2f, ["carbon_dioxide"] = null }),
            new PlantWaterView(100.0, 100.0, 20f),
            new PlantNeedsView("Tomato",
                new PlantBands(new BandView(new[] { 15f, 30f }, new[] { 5f, 40f }),
                    new BandView(new[] { 50f, 150f }, new[] { 20f, 200f })),
                new PlantGases(
                    new List<InhaledGasView> { new InhaledGasView("carbon dioxide", 0.01f, 0.01f, 0.02f) },
                    new List<ExhaledGasView> { new ExhaledGasView("oxygen", 0.01f) },
                    new List<HarmfulGasView> { new HarmfulGasView("pollutant", 1f, 0f) }),
                new PlantDaily(0.002f, 300f, 100f)));
        PlantHistory history = new PlantHistory(
            new PlantRecordView(500f, 30f, 0f, 0f, new PressureTimes(0f, 0f, 0f, 0f)),
            new SortedDictionary<string, float> { ["DarkPerDay"] = 0.1f, ["WaterUsage"] = -0.2f },
            new PlantForecastView(0.9f, 52.8, 100.0, 200.0, new RegrowTimes(null, null)));
        return new PlantView(identity, growth, care, supply, history);
    }
}
