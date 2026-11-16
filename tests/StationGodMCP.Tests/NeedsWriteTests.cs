#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;
using StationGodMCP.Tests.Hub;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>write needs: the checks, the waste conversion, the options as the binder reads them, the reply and the undo.</summary>
public sealed class NeedsWriteTests
{
    private const string Label = "write needs";

    // A stomach's waste capacity is 5 L; polluted water's molar volume is a number of litres per mole the game owns.
    private const double LitresPerMole = 0.018;

    private static NeedsAsk Ask(double? nutrition, double? hydration, double? waste) =>
        NeedsAsk.Of(Label, nutrition, hydration, waste);

    private static NeedsRequest Request(double? nutrition, double? hydration, double? waste) =>
        Assert.IsType<Asked>(Ask(nutrition, hydration, waste)).Request;

    [Fact]
    public void AllThreeAreAskedAsGiven()
    {
        NeedsRequest request = Request(0.5, 0.5, 0.5);

        Assert.Equal(new NeedsRequest(0.5, 0.5, 0.5), request);
    }

    [Fact]
    public void OneValueAsksOnlyThatValue()
    {
        NeedsRequest request = Request(null, 0.5, null);

        Assert.Null(request.Nutrition);
        Assert.Equal(0.5, request.Hydration);
        Assert.Null(request.Waste);
        Assert.False(request.AsksWaste);
    }

    [Fact]
    public void NoValueIsMissingTargetNamingTheThree()
    {
        VerbError error = Assert.IsType<NothingAsked>(Ask(null, null, null)).Error;

        Assert.Equal("missing_target", error.Code);
        Assert.Equal(new[] { "nutrition", "hydration", "waste" },
            error.Data!["options"]!.Select(option => (string)option!).ToArray());
    }

    [Theory]
    [InlineData(1.1, null, null, "nutrition")]
    [InlineData(null, 2.0, null, "hydration")]
    [InlineData(null, null, -0.1, "waste")]
    [InlineData(null, 1.76, null, "hydration")]
    public void ARangeOutsideTheGamesIsInvalidArgumentNamingTheOption(double? nutrition, double? hydration,
        double? waste, string path)
    {
        VerbError error = Assert.IsType<NothingAsked>(Ask(nutrition, hydration, waste)).Error;

        Assert.Equal("invalid_argument", error.Code);
        Assert.Contains(path, error.Message);
    }

    [Fact]
    public void HydrationTakesItsCapOf1Point75AndNutritionStopsAtOne()
    {
        Assert.IsType<Asked>(Ask(null, 1.75, null));
        Assert.IsType<Asked>(Ask(1.0, null, null));
        Assert.IsType<NothingAsked>(Ask(1.01, null, null));
    }

    [Fact]
    public void SanitationOffRefusesAWasteAskAndNothingElse()
    {
        BodyFacts noSanitation = new BodyFacts(HasStomach: true, SanitationOn: false);

        NeedsRefused refused = Assert.IsType<NeedsRefused>(NeedsCheck.Of(Request(null, null, 0.5), noSanitation));
        Assert.Equal("sanitation_off", refused.Code);
        Assert.IsType<NeedsCleared>(NeedsCheck.Of(Request(0.5, 0.5, null), noSanitation));
    }

    [Fact]
    public void ABodyWithNoStomachIsRefusedNoOrganWhateverIsAsked()
    {
        BodyFacts robot = new BodyFacts(HasStomach: false, SanitationOn: true);

        foreach (NeedsRequest request in new[] { Request(0.5, null, null), Request(null, 0.5, null), Request(null, null, 0.5) })
        {
            Assert.Equal("no_organ", Assert.IsType<NeedsRefused>(NeedsCheck.Of(request, robot)).Code);
        }
    }

    [Fact]
    public void ARobotWithSanitationOffIsNoOrganNotSanitationOff()
    {
        BodyFacts both = new BodyFacts(HasStomach: false, SanitationOn: false);

        Assert.Equal("no_organ", Assert.IsType<NeedsRefused>(NeedsCheck.Of(Request(null, null, 0.5), both)).Code);
    }

    [Fact]
    public void AllChecksPassForAHumanWithSanitation()
    {
        Assert.IsType<NeedsCleared>(NeedsCheck.Of(Request(0.5, 0.5, 0.5), new BodyFacts(true, true)));
    }

    [Fact]
    public void WasteAtOneWarnsItWetsNextTickAndBelowOneDoesNot()
    {
        Assert.Contains(NeedsWarnings.Of(Request(null, null, 1.0)), warning => warning.Code == "wets_next_tick");
        Assert.DoesNotContain(NeedsWarnings.Of(Request(null, null, 0.99)), warning => warning.Code == "wets_next_tick");
    }

    [Fact]
    public void NutritionOrHydrationWarnsTheyDrainAndWasteAloneDoesNot()
    {
        Assert.Contains(NeedsWarnings.Of(Request(0.5, null, null)), warning => warning.Code == "drains");
        Assert.Contains(NeedsWarnings.Of(Request(null, 0.5, null)), warning => warning.Code == "drains");
        Assert.DoesNotContain(NeedsWarnings.Of(Request(null, null, 0.5)), warning => warning.Code == "drains");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void WasteMolesAndRatioRoundTrip(double ratio)
    {
        double moles = WasteMoles.MolesFor(ratio, LitresPerMole);

        Assert.Equal(ratio, WasteMoles.RatioOf(moles, LitresPerMole), 9);
    }

    [Fact]
    public void WasteMolesFollowTheStomachsFiveLitreCapacity()
    {
        Assert.Equal(5.0 / LitresPerMole, WasteMoles.MolesFor(1.0, LitresPerMole), 9);
        Assert.Equal(2.5 / LitresPerMole, WasteMoles.MolesFor(0.5, LitresPerMole), 9);
        Assert.Equal(0.0, WasteMoles.MolesFor(0.0, LitresPerMole));
    }

    [Fact]
    public void TheRatioIsClampedAsTheGameClampsIt()
    {
        Assert.Equal(1.0, WasteMoles.RatioOf(1000.0, LitresPerMole));
        Assert.Equal(1.0, WasteMoles.RatioOf(WasteMoles.MolesFor(7.0, LitresPerMole), LitresPerMole), 9);
    }

    [Fact]
    public void TheDifferenceIsPositiveToAddAndNegativeToRemove()
    {
        Assert.Equal(40.0, WasteMoles.Difference(10.0, 50.0));
        Assert.Equal(-10.0, WasteMoles.Difference(50.0, 40.0));
    }

    [Fact]
    public void ANeedChangeIsASharePerCapacity()
    {
        NeedChange change = NeedChange.Of(10.0, 25.0, 50.0);

        Assert.Equal(new NeedChange(0.2, 0.5, 50.0), change);
    }

    [Fact]
    public void TheReadBackAcceptsWhatTheGamesFloatsKeep()
    {
        Assert.True(WasteReadBack.Reached(0.5, 0.5004));
        Assert.False(WasteReadBack.Reached(0.5, 0.45));
    }

    // The binder fills a default into an option the call left out; these bind real JSON arguments through the
    // generated options class, as the verb handler does, so a default can never make a given rule misfire.
    private static NeedsAsk Bound(string json)
    {
        WriteNeedsOptions options = WriteNeedsOptions.Bind(new OptionReader(WriteNeedsOptions.Spec, JObject.Parse(json)));
        return NeedsAsk.Of(Label, options.Nutrition, options.Hydration, options.Waste);
    }

    [Fact]
    public void BoundFromJsonOnlyTheGivenValuesAreAsked()
    {
        NeedsRequest only = Assert.IsType<Asked>(Bound("""{"hydration":0.5,"confirm":true}""")).Request;
        Assert.Equal(new NeedsRequest(null, 0.5, null), only);

        NeedsRequest all = Assert.IsType<Asked>(Bound("""{"nutrition":0.5,"hydration":0.5,"waste":0.5}""")).Request;
        Assert.Equal(new NeedsRequest(0.5, 0.5, 0.5), all);
    }

    [Fact]
    public void BoundFromJsonNothingGivenIsMissingTargetAndAZeroIsAGivenValue()
    {
        Assert.Equal("missing_target",
            Assert.IsType<NothingAsked>(Bound("""{"player":"xceled","confirm":true}""")).Error.Code);

        NeedsRequest zero = Assert.IsType<Asked>(Bound("""{"waste":0}""")).Request;
        Assert.Equal(new NeedsRequest(null, null, 0.0), zero);
        Assert.True(zero.AsksWaste);
    }

    [Fact]
    public void BoundFromJsonARangeIsRefused()
    {
        Assert.Equal("invalid_argument", Assert.IsType<NothingAsked>(Bound("""{"hydration":2}""")).Error.Code);
    }

    [Fact]
    public void TheReportListsEachAskedNeedAsATargetWithTheUndoMark()
    {
        NeedsWrittenView reading = new NeedsWrittenView(new ThingView(new ThingId(77), "Human", "xceled"),
            new NeedChange(0.2, 0.5, 50.0), new NeedChange(0.4, 0.4, 5.0), new WasteChange(0.1, 0.5), true,
            new List<NeedsWarning>());

        ReportReply report = NeedsReplies.Reported(reading, new NeedsRequest(0.5, null, 0.5), "[undo]");

        Assert.Equal(new[] { "nutrition", "waste" }, report.Targets.Select(target => target.Name).ToArray());
        Assert.All(report.Targets, target => Assert.Equal("77", target.Id));
        Assert.Equal(0.2, report.Targets[0].Current);
        Assert.Equal(0.5, report.Targets[0].New);
    }

    [Fact]
    public void AWasteTargetIsLeftOutOfTheReportWhenTheBodyHasNone()
    {
        NeedsWrittenView reading = new NeedsWrittenView(new ThingView(new ThingId(77), "Human", "xceled"),
            new NeedChange(0.2, 0.5, 50.0), new NeedChange(0.4, 0.4, 5.0), null, false, new List<NeedsWarning>());

        ReportReply report = NeedsReplies.Reported(reading, new NeedsRequest(0.5, null, 0.5), "[undo]");

        Assert.Equal("nutrition", Assert.Single(report.Targets).Name);
    }

    // ---- undo: the hub writes each moved need back to its share before ------------------------------------------------

    private const string Written =
        """{"player":{"reference_id":"77","prefab_name":"Human","display_name":"xceled"},"nutrition":{"before":0.2,"after":0.5,"capacity":50},"hydration":{"before":0.4,"after":0.4,"capacity":5},"waste":{"before":0.1,"after":0.5},"sanitation":true,"warnings":[]}""";

    [Fact]
    public void UndoWritesTheMovedNeedsBackAndLeavesTheOneTheWriteDidNotMove()
    {
        FakeGameLink game = new();
        game.Answer = call => call.Method == "write" && call.Params()["what"]?.ToString() == "needs"
            ? FakeGameLink.Ok(Written)
            : FakeGameLink.Ok("{}");
        StationGodMCP.Server.VerbPipeline pipeline = game.Pipeline();

        JsonNode? acted = pipeline.Call("write", "needs", """{"nutrition":0.5,"waste":0.5,"confirm":true}""").Done();
        JsonNode? undone = pipeline.Call("undo", null, """{"id":"write-1","confirm":true}""").Done();

        Assert.Equal("write-1", (string)acted!["job_id"]!);
        Assert.Equal(1, (int)undone!["undone"]!);
        JsonObject back = game.CallsTo("write")[1].Params()!;
        Assert.Equal("77", (string)back["player"]!);
        Assert.Equal(0.2, (double)back["nutrition"]!);
        Assert.Equal(0.1, (double)back["waste"]!);
        Assert.Null(back["hydration"]);
        Assert.True((bool)back["confirm"]!);
    }
}
