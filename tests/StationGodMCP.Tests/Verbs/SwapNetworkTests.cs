#nullable enable

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// swap cable, pipe and chute without the game: a call names its pieces one way, by id, network or (chute) box; two
/// ways is a problem and none is missing_target.
/// </summary>
public sealed class SwapNetworkTests
{
    private static Selector Chute(string json) =>
        SwapChuteOptions.Bind(new OptionReader(SwapChuteOptions.Spec, JObject.Parse(json))).Selector;

    [Theory]
    [InlineData("""{"id": ["1", "2"]}""", "id")]
    [InlineData("""{"network": "6100100"}""", "network")]
    [InlineData("""{"network": {"id": "7", "port": "0"}}""", "network")]
    [InlineData("""{"box": [[0, 0, 0], [4, 4, 4]]}""", "box")]
    public void AChuteIsNamedByIdNetworkOrBox(string json, string form)
    {
        List<OptionProblem> problems = new();

        Assert.Equal(form, NetworkSwapOrders.Form(Chute(json), true, "swap chute", "chute", problems, out _));
        Assert.Empty(problems);
    }

    [Fact]
    public void TwoWaysIsAProblemAndNoneIsMissingTarget()
    {
        List<OptionProblem> problems = new();

        Assert.Null(NetworkSwapOrders.Form(Chute("""{"id": "1", "box": [[0, 0, 0], [1, 1, 1]]}"""), true,
            "swap chute", "chute", problems, out VerbError? missing));
        Assert.Null(missing);
        Assert.Equal("box", Assert.Single(problems).Path);

        problems.Clear();
        Assert.Null(NetworkSwapOrders.Form(Chute("{}"), true, "swap chute", "chute", problems, out missing));
        Assert.Empty(problems);
        Assert.Equal("missing_target", missing!.Code);
        Assert.Equal(new[] { "id", "network", "box" }, missing.Data!["options"]!.Select(option => (string)option!));
    }

    [Fact]
    public void ABoxIsNotAWayWhereTheWhatTakesNone()
    {
        List<OptionProblem> problems = new();

        Assert.Null(NetworkSwapOrders.Form(Chute("""{"box": [[0, 0, 0], [1, 1, 1]]}"""), false, "swap cable", "cable",
            problems, out VerbError? missing));
        Assert.Equal(new[] { "id", "network" }, missing!.Data!["options"]!.Select(option => (string)option!));
    }

    // ---- swap cable and pipe: into or tidy ----

    private static List<string>? Tidy(string json, List<OptionProblem> problems)
    {
        SwapCableOptions options = SwapCableOptions.Bind(new OptionReader(SwapCableOptions.Spec, JObject.Parse(json)));
        return NetworkSwapOrders.Tidy(options.Tidy, options.Into,
            new TidyLimits(options.Spare != null, options.Only != null, options.OlderThan != null, options.Root != null),
            problems);
    }

    [Theory]
    [InlineData("""{"network": "1"}""")]
    [InlineData("""{"network": "1", "into": "super_heavy"}""")]
    [InlineData("""{"network": "1", "tidy": false}""")]
    public void IntoOrNoTidyIsTheUpgrade(string json)
    {
        List<OptionProblem> problems = new();

        Assert.Null(Tidy(json, problems));
        Assert.Empty(problems);
    }

    [Fact]
    public void TidyTrueIsSimplifyJunctionsAndAListRunsInItsOrder()
    {
        List<OptionProblem> problems = new();

        Assert.Equal(new[] { "simplify_junctions" }, Tidy("""{"network": "1", "tidy": true}""", problems));
        Assert.Equal(new[] { "remove_dead_ends", "remove_redundant" },
            Tidy("""{"network": "1", "tidy": ["remove_redundant", "remove_dead_ends"], "only": ["5"], "root": "9"}""",
                problems));
        Assert.Empty(problems);
    }

    [Fact]
    public void EveryTidyProblemIsListedAtOnce()
    {
        List<OptionProblem> problems = new();

        Tidy("""{"network": "1", "tidy": ["simplify_junctions"], "spare": ["2"], "only": ["3"], "older_than": "4"}""",
            problems);
        Assert.Equal(new[] { "spare", "only", "older_than" }, problems.Select(problem => problem.Path));

        problems.Clear();
        Tidy("""{"network": "1", "into": "heavy", "tidy": true}""", problems);
        Assert.Equal("tidy", Assert.Single(problems).Path);

        problems.Clear();
        Tidy("""{"network": "1", "tidy": ["split_long_straights", "merge_straights"]}""", problems);
        Assert.Contains("undo each other", Assert.Single(problems).Problem);

        problems.Clear();
        Tidy("""{"network": "1", "tidy": []}""", problems);
        Assert.StartsWith("names no operation", Assert.Single(problems).Problem);

        problems.Clear();
        Tidy("""{"network": "1", "spare": ["2"]}""", problems);
        Assert.Equal("spare", Assert.Single(problems).Path);
    }
}
