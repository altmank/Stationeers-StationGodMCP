#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;
using StationGodMCP.Tests.CatalogueChecks;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// build finish without the game: its call (FinishOrder and FinishRefusals: one structure, a list or a ref, the state
/// to reach, free, materials, confirm; every rule refusal worded for the replies, a state out of range as a problem
/// about the state option in verb words) and the two replies its per-id steps answer (FinishReplies): confirmed, the
/// shared per-id rows - one id its own step, a list the rows, a refused id its row's error - and until confirm the one
/// report (targets [id, name, state -> target], the call's whole cost, the steps, the warnings, the problems).
/// </summary>
public sealed class FinishStepTests
{
    private static readonly ThingView Structure = new ThingView(new ThingId(11), "StructureConsole", "Console");

    private static readonly ThingView Player = new ThingView(new ThingId(4021), "Human", "Engineer");

    private static FinishOrder Order(string json) =>
        FinishOrder.Of(BuildFinishOptions.Bind(new OptionReader(BuildFinishOptions.Spec, JObject.Parse(json))));

    // One step of a console that stands at from and reaches to: a steel plate or quantity of them and a welder.
    private static FinishStep Step(int index, long id, int from, int to, int last, int quantity = 1,
        VerbError? problem = null, bool acted = false, bool paidByPlayer = false, int? available = 9,
        bool sourced = true)
    {
        List<BuildCostView> cost = new List<BuildCostView>
        {
            new BuildCostView("ItemSteelPlate", "Steel Plate", quantity, available, null)
        };
        List<BuildNeedView> tools = new List<BuildNeedView>
        {
            new BuildNeedView("ItemWelder", "Welding Torch", null, true)
        };
        List<BuildIssueView> warnings = paidByPlayer
            ? new List<BuildIssueView> { new BuildIssueView("materials_from_player", "paid from Engineer") }
            : new List<BuildIssueView>();
        FinishStep step = new FinishStep(index, new ThingId(id), Structure, sourced ? Player : null, "Console", from,
            to, last, cost, tools, warnings, problem);
        if (acted)
        {
            step.Stepped(new BuildStateView(to, last, null));
        }

        return step;
    }

    private static JObject Reply(VerbOutcome outcome) => Replies.Tree(((VerbOutcome.Answered)outcome).Reply);

    // ---- the call ----

    [Fact]
    public void OneStructureIsNotAListAndTheStateGivenWins()
    {
        FinishOrder order = Order("""{"id": "1199811", "state": 2}""");

        Assert.False(order.Ids.AnswersRows);
        Assert.Equal("1199811", Assert.Single(order.Ids.Ids));
        Assert.Equal(2L, order.State);
        Assert.Equal(2, order.Target(0));
        Assert.False(order.Free);
        Assert.False(order.Acts);
        Assert.Equal("build finish", order.Label);
        Assert.Equal("[cannot undo: change job]", order.Mark);
    }

    [Fact]
    public void AListKeepsItsOrderAndReachesTheNextStateByDefault()
    {
        FinishOrder order = Order("""{"id": ["1199811", "1199812"], "confirm": true}""");

        Assert.True(order.Ids.AnswersRows);
        Assert.Equal(new[] { "1199811", "1199812" }, order.Ids.Ids);
        Assert.Null(order.State);
        Assert.Equal(3, order.Target(2));
        Assert.True(order.Acts);
    }

    [Fact]
    public void CreativeAndTheHoldersThatPayAreTheCalls()
    {
        FinishOrder order = Order("""{"id": "11", "free": true, "materials": ["player", "1199813"]}""");

        Assert.True(order.Free);
        Assert.True(order.Materials.FromPlayer);
        Assert.Equal(2, order.Materials.Payers.Count);
    }

    // ---- the refusals ----

    [Fact]
    public void EveryRuleRefusalIsItsOwnCodeAndAStateOutOfRangeIsAProblemAboutState()
    {
        const string label = "build finish";
        Assert.Null(FinishRefusals.Of(BuildStepRefusal.None, "Console 11", 0, 2, label));
        Assert.Equal("no_build_states",
            FinishRefusals.Of(BuildStepRefusal.SingleState, "Console 11", 0, 0, label)!.Code);
        Assert.Equal("structure_broken",
            FinishRefusals.Of(BuildStepRefusal.Broken, "Console 11", -1, 2, label)!.Code);
        Assert.Equal("already_complete",
            FinishRefusals.Of(BuildStepRefusal.Complete, "Console 11", 2, 2, label)!.Code);
        Assert.Equal("structure_damaged",
            FinishRefusals.Of(BuildStepRefusal.Damaged, "Console 11", 0, 2, label)!.Code);
        Assert.Contains("Console 11 is damaged", FinishRefusals
            .Of(BuildStepRefusal.Damaged, "Console 11", 0, 2, label)!.Message);

        VerbError outOfRange = FinishRefusals.Of(BuildStepRefusal.TargetOutOfRange, "Console 11", 1, 2, label)!;

        Assert.Equal(VerbError.InvalidArgument, outOfRange.Code);
        Assert.Equal("build finish: state: must be above 1 and at most 2.", outOfRange.Message);
        Assert.Equal("state", (string?)outOfRange.Data!["problems"]![0]!["path"]);
        Assert.Equal("must be above 1 and at most 2", (string?)outOfRange.Data!["problems"]![0]!["problem"]);
    }

    // ---- the two replies ----

    [Fact]
    public void ConfirmedAListAnswersRowsAndOneStructureItsOwnStep()
    {
        List<FinishResult> results = new List<FinishResult>
        {
            new FinishResult.Stepped(Step(0, 11, 0, 1, 2, quantity: 2, acted: true)),
            new FinishResult.Failed(1, "nope",
                new VerbError("thing_not_found", "build finish: 'nope' is not a thing's id."))
        };

        JObject rows = Reply(FinishReplies.Acted(new PerIdList(new[] { "11", "nope" }, true, null), results));

        Assert.Equal(2, (int)rows["count"]!);
        Assert.Equal(1, (int)rows["success_count"]!);
        Assert.Equal(1, (int)rows["error_count"]!);
        JObject first = (JObject)rows["results"]![0]!;
        Assert.Equal(0, (int)first["index"]!);
        Assert.True((bool)first["ok"]!);
        Assert.Equal("11", (string?)first["reference_id"]);
        Assert.Equal("StructureConsole", (string?)first["thing"]!["prefab_name"]);
        Assert.Equal(0, (int)first["from_state"]!);
        Assert.Equal(1, (int)first["to_state"]!);
        Assert.Equal(2, (int)first["last_state"]!);
        Assert.Equal(2, (int)first["cost"]![0]!["quantity"]!);
        Assert.Equal("ItemWelder", (string?)first["tools"]![0]!["prefab_name"]);
        Assert.Equal(1, (int)first["build_state"]!["current"]!);
        Assert.Null(first["error"]);
        Assert.Equal("thing_not_found", (string?)rows["results"]![1]!["error"]!["code"]);

        JObject one = Reply(FinishReplies.Acted(new PerIdList(new[] { "11" }, false, null),
            results.GetRange(0, 1)));

        Assert.Null(one["results"]);
        Assert.Equal("11", (string?)one["reference_id"]);
        Assert.Equal(1, (int)one["to_state"]!);
        Assert.Equal(1, (int)one["build_state"]!["current"]!);

        VerbOutcome refused = FinishReplies.Acted(new PerIdList(new[] { "nope" }, false, null),
            results.GetRange(1, 1));

        Assert.Equal("thing_not_found", Assert.IsType<VerbOutcome.Refused>(refused).Error.Code);
    }

    [Fact]
    public void AStepShortOfMaterialsIsItsRowsErrorAndItsReportsProblem()
    {
        VerbError short_ = new VerbError("not_enough_materials",
            "3 Steel Plate (ItemSteelPlate) needed, 1 held by Locker A.");
        List<FinishResult> results = new List<FinishResult>
        {
            new FinishResult.Stepped(Step(0, 11, 0, 1, 2, quantity: 3, problem: short_))
        };

        JObject rows = Reply(FinishReplies.Acted(new PerIdList(new[] { "11" }, true, null), results));
        Assert.False((bool)rows["results"]![0]!["ok"]!);
        Assert.Equal("not_enough_materials", (string?)rows["results"]![0]!["error"]!["code"]);

        JObject report = Replies.Tree(FinishReplies.Reported(results, free: false, "[cannot undo: change job]"));
        Assert.False((bool)report["ready"]!);
        Assert.Equal(0, (int)report["problems"]![0]!["index"]!);
        Assert.Equal("not_enough_materials", (string?)report["problems"]![0]!["code"]);
        Assert.Equal("11", (string?)report["problems"]![0]!["reference_id"]);
        Assert.Single((JArray)report["targets"]!);
        Assert.Single((JArray)report["steps"]!);
    }

    [Fact]
    public void TheReportListsEveryTargetItsStepTheWholeCostAndTheWarningOnce()
    {
        List<FinishResult> results = new List<FinishResult>
        {
            new FinishResult.Stepped(Step(0, 11, 0, 1, 2, quantity: 2, paidByPlayer: true)),
            new FinishResult.Stepped(Step(1, 12, 1, 2, 2, quantity: 3, paidByPlayer: true))
        };

        JObject report = Replies.Tree(FinishReplies.Reported(results, free: false, "[cannot undo: change job]"));

        Assert.Equal(new[] { "ready", "free", "from", "materials", "steps", "warnings", "problems", "report_only", "targets" },
            report.Properties().Select(key => key.Name).ToArray());
        Assert.True((bool)report["ready"]!);
        Assert.False((bool)report["free"]!);
        Assert.Equal("Engineer", (string?)report["from"]!["display_name"]);
        Assert.Equal(5, (int)report["materials"]![0]!["quantity"]!);
        Assert.Equal("ItemSteelPlate", (string?)report["materials"]![0]!["prefab_name"]);
        Assert.Equal(2, report["steps"]!.Count());
        Assert.Equal(1, (int)report["steps"]![1]!["index"]!);
        Assert.Equal("12", (string?)report["steps"]![1]!["reference_id"]);
        Assert.Equal("[11, Console, 0 -> 1] [cannot undo: change job]", (string?)report["targets"]![0]!["text"]);
        Assert.Equal("[12, Console, 1 -> 2] [cannot undo: change job]", (string?)report["targets"]![1]!["text"]);
        Assert.Equal("materials_from_player", (string?)Assert.Single((JArray)report["warnings"]!)["code"]);
        Assert.Empty((JArray)report["problems"]!);
    }

    [Fact]
    public void AFreeReportTakesNoMaterialsAndHasNoSource()
    {
        JObject report = Replies.Tree(FinishReplies.Reported(
            new List<FinishResult> { new FinishResult.Stepped(Step(0, 11, 0, 1, 2, available: null, sourced: false)) },
            free: true, "[cannot undo: change job]"));

        Assert.True((bool)report["free"]!);
        Assert.Equal(JTokenType.Null, report["from"]!.Type);
        Assert.Equal(JTokenType.Null, report["materials"]![0]!["available"]!.Type);
    }

    [Fact]
    public void NoStepAtAllIsAReadyReportWithNothingInIt()
    {
        JObject report = Replies.Tree(FinishReplies.Reported(new List<FinishResult>(), free: false, "[cannot undo: change job]"));

        Assert.True((bool)report["ready"]!);
        Assert.Empty((JArray)report["targets"]!);
        Assert.Empty((JArray)report["steps"]!);
        Assert.Empty((JArray)report["materials"]!);
        Assert.Empty((JArray)report["warnings"]!);
    }

    // ---- the standing rule ----

    /// <summary>
    /// build finish's new files are verb code: verb words only, no Vocabulary (old structure, gone in A5) and none of
    /// the old tool's argument or key names.
    /// </summary>
    [Fact]
    public void TheNewFinishFilesUseVerbWordsOnly()
    {
        string[] files =
        {
            "src/StationGodMCP.Mod/Pure/Verbs/FinishOrder.cs",
            "src/StationGodMCP.Mod/Pure/Verbs/FinishSteps.cs",
            "src/StationGodMCP.Mod/Verbs/BuildFinish.cs",
            "src/StationGodMCP.Mod/Verbs/BuildStep.cs"
        };
        foreach (string file in files)
        {
            string text = File.ReadAllText(Path.Combine(CatalogueFiles.RepositoryRoot(), file));
            Assert.DoesNotContain("Vocabulary", text);
            Assert.DoesNotContain("advance_build_state", text);
            Assert.DoesNotContain("from_id", text);
            Assert.DoesNotContain("dry_run", text);
        }
    }
}
