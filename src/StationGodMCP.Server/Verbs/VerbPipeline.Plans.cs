using System.Text.Json.Nodes;

namespace StationGodMCP.Server;

/// <summary>
/// build's from_plan (refs topic): a plan route or plan spot ref ("r9", one spot "r9.spots.2", default the first), or
/// on the script and program faces the plan reply or one spot itself, stands for that plan's build_arguments. The call
/// is resolved before it is checked: what comes from the plan when it is left out, the options given replace the
/// plan's, and a run given (waypoints, cells, piece, pieces) replaces the plan's run. A plan's ref holds its
/// build_arguments whatever the call's shaping showed: fields and omit change only what the caller sees.
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

        if (stored.Verb == "build")
        {
            return new PlanArguments.Refused(
                $"{stored.Id} is a build {stored.What} call, not a plan: repeat: \"{stored.Id}\" sends that call again " +
                "(the options given replace its own); from_plan takes a plan route or plan spot ref");
        }

        if (stored.Verb != "plan" || !PlannedWhats.Contains(stored.What))
        {
            return new PlanArguments.Refused(
                $"{stored.Id} is a {stored.Verb} {stored.What} call; from_plan takes a plan route or plan spot ref");
        }

        return At(stored.Planned ?? stored.Whole, parsed.Path) is JsonObject entry
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

    /// <summary>
    /// A direct plan route or plan spot call's shaping as sent: the caller's fields with build_arguments added when they
    /// do not name it whole (Hidden; Parts holds the keys of it they did name, as build_arguments.branches names
    /// branches), and the caller's omit without the selectors that would drop it (Omitted). Shown takes those back out
    /// of the reply, so the caller sees what was asked and the ref keeps the whole plan.
    /// </summary>
    private sealed record PlanShaping(JsonObject Sent, bool Hidden, IReadOnlyList<string> Parts, IReadOnlyList<string> Omitted)
    {
        internal JsonNode? Shown(JsonNode planned)
        {
            if (planned.DeepClone() is not JsonObject shown)
            {
                return planned;
            }

            if (Hidden)
            {
                HideArguments(shown, Parts);
            }

            foreach (string omitted in Omitted)
            {
                Omit(shown, omitted);
            }

            return shown;
        }
    }

    private const string UnmatchedKey = "fields_unmatched";
    private const string ClosestKey = "fields_closest";
    private const string ValidKey = "fields_valid";

    private static PlanShaping? KeepingArguments(WhatEntry what, Face face, JsonObject options)
    {
        if (face != Face.Direct || what.Verb != "plan" || !PlannedWhats.Contains(what.Name))
        {
            return null;
        }

        JsonArray? fields = options[SidecarArguments.FieldsArgument] as JsonArray;
        JsonArray? omit = options[SidecarArguments.OmitArgument] as JsonArray;
        List<string> selectors = fields?.Select(JsonNodes.Text).OfType<string>().Select(field => field.Trim()).ToList() ?? [];
        bool hidden = fields != null && !selectors.Any(NamesWholeArguments);
        List<string> parts = selectors.Select(ArgumentsPart).OfType<string>().Distinct().ToList();
        List<string> omitted = omit?.Select(JsonNodes.Text).OfType<string>().Where(DropsArguments).ToList() ?? [];
        if (!hidden && omitted.Count == 0)
        {
            return null;
        }

        JsonObject sent = (JsonObject)options.DeepClone();
        if (hidden)
        {
            ((JsonArray)sent[SidecarArguments.FieldsArgument]!).Add(PlanArgumentsKey);
        }

        if (omitted.Count > 0)
        {
            JsonArray kept = [.. omit!.Where(selector => !omitted.Contains(JsonNodes.Text(selector) ?? ""))
                .Select(selector => selector?.DeepClone())];
            sent[SidecarArguments.OmitArgument] = kept.Count > 0 ? kept : null;
        }

        return new PlanShaping(sent, hidden, parts, omitted);
    }

    // build_arguments as a whole: by its name, or as spots.build_arguments.
    private static bool NamesWholeArguments(string selector) =>
        selector == PlanArgumentsKey || selector == "spots." + PlanArgumentsKey;

    // The key of build_arguments a path names (build_arguments.branches, spots.build_arguments.at.0): branches, at.
    private static string? ArgumentsPart(string selector)
    {
        string[] steps = selector.Split('.');
        int at = Array.IndexOf(steps, PlanArgumentsKey);
        return at >= 0 && at + 1 < steps.Length ? steps[at + 1] : null;
    }

    // An omit selector that leaves build_arguments out: it names it, or the spots that hold it.
    private static bool DropsArguments(string selector) =>
        selector.Contains(PlanArgumentsKey) || selector.Trim() == "spots";

    // build_arguments out of the reply's top and its top-level lists' entries, or cut to the parts the caller's fields
    // named; a list left holding only empty entries goes (fields kept it for build_arguments alone), and so does the
    // added name's fields_unmatched mention.
    private static void HideArguments(JsonObject shown, IReadOnlyList<string> parts)
    {
        CutArguments(shown, parts);
        foreach (string name in shown.Where(pair => pair.Value is JsonArray).Select(pair => pair.Key).ToList())
        {
            JsonArray entries = (JsonArray)shown[name]!;
            foreach (JsonObject entry in entries.OfType<JsonObject>())
            {
                CutArguments(entry, parts);
            }

            if (entries.Count > 0 && entries.All(entry => entry is JsonObject { Count: 0 }))
            {
                shown.Remove(name);
            }
        }

        if (shown[UnmatchedKey] is not JsonArray unmatched)
        {
            return;
        }

        foreach (JsonNode? named in unmatched.Where(name => JsonNodes.Text(name) == PlanArgumentsKey).ToList())
        {
            unmatched.Remove(named);
        }

        (shown[ClosestKey] as JsonObject)?.Remove(PlanArgumentsKey);
        if (unmatched.Count == 0)
        {
            shown.Remove(UnmatchedKey);
            shown.Remove(ClosestKey);
            shown.Remove(ValidKey);
        }
    }

    private static void CutArguments(JsonObject holder, IReadOnlyList<string> parts)
    {
        if (parts.Count == 0 || holder[PlanArgumentsKey] is not JsonObject arguments)
        {
            holder.Remove(PlanArgumentsKey);
            return;
        }

        foreach (string key in arguments.Select(pair => pair.Key).Where(key => !parts.Contains(key)).ToList())
        {
            arguments.Remove(key);
        }
    }

    // An omit selector applied here as the mod applies it: a name leaves that key out anywhere, a path leaves out what
    // it reaches from the reply's top or from each entry of a top-level list.
    private static void Omit(JsonObject shown, string selector)
    {
        string[] steps = selector.Trim().Split('.');
        if (steps.Length == 1)
        {
            OmitEverywhere(shown, steps[0]);
            return;
        }

        OmitPath(shown, steps);
        foreach (JsonObject entry in shown.Select(pair => pair.Value).OfType<JsonArray>().SelectMany(list => list.OfType<JsonObject>()))
        {
            OmitPath(entry, steps);
        }
    }

    private static void OmitEverywhere(JsonNode? node, string key)
    {
        switch (node)
        {
            case JsonObject inner:
                inner.Remove(key);
                foreach (JsonNode? child in inner.Select(pair => pair.Value).ToList())
                {
                    OmitEverywhere(child, key);
                }

                break;
            case JsonArray list:
                foreach (JsonNode? entry in list)
                {
                    OmitEverywhere(entry, key);
                }

                break;
        }
    }

    private static void OmitPath(JsonNode? node, ReadOnlySpan<string> steps)
    {
        switch (node)
        {
            case JsonObject inner when steps.Length == 1:
                inner.Remove(steps[0]);
                break;
            case JsonObject inner:
                OmitPath(inner[steps[0]], steps[1..]);
                break;
            case JsonArray list:
                foreach (JsonNode? entry in list)
                {
                    OmitPath(entry, steps);
                }

                break;
        }
    }
}
