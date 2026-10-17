#nullable enable

using System.Linq;
using System.Text.Json.Nodes;
using StationGodMCP.Client;
using StationGodMCP.Server;
using Xunit;
using static StationGodMCP.Tests.Hub.Verbs;

namespace StationGodMCP.Tests.Hub;

/// <summary>
/// A plan's ref holds its build_arguments whatever the plan call's shaping showed: fields, omit, list_limits, the
/// 12 KB cut and output_file change only what the caller sees, and build's from_plan builds the whole plan.
/// </summary>
public sealed class FromPlanShapingTests
{
    private const string Arguments = """{"what":"cable","waypoints":[[1,2,3],[1,2,5]],"grade":"normal","join":"ends"}""";

    private static readonly string RoutePlan =
        $$"""{"found":true,"route":{"length":2},"build_arguments":{{Arguments}},"notes":["a","b"]}""";

    private const string SpotPlan =
        """{"prefab_name":"StructureWallLight","spots":[""" +
        """{"position":{"x":1,"y":2,"z":3},"build_arguments":{"what":"structure","prefab":"StructureWallLight","at":[1,2,3]}},""" +
        """{"position":{"x":4,"y":5,"z":6},"build_arguments":{"what":"structure","prefab":"StructureWallLight","at":[4,5,6]}}""" +
        """],"tried":2}""";

    // The mod's shaping as far as these tests need it: fields keeps the top-level keys a selector starts with and, in
    // each top-level list's entries, the single names; omit leaves a single name out anywhere; limit cuts top lists.
    private static JsonNode Shaped(string reply, GameCall call)
    {
        JsonObject shaped = JsonNode.Parse(reply)!.AsObject();
        JsonObject? shape = call.Shape is { } given ? JsonNode.Parse(given.GetRawText())!.AsObject() : null;
        if (shape?["fields"] is JsonArray fields)
        {
            string[] selectors = fields.Select(field => (string)field!).ToArray();
            foreach (string key in shaped.Select(pair => pair.Key).ToList())
            {
                if (selectors.Any(selector => selector.Split('.')[0] == key))
                {
                    continue;
                }

                if (shaped[key] is JsonArray entries)
                {
                    foreach (JsonObject entry in entries.OfType<JsonObject>())
                    {
                        foreach (string inner in entry.Select(pair => pair.Key).Where(inner => !selectors.Contains(inner)).ToList())
                        {
                            entry.Remove(inner);
                        }
                    }

                    if (entries.All(entry => entry is not JsonObject { Count: > 0 }))
                    {
                        shaped.Remove(key);
                    }
                }
                else
                {
                    shaped.Remove(key);
                }
            }
        }

        foreach (string omitted in (shape?["omit"] as JsonArray ?? []).Select(name => (string)name!))
        {
            shaped.Remove(omitted);
            foreach (JsonObject entry in shaped.Select(pair => pair.Value).OfType<JsonArray>().SelectMany(list => list.OfType<JsonObject>()))
            {
                entry.Remove(omitted);
            }
        }

        foreach ((string list, JsonNode? limit) in shape?["limit"] as JsonObject ?? [])
        {
            if (shaped[list] is JsonArray entries)
            {
                while (entries.Count > (int)limit!)
                {
                    entries.RemoveAt(entries.Count - 1);
                }
            }
        }

        return shaped;
    }

    private static FakeGameLink Planning(string plan) =>
        new() { Answer = call => FakeGameLink.Ok(call.Method == "plan" ? Shaped(plan, call).ToJsonString() : "{}") };

    private static JsonNode Plan(VerbPipeline pipeline, string what, string shaping)
    {
        JsonObject options = Options(shaping);
        options[what == "route" ? "kind" : "prefab"] = what == "route" ? "cable" : "StructureWallLight";
        return pipeline.Call("plan", what, options.ToJsonString()).Done()!;
    }

    private static JsonObject Built(FakeGameLink game, VerbPipeline pipeline, string fromPlan)
    {
        pipeline.Call("build", null, $$"""{"from_plan":"{{fromPlan}}","confirm":true}""").Done();
        JsonObject sent = game.CallsTo("build").Last().Params();
        sent.Remove("confirm");
        return sent;
    }

    [Theory]
    [InlineData("""{"fields":["found","route.length"]}""")]
    [InlineData("""{"omit":["build_arguments","notes"]}""")]
    [InlineData("""{"list_limits":{"notes":1}}""")]
    [InlineData("""{"output_file":"plan.json"}""")]
    public void AShapedRoutePlansRefBuildsTheWholeRoute(string shaping)
    {
        FakeGameLink game = Planning(RoutePlan);
        VerbPipeline pipeline = game.Pipeline();

        JsonNode shown = Plan(pipeline, "route", shaping);

        Same(Arguments, Built(game, pipeline, (string)shown["ref"]!));
    }

    [Fact]
    public void FieldsShowOnlyWhatTheyAskThoughTheRefKeepsTheArguments()
    {
        FakeGameLink game = Planning(RoutePlan);

        JsonNode shown = Plan(game.Pipeline(), "route", """{"fields":["found","route.length"]}""");

        Assert.Equal(["found", "route", "ref"], shown.AsObject().Select(pair => pair.Key));
        Assert.Contains("build_arguments", (string)JsonNode.Parse(game.Single("plan").Shape!.Value.GetRawText())!["fields"]![2]!);
    }

    [Fact]
    public void AnOmittedBuildArgumentsIsLeftOutOfTheReplyNotOutOfTheRef()
    {
        FakeGameLink game = Planning(RoutePlan);

        JsonNode shown = Plan(game.Pipeline(), "route", """{"omit":["build_arguments","notes"]}""");

        Assert.Null(shown["build_arguments"]);
        Assert.Null(shown["notes"]);
        Same("""["notes"]""", JsonNode.Parse(game.Single("plan").Shape!.Value.GetRawText())!["omit"]);
    }

    [Fact]
    public void ACutRoutePlansRefBuildsTheWholeRoute()
    {
        string large = $$"""{"found":true,"build_arguments":{{Arguments}},"notes":[{{string.Join(",", Enumerable.Repeat("\"" + new string('x', 200) + "\"", 100))}}]}""";
        FakeGameLink game = Planning(large);
        VerbPipeline pipeline = game.Pipeline();

        JsonNode shown = Plan(pipeline, "route", "{}");

        Assert.NotNull(shown["cut"]);
        Same(Arguments, Built(game, pipeline, (string)shown["ref"]!));
    }

    [Theory]
    [InlineData("""{"fields":["position"]}""", true)]
    [InlineData("""{"fields":["prefab_name"]}""", false)]
    public void ASpotPlanShapedByFieldsBuildsTheSpotNamed(string shaping, bool spotsShown)
    {
        FakeGameLink game = Planning(SpotPlan);
        VerbPipeline pipeline = game.Pipeline();

        JsonNode shown = Plan(pipeline, "spot", shaping);

        Assert.Equal(spotsShown, shown["spots"] != null);
        Assert.True(shown["spots"]?.AsArray().All(spot => spot!["build_arguments"] == null) ?? true);
        Same("[4,5,6]", Built(game, pipeline, (string)shown["ref"]! + ".spots.1")["at"]);
    }
}
