using System.Text.Json.Nodes;

namespace StationGodMCP.Server;

/// <summary>
/// build's from_plan (refs topic): a plan route or plan spot ref ("r9", one spot "r9.spots.2", default the first), or
/// on the script and program faces the plan reply or one spot itself, stands for that plan's build_arguments. The call
/// is resolved before it is checked: what comes from the plan when it is left out, the options given replace the
/// plan's, and a run given (waypoints, cells, piece, pieces) replaces the plan's run.
/// </summary>
public sealed partial class VerbPipeline
{
    private const string FromPlanOption = "from_plan";
    private const string PlanArgumentsKey = "build_arguments";

    private static readonly string[] PlannedWhats = ["route", "spot"];
    private static readonly string[] RunOptions = ["waypoints", "cells", "piece", "pieces"];

    /// <summary>The build options a from_plan names, or why it names none.</summary>
    private abstract record PlanArguments
    {
        internal sealed record Found(JsonObject Options) : PlanArguments;

        internal sealed record Refused(string Problem) : PlanArguments;
    }

    private VerbReply.Failed? FromPlan(VerbCall call, out VerbCall resolved)
    {
        resolved = call;
        if (call.Verb != "build" || call.Args[FromPlanOption] is not { } given)
        {
            return null;
        }

        PlanArguments planned = given is JsonObject reply ? Arguments(reply, "the plan") : ArgumentsOfRef(given);
        if (planned is PlanArguments.Refused refused)
        {
            return PlanRefusal(call, refused.Problem);
        }

        JsonObject options = (JsonObject)((PlanArguments.Found)planned).Options.DeepClone();
        string? plannedWhat = JsonNodes.Text(options["what"]);
        options.Remove("what");
        if (call.What != null && plannedWhat != null && call.What != plannedWhat)
        {
            return PlanRefusal(call, $"the plan builds {plannedWhat}: leave what out or give what: {plannedWhat}");
        }

        if (RunOptions.Any(run => call.Args[run] != null))
        {
            foreach (string run in RunOptions)
            {
                options.Remove(run);
            }
        }

        foreach ((string name, JsonNode? value) in call.Args.Where(option => option.Key != FromPlanOption))
        {
            options[name] = value?.DeepClone();
        }

        resolved = new VerbCall(call.Verb, call.What ?? plannedWhat, options, call.Face, call.Stream, call.Line)
        {
            Gate = call.Gate,
            LateReply = call.LateReply,
            Note = call.Note,
            Recorded = call.Recorded
        };
        return null;
    }

    private static VerbReply.Failed PlanRefusal(VerbCall call, string problem) =>
        OptionProblem.Refusal(call.What == null ? call.Verb : $"{call.Verb} {call.What}",
            [new OptionProblem(FromPlanOption, problem)]);

    private PlanArguments ArgumentsOfRef(JsonNode given)
    {
        string text = JsonNodes.Text(given) ?? given.ToJsonString();
        if (RefStore.Parse(text) is not { } parsed || _refs.Get(parsed.Id) is not { } stored)
        {
            return new PlanArguments.Refused($"no ref '{text}' (refs last until the world changes)");
        }

        if (stored.Verb != "plan" || !PlannedWhats.Contains(stored.What))
        {
            return new PlanArguments.Refused(
                $"{stored.Id} is a {stored.Verb} {stored.What} call; from_plan takes a plan route or plan spot ref");
        }

        return At(stored.Whole, parsed.Path) is JsonObject entry
            ? Arguments(entry, text)
            : new PlanArguments.Refused($"{text} names nothing in {stored.Id}'s reply");
    }

    // A route's build_arguments, a spot's, or a spot plan's first spot's.
    private static PlanArguments Arguments(JsonObject entry, string named) =>
        entry[PlanArgumentsKey] is JsonObject options ? new PlanArguments.Found(options)
        : entry["spots"] is JsonArray { Count: > 0 } spots && spots[0] is JsonObject first ? Arguments(first, named)
        : new PlanArguments.Refused($"{named} has no build_arguments (the plan found nothing to build)");

    // A ref's path through its whole reply: a name steps into an object, a number into a list.
    private static JsonNode? At(JsonNode? node, string path) =>
        path.Length == 0
            ? node
            : path.Split('.').Aggregate(node, (at, step) => at switch
            {
                JsonArray list when int.TryParse(step, out int index) => index >= 0 && index < list.Count ? list[index] : null,
                JsonObject inner => inner[step],
                _ => null
            });
}
