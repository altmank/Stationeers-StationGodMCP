#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// server command and server console without the game: the command line (missing, blank, trimmed), the lines option
/// (the catalogue's default, a given number, and 1 to 500), confirm, and the what's label on a refusal.
/// </summary>
public sealed class ConsoleOrderTests
{
    private static ConsoleOrder? Command(string json, List<OptionProblem> problems) =>
        ConsoleOrder.Of(ServerCommandOptions.Bind(new OptionReader(ServerCommandOptions.Spec, JObject.Parse(json))),
            ReplyDefaults.ConsoleOutputLines, problems);

    private static int? Lines(string json, List<OptionProblem> problems) =>
        ConsoleOrders.Lines(
            ServerConsoleOptions.Bind(new OptionReader(ServerConsoleOptions.Spec, JObject.Parse(json))).Lines,
            ReplyDefaults.ConsoleLines, "lines", problems);

    // ---- server command: the command ----

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"command": ""}""")]
    [InlineData("""{"command": "   "}""")]
    public void ACommandIsRequired(string json)
    {
        List<OptionProblem> problems = new();

        Assert.Null(Command(json, problems));

        Assert.Equal("command", Assert.Single(problems).Path);
        Assert.Equal("is required: the console command to run, e.g. help", problems[0].Problem);
        Assert.Equal("server command: command: is required: the console command to run, e.g. help.",
            OptionProblem.Refusal(ServerCommandOptions.Spec.Label, problems).Message);
    }

    [Fact]
    public void TheCommandIsTheWholeLineTrimmed()
    {
        List<OptionProblem> problems = new();

        ConsoleOrder order = Command("""{"command": "  storm start  "}""", problems)!;

        Assert.Empty(problems);
        Assert.Equal("storm start", order.Command);
    }

    // ---- server command: the lines ----

    [Theory]
    [InlineData("""{"command": "help"}""", 100)]
    [InlineData("""{"command": "help", "lines": 1}""", 1)]
    [InlineData("""{"command": "help", "lines": 7}""", 7)]
    [InlineData("""{"command": "help", "lines": 500}""", 500)]
    public void TheLinesAreTheCallsOrTheCatalogueDefault(string json, int lines)
    {
        List<OptionProblem> problems = new();

        ConsoleOrder order = Command(json, problems)!;

        Assert.Empty(problems);
        Assert.Equal(lines, order.Lines);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(501)]
    [InlineData(5000)]
    public void LinesOutsideOneToFiveHundredIsAProblem(long lines)
    {
        List<OptionProblem> problems = new();

        Assert.Null(Command($$"""{"command": "help", "lines": {{lines}}}""", problems));

        Assert.Equal("lines", Assert.Single(problems).Path);
        Assert.Equal("is 1 to 500 lines", problems[0].Problem);
        Assert.Equal(500, ConsoleOrders.MaximumLines);
    }

    [Fact]
    public void ConfirmIsTheCallsAndTheCommandRunsOnlyWithIt()
    {
        List<OptionProblem> problems = new();

        ConsoleOrder plain = Command("""{"command": "help"}""", problems)!;
        ConsoleOrder confirmed = Command("""{"command": "help", "confirm": true}""", problems)!;

        Assert.Empty(problems);
        Assert.True(plain.Report.TakesConfirm);
        Assert.False(plain.Report.Acts);
        Assert.True(confirmed.Report.Acts);
    }

    // ---- server console: the lines ----

    [Theory]
    [InlineData("{}", 40)]
    [InlineData("""{"lines": 1}""", 1)]
    [InlineData("""{"lines": 80}""", 80)]
    [InlineData("""{"lines": 500}""", 500)]
    public void TheConsolesLinesAreTheCallsOrTheCatalogueDefault(string json, int lines)
    {
        List<OptionProblem> problems = new();

        int? given = Lines(json, problems);

        Assert.Empty(problems);
        Assert.Equal(lines, given);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public void TheConsolesLinesAreKeptToOneToFiveHundred(long lines)
    {
        List<OptionProblem> problems = new();

        Assert.Null(Lines($$"""{"lines": {{lines}}}""", problems));

        Assert.Equal("lines", Assert.Single(problems).Path);
        Assert.Equal("is 1 to 500 lines", problems[0].Problem);
        Assert.Equal("server console: lines: is 1 to 500 lines.",
            OptionProblem.Refusal(ServerConsoleOptions.Spec.Label, problems).Message);
    }
}
