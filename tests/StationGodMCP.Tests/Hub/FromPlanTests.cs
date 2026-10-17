#nullable enable

using System.Linq;
using System.Text.Json.Nodes;
using StationGodMCP.Client;
using StationGodMCP.Server;
using Xunit;
using static StationGodMCP.Tests.Hub.Verbs;

namespace StationGodMCP.Tests.Hub;

/// <summary>
/// build's from_plan: a plan route or plan spot ref (or, from a script, the plan reply or one spot) builds that plan's
/// build_arguments; what may be left out, the options given beside it replace the plan's, and a run named beside it
/// replaces the plan's run. Every plan reply carries a ref.
/// </summary>
public sealed class FromPlanTests
{
    private const string RoutePlan =
        """{"found":true,"build_arguments":{"what":"cable","waypoints":[[1,2,3],[1,2,5]],"grade":"normal","join":"ends"},"notes":[]}""";

    private const string SpotPlan =
        """{"prefab_name":"StructureWallLight","spots":[""" +
        """{"position":{"x":1,"y":2,"z":3},"build_arguments":{"what":"structure","prefab":"StructureWallLight","at":[1,2,3],"facing":"+x"}},""" +
        """{"position":{"x":4,"y":5,"z":6},"build_arguments":{"what":"structure","prefab":"StructureWallLight","at":[4,5,6],"facing":"-z"}}""" +
        """],"tried":2}""";

    // A link whose plan calls answer plan; every other call answers {}.
    private static FakeGameLink Planning(string plan) =>
        new() { Answer = call => FakeGameLink.Ok(call.Method == "plan" ? plan : "{}") };

    private static string Planned(VerbPipeline pipeline, string what) =>
        (string)pipeline.Call("plan", what, what == "route" ? """{"kind":"cable"}""" : """{"prefab":"StructureWallLight"}""")
            .Done()!["ref"]!;

    // The build options the mod got: what and the options, without the wire's own keys.
    private static JsonObject BuildSent(FakeGameLink game)
    {
        JsonObject sent = game.Single("build").Params();
        sent.Remove("confirm");
        return sent;
    }

    [Fact]
    public void APlanReplyAlwaysCarriesARef()
    {
        FakeGameLink game = Planning(RoutePlan);

        Assert.StartsWith("r", Planned(game.Pipeline(), "route"));
    }

    [Fact]
    public void FromPlanAloneBuildsTheRoutesBuildArguments()
    {
        FakeGameLink game = Planning(RoutePlan);
        VerbPipeline pipeline = game.Pipeline();
        string plan = Planned(pipeline, "route");

        pipeline.Call("build", null, $$"""{"from_plan":"{{plan}}","confirm":true}""").Done();

        Same("""{"what":"cable","waypoints":[[1,2,3],[1,2,5]],"grade":"normal","join":"ends"}""", BuildSent(game));
        Assert.True((bool)game.Single("build").Params()["confirm"]!);
    }

    [Fact]
    public void FromPlanWithItsOwnWhatBuildsTheSame()
    {
        FakeGameLink game = Planning(RoutePlan);
        VerbPipeline pipeline = game.Pipeline();
        string plan = Planned(pipeline, "route");

        pipeline.Call("build", "cable", $$"""{"from_plan":"{{plan}}","confirm":true}""").Done();

        Same("""{"what":"cable","waypoints":[[1,2,3],[1,2,5]],"grade":"normal","join":"ends"}""", BuildSent(game));
    }

    [Fact]
    public void OptionsGivenBesideItReplaceThePlans()
    {
        FakeGameLink game = Planning(RoutePlan);
        VerbPipeline pipeline = game.Pipeline();
        string plan = Planned(pipeline, "route");

        pipeline.Call("build", null, $$"""{"from_plan":"{{plan}}","grade":"heavy","split_long":true,"confirm":true}""").Done();

        Same("""{"what":"cable","waypoints":[[1,2,3],[1,2,5]],"grade":"heavy","join":"ends","split_long":true}""", BuildSent(game));
    }

    [Fact]
    public void ARunGivenBesideItReplacesThePlansRun()
    {
        FakeGameLink game = Planning(RoutePlan);
        VerbPipeline pipeline = game.Pipeline();
        string plan = Planned(pipeline, "route");

        pipeline.Call("build", null, $$"""{"from_plan":"{{plan}}","cells":[[0,0,0],[0,0,1]],"confirm":true}""").Done();

        Same("""{"what":"cable","cells":[[0,0,0],[0,0,1]],"grade":"normal","join":"ends"}""", BuildSent(game));
    }

    [Fact]
    public void AReportFromAPlanActsOnRepeatWithConfirm()
    {
        FakeGameLink game = Planning(RoutePlan);
        game.Answer = call => FakeGameLink.Ok(call.Method == "plan" ? RoutePlan
            : (bool?)call.Params()["confirm"] == true ? "{}" : """{"report_only":true,"targets":[]}""");
        VerbPipeline pipeline = game.Pipeline();
        string plan = Planned(pipeline, "route");

        string report = (string)pipeline.Call("build", null, $$"""{"from_plan":"{{plan}}"}""").Done()!["ref"]!;
        pipeline.Call("build", null, $$"""{"repeat":"{{report}}","confirm":true}""").Done();

        JsonObject acted = game.CallsTo("build").Last().Params();
        Assert.True((bool)acted["confirm"]!);
        acted.Remove("confirm");
        Same("""{"what":"cable","waypoints":[[1,2,3],[1,2,5]],"grade":"normal","join":"ends"}""", acted);
    }

    [Theory]
    [InlineData("", "[1,2,3]")]
    [InlineData(".spots.0", "[1,2,3]")]
    [InlineData(".spots.1", "[4,5,6]")]
    public void ASpotPlanBuildsItsFirstSpotOrTheOneNamed(string path, string at)
    {
        FakeGameLink game = Planning(SpotPlan);
        VerbPipeline pipeline = game.Pipeline();
        string plan = Planned(pipeline, "spot");

        pipeline.Call("build", null, $$"""{"from_plan":"{{plan}}{{path}}","confirm":true}""").Done();

        Same(at, BuildSent(game)["at"]);
        Assert.Equal("structure", (string)BuildSent(game)["what"]!);
    }

    [Fact]
    public void AScriptGivesThePlanReplyOrOneSpot()
    {
        FakeGameLink game = Planning("{}");
        VerbPipeline pipeline = game.Pipeline();
        JsonObject spots = JsonNode.Parse(SpotPlan)!.AsObject();

        pipeline.Call("build", null, $$"""{"from_plan":{{RoutePlan}}}""", Face.Script).Done();
        pipeline.Call("build", null, $$"""{"from_plan":{{spots["spots"]![1]!.ToJsonString()}}}""", Face.Script).Done();

        JsonObject[] sent = game.CallsTo("build").Select(call => call.Params()).ToArray();
        Assert.Equal("cable", (string)sent[0]["what"]!);
        Same("[[1,2,3],[1,2,5]]", sent[0]["waypoints"]);
        Assert.Equal("structure", (string)sent[1]["what"]!);
        Same("[4,5,6]", sent[1]["at"]);
        Assert.All(sent, options => Assert.Null(options["from_plan"]));
    }

    [Fact]
    public void AnUnknownRefIsRefused()
    {
        FakeGameLink game = Planning(RoutePlan);

        VerbReply.Failed failed = game.Pipeline().Call("build", null, """{"from_plan":"r99"}""").Failed();

        Assert.Equal(["from_plan: no ref 'r99' (refs last until the world changes)"], failed.Problems());
        Assert.Empty(game.CallsTo("build"));
    }

    [Fact]
    public void ARefOfAnotherCallIsRefused()
    {
        FakeGameLink game = new() { Answer = _ => FakeGameLink.Ok("""{"things":[{"reference_id":"1"},{"reference_id":"2"},{"reference_id":"3"}]}""") };
        VerbPipeline pipeline = game.Pipeline();
        string found = (string)pipeline.Call("find", "thing").Done()!["ref"]!;

        VerbReply.Failed failed = pipeline.Call("build", null, $$"""{"from_plan":"{{found}}"}""").Failed();

        Assert.Equal([$"from_plan: {found} is a find thing call; from_plan takes a plan route or plan spot ref"],
            failed.Problems());
    }

    [Fact]
    public void APlanThatFoundNothingIsRefused()
    {
        FakeGameLink game = Planning("""{"found":false,"failure":"no_route: blocked","notes":[]}""");
        VerbPipeline pipeline = game.Pipeline();
        string plan = Planned(pipeline, "route");

        VerbReply.Failed failed = pipeline.Call("build", null, $$"""{"from_plan":"{{plan}}"}""").Failed();

        Assert.Equal([$"from_plan: {plan} has no build_arguments (the plan found nothing to build)"], failed.Problems());
    }

    [Fact]
    public void AWhatOtherThanThePlansIsRefused()
    {
        FakeGameLink game = Planning(RoutePlan);
        VerbPipeline pipeline = game.Pipeline();
        string plan = Planned(pipeline, "route");

        VerbReply.Failed failed = pipeline.Call("build", "pipe", $$"""{"from_plan":"{{plan}}"}""").Failed();

        Assert.Equal(["from_plan: the plan builds cable: leave what out or give what: cable"], failed.Problems());
    }
}
