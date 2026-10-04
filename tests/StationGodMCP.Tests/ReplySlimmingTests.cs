#nullable enable

using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Rockets;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// 1.9.0 slimmer replies on the mod's side: pipe clients held to each tool's argument names (a find_things with
/// prefab for prefab_contains answered 277 KB of everything), find_things location, connections' member filters,
/// brief confirmed runs (6-10 KB of preflight again) and network device lists as counts (~10 KB).
/// </summary>
public sealed class ReplySlimmingTests
{
    private static readonly DeclaredArguments Declared = new DeclaredArguments(Pure.Catalogue.Catalogue.Load(
        File.ReadAllText(CatalogueChecks.CatalogueFiles.AssembledPath)));

    [Fact]
    public void AnUnknownArgumentFromAPipeClientIsRefusedNamingTheOneMeant()
    {
        ApiException refused = Assert.Throws<ApiException>(() =>
            Declared.Check("find_things", JObject.Parse("""{"prefab":"ItemDirtyOre"}""")));

        Assert.Equal("invalid_argument", refused.Code);
        Assert.StartsWith("Unknown argument 'prefab'; did you mean 'prefab_contains'?", refused.Message);
        Assert.Contains("find_things takes: broken, has_atmosphere, kind,", refused.Message);
        Assert.EndsWith("Nothing was run.", refused.Message);
    }

    [Fact]
    public void EveryUnknownArgumentIsNamed()
    {
        ApiException refused = Assert.Throws<ApiException>(() =>
            Declared.Check("connections", JObject.Parse("""{"network_id":"5","kind":"chute","bogus":1,"prefab":"x"}""")));

        Assert.Contains("Unknown argument 'bogus'.", refused.Message);
        Assert.Contains("Unknown argument 'prefab'; did you mean 'prefab_contains'?", refused.Message);
    }

    [Fact]
    public void DeclaredAndNullArgumentsPass()
    {
        Declared.Check("find_things", JObject.Parse("""{"prefab_contains":"Ore","location":"ground","kind":null}"""));
        Declared.Check("game_clock", null);
        Declared.Check("not_a_listed_tool", JObject.Parse("""{"anything":1}"""));
    }

    [Fact]
    public void EveryToolOfTheSidecarIsListed()
    {
        Assert.Equal(Server.Program.InputSchemas.Count, Declared.ToolCount);
    }

    [Theory]
    [InlineData("prefab", "prefab_contains")]
    [InlineData("prefab_contain", "prefab_contains")]
    [InlineData("contains", "prefab_contains")]
    [InlineData("qty", null)]
    [InlineData("name", null)]
    public void TheNearestNameIsASlipOrOneWordMore(string given, string? expected)
    {
        Assert.Equal(expected, NearestName.Of(given, new[] { "prefab_contains", "limit", "quantity", "kind" }));
    }

    [Fact]
    public void AWordAddedToTwoNamesSuggestsNeither()
    {
        Assert.Null(NearestName.Of("name", new[] { "name_contains", "prefab_name" }));
    }

    [Theory]
    [InlineData(null, "any")]
    [InlineData(" Ground ", "ground")]
    [InlineData("built", "built")]
    [InlineData("world", "world")]
    public void ALocationIsReadAsFindThingsReportsIt(string? given, string expected)
    {
        Assert.Equal(expected, ThingLocations.Parse(given));
    }

    [Fact]
    public void AnUnknownLocationIsRefusedWithTheWordsItTakes()
    {
        ApiException refused = Assert.Throws<ApiException>(() => ThingLocations.Parse("floor"));

        Assert.Equal("invalid_argument", refused.Code);
        Assert.Contains("any, ground, player, stored, built, world", refused.Message);
    }

    [Fact]
    public void MemberFiltersAreOffUnlessAsked()
    {
        NetworkMemberFilter filter = NetworkMemberFilter.Parse(new Args(new JObject()));

        Assert.False(filter.OpenEndsOnly);
        Assert.True(filter.KeepsPrefab("StructureChuteStraight"));
        Assert.True(filter.KeepsPrefab(null));
    }

    [Fact]
    public void APrefabFilterMatchesAPartIgnoringCase()
    {
        NetworkMemberFilter filter = NetworkMemberFilter.Parse(
            new Args(JObject.Parse("""{"prefab_contains":" chutejunction ","open_ends_only":true}""")));

        Assert.True(filter.OpenEndsOnly);
        Assert.True(filter.KeepsPrefab("StructureChuteJunction"));
        Assert.False(filter.KeepsPrefab("StructureChuteStraight"));
        Assert.False(filter.KeepsPrefab(null));
    }

    [Fact]
    public void AMemberListsItsOpenEndsOnlyWhenAsked()
    {
        ThingView chute = new ThingView(new ThingId(7), "StructureChuteStraight", "Chute");
        PositionView at = new PositionView(1, 2, 3);

        Assert.Null(JObject.Parse(WireCheck.New(new NetworkMemberView(chute, "chute", at)))["open_ends"]);
        Assert.Equal(1, (int)JObject.Parse(WireCheck.New(
            new NetworkMemberView(chute, "chute", at, new List<int> { 1 })))["open_ends"]![0]!);
    }

    [Fact]
    public void AConfirmedRunAnswersItsJobAndThePreflightInShort()
    {
        RunJobView started = new RunJobView("run-5", "place_cables", "waiting", Report(), null);

        JObject json = JObject.Parse(WireCheck.New(JobStartReplies.Brief(started, JobPreflightSummaryView.Of(Report()))));

        Assert.Equal("run-5", (string?)json["job_id"]);
        Assert.Equal("waiting", (string?)json["status"]);
        Assert.Equal(JTokenType.Null, json["preflight"]!.Type);
        Assert.Equal(10, (int)json["preflight_summary"]!["placed"]!);
        Assert.Equal(2, (int)json["preflight_summary"]!["changed"]!);
        Assert.Equal(0, (int)json["preflight_summary"]!["removed"]!);
        Assert.Equal(new[] { "open_end", "through_air" }, json["preflight_summary"]!["warnings"]!.ToObject<string[]>());
    }

    [Fact]
    public void AQueuedRunLeavesItsPreflightOutToo()
    {
        JobQueuedView queued = new JobQueuedView("run-6", "place_cables", 2, "run-5", Report());

        JObject json = JObject.Parse(WireCheck.New(JobStartReplies.Brief(queued, JobPreflightSummaryView.Of(Report()))));

        Assert.Null(json["preflight"]);
        Assert.Equal("queued", (string?)json["status"]);
        Assert.Equal(2, (int)json["position"]!);
        Assert.NotNull(json["preflight_summary"]);
    }

    [Fact]
    public void ABusyReplyAndAPollAreUnchanged()
    {
        JobBusyView busy = new JobBusyView("place_cables", "run-5", 0, "busy");

        Assert.Same(busy, JobStartReplies.Brief(busy, JobPreflightSummaryView.Of(Report())));
        Assert.Null(JObject.Parse(WireCheck.New(RunJobView.Brief(
            new RunJobView("run-5", "place_cables", "waiting", Report(), null))))["preflight_summary"]);
    }

    [Fact]
    public void AStartedStructureJobCountsItsPlacements()
    {
        BuildJobView started = new BuildJobView("place-3", "place_structure", "waiting", new { big = true }, null);
        PlaceReportView report = new PlaceReportView(
            new BuildHeader("scheduled", null, new List<BuildIssueView>(),
                new List<BuildIssueView> { new BuildIssueView("network_piece", "m", 0) }, new List<string>()),
            new List<PlacementView>(), new List<BuildMaterialView>(), null, false);

        JObject json = JObject.Parse(WireCheck.New(JobStartReplies.Brief(started, JobPreflightSummaryView.Of(report))));

        Assert.Equal(JTokenType.Null, json["preflight"]!.Type);
        Assert.Equal(0, (int)json["preflight_summary"]!["placed"]!);
        Assert.Null(json["preflight_summary"]!["removed"]);
        Assert.Equal("network_piece", (string?)json["preflight_summary"]!["warnings"]![0]);
    }

    [Fact]
    public void ANetworkWithoutDevicesKeepsTheirCount()
    {
        RunCableNetworkView network = new RunCableNetworkView(new ThingId(5), 3,
            new CableNetworkRatings(100, 200, 5000, null, null), new List<ThingView> { Device(1), Device(2) });

        JObject full = JObject.Parse(WireCheck.New(network));
        JObject slim = JObject.Parse(WireCheck.New(network.WithoutDevices()));

        Assert.Equal(2, ((JArray)full["devices"]!).Count);
        Assert.Null(slim["devices"]);
        Assert.Equal(2, (int)slim["device_count"]!);
        Assert.Equal(3, (int)slim["cable_count"]!);
        Assert.NotNull(network.Devices);
    }

    [Fact]
    public void ANetworkAfterTheEditCountsItsDevicesOnceAndItsPorts()
    {
        RunNetworkAfterView after = new RunNetworkAfterView(0, new List<ThingId> { new ThingId(5) }, 4,
            new List<RunPortView>
            {
                new RunPortView(Device(1), 0, false, new ThingId(5)),
                new RunPortView(Device(1), 1, false, new ThingId(5)),
                new RunPortView(Device(2), 0, false, new ThingId(5))
            }, null);

        JObject slim = JObject.Parse(WireCheck.New(after.WithoutDevices()));

        Assert.Null(slim["devices"]);
        Assert.Equal(2, (int)slim["device_count"]!);
        Assert.Equal(3, (int)slim["port_count"]!);
        Assert.Equal(4, (int)slim["new_pieces"]!);
    }

    [Theory]
    [InlineData(1.0, 1.0, "terraforming_reloaded")]
    [InlineData(0.96, 0.96, "terraforming_reloaded")]
    public void TerraformingReloadedsRateIsUsedWhenItIsARate(double returned, double rate, string source)
    {
        CombustionRate used = CombustionRate.FromTerraforming(returned);

        Assert.Equal(rate, used.Rate);
        Assert.Equal(source, used.Source);
        Assert.Null(used.Note);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    [InlineData("1.0")]
    public void AnythingElseFallsBackToTheGamesRateWithANote(object? returned)
    {
        CombustionRate used = CombustionRate.FromTerraforming(returned);

        Assert.Equal(CombustionRate.GameRate, used.Rate);
        Assert.Equal("game", used.Source);
        Assert.NotNull(used.Note);
    }

    [Fact]
    public void TheCombustionRateIsReportedWithItsSource()
    {
        JObject json = JObject.Parse(WireCheck.New(new CombustionView(CombustionRate.Game)));

        Assert.Equal(0.96, (double)json["rate"]!, 6);
        Assert.Equal("game", (string?)json["source"]);
        Assert.Null(json["note"]);
    }

    private static ThingView Device(long id) => new ThingView(new ThingId(id), "StructureBattery", "Battery");

    private static RunReportView Report() => new RunReportView(
        new RunHeaderView("place_cables", "scheduled", null, "heavy", null),
        new RunCellsView(new List<RunCellView>(), 12, 10, 2, 0, new List<RunRemovalView>(), 0),
        new RunMaterialsView(null, new List<UpgradeCoilView>(), true, new List<UpgradeAmountView>()),
        new RunNetworksView(new List<object>(), new List<RunNetworkAfterView>(), new List<RunBridgeView>(),
            new List<RunSplitView>(), null),
        new List<RunIssueView>(),
        new List<RunIssueView>
        {
            new RunIssueView("open_end", "a", null, null),
            new RunIssueView("open_end", "b", null, null),
            new RunIssueView("through_air", "c", null, null)
        });
}
