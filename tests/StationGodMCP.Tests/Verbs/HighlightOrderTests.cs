#nullable enable

using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// show highlight without the game: the three target forms (id one and a list, network, at), each target exactly one
/// of them, the palette colours in turn, color, label (trimmed, blank as none) and pulse, the colour array, the caps
/// (64 targets, 1024 ids, an 80-character label), a bad seconds, and the clear branch (targets, seconds and keep go
/// without it). The old highlight arguments' parse cases are these, in the catalogue's words.
/// </summary>
public sealed class HighlightOrderTests
{
    private static HighlightOrder? Order(string json, List<OptionProblem> problems)
    {
        OptionReader given = new OptionReader(ShowHighlightOptions.Spec, JObject.Parse(json));
        return HighlightOrder.Of(ShowHighlightOptions.Bind(given), given, problems);
    }

    private static HighlightOrder Drawn(string json) => Order(json, new List<OptionProblem>())!;

    [Fact]
    public void EveryTargetFormIsReadWithThePaletteInTurn()
    {
        HighlightOrder order = Drawn("""
            {"targets": [{"id": "1199811"}, {"id": ["5", "6"]}, {"network": "77"}, {"at": [700, 200, 650]}],
             "seconds": 120, "keep": true}
            """);

        Assert.False(order.Clear);
        HighlightRequest request = order.Request!;
        Assert.Equal(120, request.Seconds);
        Assert.True(request.Keep);
        Assert.Equal(4, request.Targets.Count);
        HighlightTarget.Things one = Assert.IsType<HighlightTarget.Things>(request.Targets[0]);
        Assert.Equal(1199811, Assert.Single(one.Ids).Value);
        Assert.Equal("cyan", one.ColorName);
        HighlightTarget.Things many = Assert.IsType<HighlightTarget.Things>(request.Targets[1]);
        Assert.Equal(5, (int)many.Ids[0].Value);
        Assert.Equal(6, (int)many.Ids[1].Value);
        Assert.Equal("magenta", many.ColorName);
        HighlightTarget.Network network = Assert.IsType<HighlightTarget.Network>(request.Targets[2]);
        Assert.Equal(77, network.Id.Value);
        Assert.Equal("yellow", network.ColorName);
        HighlightTarget.Point point = Assert.IsType<HighlightTarget.Point>(request.Targets[3]);
        Assert.Equal(new Vec3(700, 200, 650), point.At);
        Assert.Equal("green", point.ColorName);
    }

    [Fact]
    public void AColourLabelAndPulseAreRead()
    {
        HighlightOrder order = Drawn("""
            {"targets": [{"network": "77", "label": " Fuel ", "color": "RED", "pulse": true},
                         {"id": "9", "color": [0.5, 0, 1], "label": "   "}]}
            """);

        HighlightRequest request = order.Request!;
        HighlightTarget named = request.Targets[0];
        Assert.Equal("red", named.ColorName);
        Assert.Equal(1.0, named.Tint.Red);
        Assert.Equal("Fuel", named.Label);
        Assert.True(named.Pulse);
        HighlightTarget custom = request.Targets[1];
        Assert.Equal("custom", custom.ColorName);
        Assert.Equal(0.5, custom.Tint.Red);
        Assert.Equal(1.0, custom.Tint.Blue);
        Assert.Null(custom.Label);
        Assert.False(custom.Pulse);
    }

    [Theory]
    [InlineData("""{"targets": [{"id": "5", "at": [1, 2, 3]}]}""")]
    [InlineData("""{"targets": [{"label": "x"}]}""")]
    [InlineData("""{"targets": [{"id": "5", "network": "7"}]}""")]
    public void ATargetTakesExactlyOneForm(string json)
    {
        List<OptionProblem> problems = new();

        Assert.Null(Order(json, problems));

        OptionProblem problem = Assert.Single(problems);
        Assert.Equal("targets[0]", problem.Path);
        Assert.Equal("takes exactly one of id, network, at", problem.Problem);
    }

    [Theory]
    [InlineData("""{"targets": [{"id": "5", "color": "purple"}]}""", "targets[0].color")]
    [InlineData("""{"targets": [{"id": "5", "color": [2, 0, 0]}]}""", "targets[0].color")]
    [InlineData("""{"targets": [{"id": 5.5}]}""", "targets[0].id")]
    [InlineData("""{"targets": [{"id": ["5", "x"]}]}""", "targets[0].id[1]")]
    [InlineData("""{"targets": [{"network": "not an id"}]}""", "targets[0].network")]
    [InlineData("""{"targets": [{"at": [1, 2]}]}""", "targets[0].at")]
    [InlineData("""{"targets": [{"id": "5", "label": 3}]}""", "targets[0].label")]
    [InlineData("""{"targets": [{"id": "5", "pulse": "yes"}]}""", "targets[0].pulse")]
    [InlineData("""{"targets": [3]}""", "targets[0]")]
    [InlineData("""{"targets": []}""", "targets")]
    public void ATargetThatCannotBeReadIsAProblemAtItsPath(string json, string path)
    {
        List<OptionProblem> problems = new();

        Assert.Null(Order(json, problems));

        Assert.Equal(path, Assert.Single(problems).Path);
    }

    [Fact]
    public void ATargetKeyThatIsNoTargetKeyNamesTheNearest()
    {
        List<OptionProblem> problems = new();

        Assert.Null(Order("""{"targets": [{"id": "5", "colour": "red"}]}""", problems));

        OptionProblem problem = Assert.Single(problems);
        Assert.Equal("targets[0].colour", problem.Path);
        Assert.Equal("is not a target key", problem.Problem);
        Assert.Equal(new[] { "color" }, problem.Nearest);
    }

    [Fact]
    public void TheCapsHold()
    {
        List<OptionProblem> problems = new();

        Assert.Null(Order("{\"targets\": [" + Targets(65) + "]}", problems));
        OptionProblem over = Assert.Single(problems);
        Assert.Equal("targets", over.Path);
        Assert.Equal("is 1 to 64 targets, each {id, network or at}", over.Problem);

        problems.Clear();
        StringBuilder ids = new StringBuilder();
        for (int index = 0; index < HighlightOrder.MaximumIds + 1; index++)
        {
            ids.Append(index > 0 ? ", " : "").Append('"').Append(index).Append('"');
        }

        Assert.Null(Order("{\"targets\": [{\"id\": [" + ids + "]}]}", problems));
        Assert.Equal("targets[0].id", Assert.Single(problems).Path);

        problems.Clear();
        Assert.Null(Order("{\"targets\": [{\"id\": \"5\", \"label\": \"" + new string('l', 81) + "\"}]}", problems));
        OptionProblem tooLong = Assert.Single(problems);
        Assert.Equal("targets[0].label", tooLong.Path);
        Assert.Equal("is at most 80 characters", tooLong.Problem);

        problems.Clear();
        Assert.Null(Order("""{"targets": [{"id": {}}]}""", problems));
        Assert.Equal("targets[0].id", Assert.Single(problems).Path);
    }

    [Fact]
    public void TheSecondsCapHolds()
    {
        List<OptionProblem> problems = new();

        Assert.Null(Order("""{"targets": [{"id": "5"}], "seconds": 601}""", problems));
        OptionProblem over = Assert.Single(problems);
        Assert.Equal("seconds", over.Path);
        Assert.Equal("is at most 600", over.Problem);

        problems.Clear();
        Assert.Equal(600, Order("""{"targets": [{"id": "5"}], "seconds": 600}""", problems)!.Request!.Seconds);
        Assert.Empty(problems);
        Assert.Equal(0, Order("""{"targets": [{"id": "5"}], "seconds": 0}""", problems)!.Request!.Seconds);
        Assert.Equal(60, Order("""{"targets": [{"id": "5"}]}""", problems)!.Request!.Seconds);
        Assert.Empty(problems);    }

    [Fact]
    public void TheClearBranchKeepsOnlyWhatItRemoves()
    {
        List<OptionProblem> problems = new();

        HighlightOrder clear = Order("""{"clear": true}""", problems)!;

        Assert.Empty(problems);
        Assert.True(clear.Clear);
        Assert.Null(clear.Request);

        // targets, seconds and keep draw something, so they go without clear; seconds and keep are refuted even when
        // they restate their own defaults: only the reader says whether the call gave them.
        foreach (string json in new[]
                 {
                     """{"clear": true, "targets": [{"id": "5"}]}""",
                     """{"clear": true, "seconds": 60}""",
                     """{"clear": true, "keep": false}"""
                 })
        {
            problems.Clear();
            Assert.Null(Order(json, problems));
            Assert.Equal("goes without clear: clear only removes what is drawn", Assert.Single(problems).Problem);
        }

        problems.Clear();
        Assert.Null(Order("""{"clear": true, "targets": [{"id": "5"}], "seconds": 60, "keep": true}""", problems));
        Assert.Equal(new[] { "targets", "seconds", "keep" },
            new List<string> { problems[0].Path, problems[1].Path, problems[2].Path });
    }

    [Fact]
    public void TheWhatsLabelPrefixesEveryRefusal()
    {
        List<OptionProblem> problems = new();

        Assert.Null(Order("""{"targets": [{"id": "5", "colour": "red"}]}""", problems));

        VerbError refused = OptionProblem.Refusal(ShowHighlightOptions.Spec.Label, problems);
        Assert.Equal("invalid_argument", refused.Code);
        Assert.StartsWith("show highlight: targets[0].colour: is not a target key (nearest: color).", refused.Message);
    }

    private static string Targets(int count)
    {
        StringBuilder items = new StringBuilder();
        for (int index = 0; index < count; index++)
        {
            items.Append(index > 0 ? ", " : "").Append("{\"id\": \"").Append(index).Append("\"}");
        }

        return items.ToString();
    }
}
