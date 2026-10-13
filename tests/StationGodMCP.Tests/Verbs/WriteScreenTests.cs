#nullable enable

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Screens;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// write screen's touch (ScreenPress): an element or a pixel, never both; change needs text; event defaults to click;
/// every problem in verb names and all at once.
/// </summary>
public sealed class WriteScreenTests
{
    private static ScreenPress? Parse(string options, out List<OptionProblem> problems)
    {
        problems = new List<OptionProblem>();
        WriteScreenOptions bound = WriteScreenOptions.Bind(new OptionReader(WriteScreenOptions.Spec, JObject.Parse(options)));
        return ScreenPress.Of(bound.Surface, bound.Element, bound.Pixel, bound.Event!, bound.Text, bound.AsPlayer,
            problems);
    }

    [Fact]
    public void AnElementIsClickedByDefault()
    {
        ScreenPress press = Parse("""{"id": "5", "element": "start"}""", out _)!;

        Assert.Equal(new ScreenPress.Target.Element("start"), press.On);
        Assert.Equal("click", press.Event);
        Assert.Null(press.Surface);
    }

    [Fact]
    public void APixelIsAPoint()
    {
        ScreenPress press = Parse("""{"id": "5", "pixel": [12, 40.5], "surface": "main", "as_player": "Ann"}""", out _)!;

        Assert.Equal(new ScreenPress.Target.Pixel(12, 40.5), press.On);
        Assert.Equal(("main", "Ann"), (press.Surface, press.AsPlayer));
    }

    [Fact]
    public void ATouchNamesOneControlAndAChangeItsText()
    {
        Assert.Null(Parse("""{"id": "5"}""", out List<OptionProblem> none));
        Assert.Equal("element", Assert.Single(none).Path);

        Assert.Null(Parse("""{"id": "5", "element": "a", "pixel": [1, 2], "event": "change"}""",
            out List<OptionProblem> both));
        Assert.Equal(new[] { "pixel", "text" }, both.Select(problem => problem.Path));

        Assert.Null(Parse("""{"id": "5", "pixel": [1]}""", out List<OptionProblem> point));
        Assert.Equal("pixel", Assert.Single(point).Path);
    }
}
