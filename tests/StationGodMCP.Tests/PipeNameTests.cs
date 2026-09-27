#nullable enable

using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>The local pipe's name: what is accepted, and which of the environment and the config wins.</summary>
public sealed class PipeNameTests
{
    private const string Environment = "STATIONGODMCP_PIPE_NAME";

    [Theory]
    [InlineData("StationGodMCP", "StationGodMCP")]
    [InlineData("StationGodMCP-Test", "StationGodMCP-Test")]
    [InlineData("  padded  ", "padded")]
    [InlineData("with space.and_dots", "with space.and_dots")]
    public void AcceptsANameTrimmed(string text, string name)
    {
        Assert.Equal(name, PipeName.TryParse(text)?.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"\\.\pipe\StationGodMCP")]
    [InlineData("a/b")]
    [InlineData(@"a\b")]
    [InlineData("C:name")]
    public void RefusesEmptyAndSeparators(string? text)
    {
        Assert.Null(PipeName.TryParse(text));
    }

    [Fact]
    public void TheConfigIsUsedWithoutTheEnvironment()
    {
        PipeNameChoice choice = PipeName.Choose(null, Environment, "StationGodMCP-Test");
        Assert.Equal("StationGodMCP-Test", choice.Name.Value);
        Assert.Null(choice.Warning);
        Assert.Equal("StationGodMCP-Test", PipeName.Choose(string.Empty, Environment, "StationGodMCP-Test").Name.Value);
    }

    [Fact]
    public void TheEnvironmentOverridesTheConfig()
    {
        PipeNameChoice choice = PipeName.Choose("FromEnvironment", Environment, "FromConfig");
        Assert.Equal("FromEnvironment", choice.Name.Value);
        Assert.Null(choice.Warning);
    }

    [Fact]
    public void AnInvalidEnvironmentValueFallsBackToTheDefaultAndSaysWhere()
    {
        PipeNameChoice choice = PipeName.Choose("a/b", Environment, "FromConfig");
        Assert.Equal(PipeName.DefaultValue, choice.Name.Value);
        Assert.Contains(Environment, choice.Warning);
        Assert.Contains("'a/b'", choice.Warning);
    }

    [Fact]
    public void AnInvalidConfigValueFallsBackToTheDefaultAndSaysWhere()
    {
        PipeNameChoice choice = PipeName.Choose(null, Environment, "   ");
        Assert.Equal("StationGodMCP", choice.Name.Value);
        Assert.Contains("[Pipe] Name", choice.Warning);
    }
}
