#nullable enable

using System.Collections.Generic;
using System.Text.Json.Nodes;
using StationGodMCP.Server;
using Xunit;

namespace StationGodMCP.Tests.Scripts;

/// <summary>
/// run_script's argument check against a saved script's main(...) parameters (SPEC-NOTES 5b): a missing required
/// argument, an unknown name and a value whose JSON type the annotation clearly maps are problems; a union that names
/// no mapped type, a list's items and an annotation this code does not know are left to the run.
/// </summary>
public sealed class ScriptArgumentsTests
{
    private static readonly ScriptParameter Who = new("who", "str", false, ScriptParameter.PosOrKw);
    private static readonly ScriptParameter Times = new("times", "int", true, ScriptParameter.PosOrKw);
    private static readonly ScriptParameter Sounds = new("sounds", "bool", true, ScriptParameter.KeywordOnly);
    private static readonly ScriptParameter Rest = new("rest", null, false, ScriptParameter.VarPositional);

    private static List<OptionProblem>? Check(IReadOnlyList<ScriptParameter> parameters, string args) =>
        ScriptArguments.Check(parameters, JsonNode.Parse(args)!.AsObject());

    [Fact]
    public void AMissingRequiredArgumentIsAProblemAndAnOptionalOneIsNot()
    {
        Assert.Equal("who", Assert.Single(Check([Who, Times], """{"times": 2}"""))!.Path);
        Assert.Null(Check([Who, Times], """{"who": "lu"}"""));
        Assert.Null(Check([Who, Times], """{"who": "lu", "times": 2}"""));
        Assert.DoesNotContain(Check([Who, Times], "{}")!, problem => problem.Path == "times");
    }

    [Fact]
    public void AnUnknownNameIsAProblemWithTheParameterNamesAsTheNearest()
    {
        List<OptionProblem> problems = Check([Who, Times], """{"who": "lu", "whos": "lu"}""")!;

        OptionProblem problem = Assert.Single(problems);
        Assert.Equal("whos", problem.Path);
        Assert.Equal("who, times", string.Join(", ", problem.Nearest!));
    }

    [Fact]
    public void AVarKeywordTakesAnyName()
    {
        ScriptParameter named = new("named", null, false, ScriptParameter.VarKeyword);

        Assert.Null(Check([Who, named], """{"who": "lu", "anything": 1}"""));
        Assert.NotNull(Check([Who, Rest], """{"anything": 1}"""));
    }

    [Theory]
    [InlineData("int", "1", true)]
    [InlineData("int", "1.5", false)]
    [InlineData("float", "2", true)]
    [InlineData("float", "1.5", true)]
    [InlineData("str", "\"x\"", true)]
    [InlineData("str", "3", false)]
    [InlineData("bool", "true", true)]
    [InlineData("bool", "\"true\"", false)]
    [InlineData("list[int]", "[1, 2]", true)]
    [InlineData("list[int]", "{}", false)]
    [InlineData("list[int]", "\"1,2\"", false)]
    [InlineData("dict[str, int]", "{\"a\": 1}", true)]
    [InlineData("dict[str, int]", "[1]", false)]
    [InlineData("int | None", "1", true)]
    [InlineData("int | None", "null", true)]
    [InlineData("int | None", "\"x\"", false)]
    [InlineData("Optional[int]", "2", true)]
    [InlineData("Union[int, str]", "\"x\"", true)]
    [InlineData("Union[int, str]", "true", false)]
    // A union with a part this code does not map, an unknown name, Any and no annotation at all: no check.
    [InlineData("int | Thing", "\"x\"", true)]
    [InlineData("Thing", "123", true)]
    [InlineData("Any", "[]", true)]
    [InlineData(null, "{}", true)]
    public void TheAnnotationMapsTheJsonTypeWhenItClearlyCan(string? annotation, string given, bool accepted)
    {
        ScriptParameter parameter = new("value", annotation, false, ScriptParameter.PosOrKw);

        List<OptionProblem>? problems = Check([parameter], "{\"value\": " + given + "}");

        Assert.Equal(accepted, problems == null);
    }

    [Fact]
    public void NullIsAllowedForEveryAnnotation()
    {
        // JSON null is Python's None, and an annotation is not enforced at the call; the key being present counts for
        // a required argument.
        Assert.Null(Check([Who, Times], """{"who": null}"""));
        Assert.Null(Check([Who, Times], """{"who": "lu", "times": null}"""));
    }
}
