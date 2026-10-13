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
}
