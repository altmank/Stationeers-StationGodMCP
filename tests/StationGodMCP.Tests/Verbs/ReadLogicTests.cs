#nullable enable

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.DeviceReads;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// read logic's request (LogicReadRequest): its three forms (one value; pairs, one row each, as the hub's reads send
/// them; items, one row per entry with slots, atmosphere, reagents, a logic list or the clock), keys inside reads,
/// names kept as sent, and every bound, each problem in verb names and all of them at once.
/// </summary>
public sealed class ReadLogicTests
{
    private static LogicReadRequest? Parse(string options, out List<OptionProblem> problems)
    {
        problems = new List<OptionProblem>();
        ReadLogicOptions bound = ReadLogicOptions.Bind(new OptionReader(ReadLogicOptions.Spec, JObject.Parse(options)));
        return LogicReadRequest.Of(bound, problems);
    }

    private static T Parsed<T>(string options) where T : LogicReadRequest
    {
        LogicReadRequest? request = Parse(options, out List<OptionProblem> problems);
        Assert.Empty(problems);
        return Assert.IsType<T>(request);
    }

    private static string Refused(string options)
    {
        Assert.Null(Parse(options, out List<OptionProblem> problems));
        Assert.NotEmpty(problems);
        return OptionProblem.Refusal(ReadLogicOptions.Spec.Label, problems).Message;
    }

    // ---- the three forms ----

    [Fact]
    public void OneIdAndOneLogicTypeIsOneValue()
    {
        LogicReadRequest.One one = Parsed<LogicReadRequest.One>("""{"id": "5", "logic": "Temperature"}""");

        Assert.Equal("5", one.Pair.Id);
        Assert.Equal("Temperature", (string?)one.Pair.Logic);
    }

    [Fact]
    public void IdAndLogicListsAreEveryIdWithEveryLogicType()
    {
        LogicReadRequest.Pairs pairs = Parsed<LogicReadRequest.Pairs>("""{"id": ["1", "2"], "logic": ["On", 280]}""");

        Assert.Equal(new[] { "1:On", "1:280", "2:On", "2:280" },
            pairs.Entries.Select(pair => $"{pair.Id}:{pair.Logic}"));
        Assert.Single(Parsed<LogicReadRequest.Pairs>("""{"id": ["1"], "logic": "On"}""").Entries);
        Assert.Equal(2, Parsed<LogicReadRequest.Pairs>("""{"id": "1", "logic": ["On", "Power"]}""").Entries.Count);
    }

    [Fact]
    public void ReadsOfPairsAreOneRowEachAsTheHubSendsThem()
    {
        LogicReadRequest.Pairs pairs = Parsed<LogicReadRequest.Pairs>(
            """{"reads": [{"id": "1", "logic": "On"}, {"id": "looking_at", "logic": "Setting"}]}""");

        Assert.Equal(new[] { "1", "looking_at" }, pairs.Entries.Select(pair => pair.Id));
    }

    [Fact]
    public void ReadsWithALogicListSlotsAtmosphereOrReagentsAreItems()
    {
        LogicReadRequest.Items items = Parsed<LogicReadRequest.Items>("""
            {"reads": [{"id": "1", "logic": ["On", "Power"]}, {"id": "2", "logic": "On"},
                       {"id": "3", "slots": [{"index": 0}, {"index": 1, "logic": ["Occupied"]}]},
                       {"id": "4", "atmosphere": {"port": 1}, "reagents": true}, {"id": "5", "atmosphere": {"of": "internal"}}]}
            """);

        DeviceReadRequest request = items.Request;
        Assert.False(request.Clock);
        Assert.Equal(new[] { "1", "2", "3", "4", "5" }, request.Items.Select(item => item.Id));
        Assert.Equal(new[] { "On", "Power" }, request.Items[0].Logic!.Select(name => name.Key));
        Assert.Equal("On", Assert.Single(request.Items[1].Logic!).Key);
        Assert.Null(request.Items[2].Slots![0].Logic);
        Assert.Equal(1, Assert.IsType<AtmosphereTarget.AtPort>(request.Items[3].Atmosphere).Port);
        Assert.True(request.Items[3].Reagents);
        Assert.IsType<AtmosphereTarget.Internal>(request.Items[4].Atmosphere);
    }

    [Fact]
    public void AtmosphereTrueIsTheThingsOwnAtmosphere()
    {
        LogicReadRequest.Items items = Parsed<LogicReadRequest.Items>(
            """{"reads": [{"id": "1", "atmosphere": true}, {"id": "2", "logic": "On", "atmosphere": false}]}""");

        Assert.IsType<AtmosphereTarget.Own>(items.Request.Items[0].Atmosphere);
        Assert.Null(items.Request.Items[1].Atmosphere);
    }

    [Fact]
    public void IncludeClockMakesEveryFormItems()
    {
        Assert.True(Parsed<LogicReadRequest.Items>("""{"id": "1", "logic": "On", "include": ["clock"]}""").Request.Clock);
        Assert.True(Parsed<LogicReadRequest.Items>(
            """{"reads": [{"id": "1", "logic": "On"}], "include": ["clock"]}""").Request.Clock);
    }

    [Fact]
    public void KeysAreTheStringsSent()
    {
        DeviceReadItem item = Parsed<LogicReadRequest.Items>(
            """{"reads": [{"id": "1", "logic": ["pressure", "Pressure", "280", 281, " On "]}]}""").Request.Items[0];

        Assert.Equal(new[] { "pressure", "Pressure", "280", "281", " On " }, item.Logic!.Select(name => name.Key));
    }

    [Fact]
    public void ALogicOptionBesideReadsServesTheReadsWithoutTheirOwn()
    {
        LogicReadRequest.Pairs pairs = Parsed<LogicReadRequest.Pairs>(
            """{"reads": [{"id": "1"}, {"id": "2", "logic": "Power"}], "logic": "On"}""");

        Assert.Equal(new[] { "On", "Power" }, pairs.Entries.Select(pair => (string?)pair.Logic));
    }

    // ---- refusals, all at once ----

    [Theory]
    [InlineData("""{"id": "1", "reads": [{"id": "1", "logic": "On"}]}""", "reads: goes without id")]
    [InlineData("""{"id": "1"}""", "logic: is needed with id")]
    [InlineData("""{}""", "id: is needed")]
    [InlineData("""{"reads": []}""", "reads: is needed")]
    [InlineData("""{"reads": [{"id": "1", "logic": ["On"], "reference_id": "1"}]}""", "reads[0].reference_id: not a key here")]
    [InlineData("""{"reads": [{"id": "1", "slots": [{"index": 0, "logic_type": "On"}]}]}""", "reads[0].slots[0].logic_type: not a key here")]
    [InlineData("""{"reads": [{"id": "1", "atmosphere": {"port": "Input"}}]}""", "reads[0].atmosphere.port: must be an integer")]
    [InlineData("""{"reads": [{"id": "1", "atmosphere": {"of": "pipe"}}]}""", "reads[0].atmosphere.of: must be \"internal\"")]
    [InlineData("""{"reads": [{"id": "1", "atmosphere": {"of": "internal", "port": 0}}]}""", "takes of or port, not both")]
    [InlineData("""{"reads": [{"id": "1", "atmosphere": "own"}]}""", "reads[0].atmosphere: must be {}")]
    [InlineData("""{"reads": [{"id": "1", "atmosphere": false}]}""", "reads[0]: asks for nothing")]
    [InlineData("""{"reads": [{"id": "1", "reagents": "yes"}]}""", "reads[0].reagents: must be true or false")]
    [InlineData("""{"reads": [{"logic": "On"}]}""", "reads[0].id: is needed")]
    [InlineData("""{"reads": [{"id": "1"}]}""", "reads[0]: asks for nothing")]
    [InlineData("""{"reads": [{"id": "1", "reagents": false}]}""", "reads[0]: asks for nothing")]
    [InlineData("""{"reads": ["1"]}""", "reads[0]: must be {id, logic")]
    [InlineData("""{"reads": [{"id": "1", "logic": []}]}""", "reads[0].logic: must be a logic type, or a list of 1 to 64")]
    [InlineData("""{"reads": [{"id": "1", "logic": [true]}]}""", "reads[0].logic[0]: must be a logic type's name or number")]
    [InlineData("""{"reads": [{"id": "1", "logic": ["On", "On"]}]}""", "names 'On' twice")]
    [InlineData("""{"reads": [{"id": "1", "slots": [{"logic": ["On"]}]}]}""", "reads[0].slots[0].index: is needed")]
    [InlineData("""{"reads": [{"id": "1", "slots": [{"index": -1}]}]}""", "reads[0].slots[0].index: must be an integer")]
    [InlineData("""{"reads": [{"id": "1", "slots": [{"index": 0}, {"index": 0}]}]}""", "names slot 0 twice")]
    public void RefusesWhatReadLogicDoesNotTake(string options, string expected)
    {
        Assert.Contains(expected, Refused(options));
    }

    [Fact]
    public void EveryProblemComesAtOnce()
    {
        Assert.Null(Parse("""{"reads": [{"logic": "On"}, {"id": "2", "slots": [{"index": -1}], "colour": 1}]}""",
            out List<OptionProblem> problems));

        Assert.Equal(new[] { "reads[0].id", "reads[1].colour", "reads[1].slots[0].index" },
            problems.Select(problem => problem.Path).OrderBy(path => path, System.StringComparer.Ordinal));
    }

    // ---- bounds ----

    [Fact]
    public void PairsAreBoundedAt256()
    {
        string ids = string.Join(", ", Enumerable.Range(1, 129).Select(id => $"\"{id}\""));

        Assert.Equal(256, Parsed<LogicReadRequest.Pairs>(
            $$"""{"id": [{{string.Join(", ", Enumerable.Range(1, 128).Select(id => $"\"{id}\""))}}], "logic": ["On", "Power"]}""").Entries.Count);
        Assert.Contains("at most 256", Refused($$"""{"id": [{{ids}}], "logic": ["On", "Power"]}"""));
    }

    [Fact]
    public void ItemsAreBoundedAt128AndTheirValuesAt1024()
    {
        string many = string.Join(", ", Enumerable.Range(1, 129).Select(id => $$"""{"id": "{{id}}", "reagents": true}"""));
        string heavy = string.Join(", ", Enumerable.Range(1, 33).Select(id =>
            $$"""{"id": "{{id}}", "slots": [{"index": 0}]}"""));

        Assert.Contains("at most 128", Refused($$"""{"reads": [{{many}}]}"""));
        Assert.Contains("at most 1024", Refused($$"""{"reads": [{{heavy}}]}"""));
        Assert.Contains("must be an integer from 0 to 64",
            Refused("""{"reads": [{"id": "1", "atmosphere": {"port": 65}}]}"""));
    }
}
