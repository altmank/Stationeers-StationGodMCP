#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// One structure's build finish step, as the replies write it: the structure, the state it goes from to, what the
/// states it adds take (cost) and the tools they use, and, having acted, the build state read back after it. A step
/// whose sources do not hold what it takes carries Problem (not_enough_materials) and does not act; a step the call only
/// reported carries no build state.
/// </summary>
internal sealed class FinishStep
{
    internal FinishStep(int index, ThingId id, ThingView thing, ThingView? from, string name, int fromState, int toState,
        int lastState, List<BuildCostView> cost, List<BuildNeedView> tools, List<BuildIssueView> warnings,
        VerbError? problem)
    {
        Index = index;
        ReferenceId = id.ToString();
        Thing = thing;
        From = from;
        Name = name;
        FromState = fromState;
        ToState = toState;
        LastState = lastState;
        Cost = cost;
        Tools = tools;
        Warnings = warnings;
        Problem = problem;
    }

    /// <summary>The id's place among the ids the call gave, in order (0 for one id).</summary>
    public int Index { get; }

    /// <summary>The structure's id, as the row and the report name it.</summary>
    public string ReferenceId { get; }

    /// <summary>The structure itself.</summary>
    public ThingView Thing { get; }

    /// <summary>What the materials come from; null when free.</summary>
    public ThingView? From { get; }

    /// <summary>The structure's shown name.</summary>
    public string Name { get; }

    /// <summary>The build state it stands at now.</summary>
    public int FromState { get; }

    /// <summary>The build state it reaches.</summary>
    public int ToState { get; }

    /// <summary>The structure's last build state (complete).</summary>
    public int LastState { get; }

    /// <summary>One entry per material the states it adds take, with what the sources hold.</summary>
    public List<BuildCostView> Cost { get; }

    /// <summary>The tools those states use; not taken and not checked.</summary>
    public List<BuildNeedView> Tools { get; }

    /// <summary>What the step does that is allowed but worth knowing (materials_from_player); empty for none.</summary>
    public List<BuildIssueView> Warnings { get; }

    /// <summary>Why the step would not act (not_enough_materials); null when it is ready.</summary>
    public VerbError? Problem { get; }

    /// <summary>True when the step acts, or would on a confirmed call: its sources hold what it takes.</summary>
    public bool Ready => Problem == null;

    /// <summary>Having acted: the build state read back after the step; null for a report.</summary>
    public BuildStateView? BuildState { get; private set; }

    internal void Stepped(BuildStateView state) => BuildState = state;

    /// <summary>The confirmed row's payload: thing, from_state, to_state, last_state, cost, tools, build_state.</summary>
    internal ReplyObject Payload() => new ReplyObject().Add("thing", Thing).Add("from_state", FromState).Add("to_state", ToState).Add("last_state", LastState).Add("cost", Cost).Add("tools", Tools).Add("build_state", BuildState);

    /// <summary>The report's step: index, reference_id, name, from_state, to_state, last_state, cost, tools.</summary>
    internal ReplyObject Summary() => new ReplyObject().Add("index", Index).Add("reference_id", ReferenceId).Add("name", Name).Add("from_state", FromState).Add("to_state", ToState).Add("last_state", LastState).Add("cost", Cost).Add("tools", Tools);
}

/// <summary>One id's build finish outcome: the step it would act, or the refusal that stops that id alone.</summary>
internal abstract class FinishResult
{
    private FinishResult()
    {
    }

    internal sealed class Stepped : FinishResult
    {
        internal Stepped(FinishStep step)
        {
            Step = step;
        }

        public FinishStep Step { get; }
    }

    /// <summary>Refused: the id as the call gave it (or the structure's id once it was read) and the error.</summary>
    internal sealed class Failed : FinishResult
    {
        internal Failed(int index, string id, VerbError error)
        {
            Index = index;
            Id = id;
            Error = error;
        }

        public int Index { get; }

        public string Id { get; }

        public VerbError Error { get; }
    }
}

/// <summary>
/// build finish's replies: confirmed, one row per id in the order given (the shared Rows writer), each row that id's
/// step as its payload and a refused or short id its error - one id answers the single shape, a list the rows shape.
/// Until confirm, the one report (Report.Of): every planned structure a target [id, name, state -> target] with the
/// what's undo mark, its step in steps (index, reference_id, name, the states, cost, tools), the call's whole cost in
/// materials (one entry per material, its quantities added up) and its warnings, and every refusal a problem about that
/// id's index. Both read the same per-id steps, so a confirmed reply and its report cannot drift.
/// </summary>
internal static class FinishReplies
{
    /// <summary>The call acted: the rows, or one id's own step as the what's single shape.</summary>
    internal static VerbOutcome Acted(PerIdList ids, IReadOnlyList<FinishResult> results)
    {
        Rows rows = new Rows();
        foreach (FinishResult result in results)
        {
            switch (result)
            {
                case FinishResult.Stepped stepped when stepped.Step.Ready:
                    rows.Succeeded(stepped.Step.ReferenceId, stepped.Step.Payload());
                    break;
                case FinishResult.Stepped stepped:
                    rows.Failed(stepped.Step.ReferenceId, stepped.Step.Problem!);
                    break;
                case FinishResult.Failed failed:
                    rows.Failed(failed.Id, failed.Error);
                    break;
            }
        }

        return rows.For(ids);
    }

    /// <summary>The report: the body (ready, free, from, materials, steps, warnings), every problem, and the targets.</summary>
    internal static ReportReply Reported(IReadOnlyList<FinishResult> results, bool free, string mark)
    {
        List<ReportTarget> targets = new List<ReportTarget>();
        List<ReportProblem> problems = new List<ReportProblem>();
        List<ReplyObject> steps = new List<ReplyObject>();
        List<BuildIssueView> warnings = new List<BuildIssueView>();
        List<BuildCostView> materials = new List<BuildCostView>();
        ThingView? from = null;
        bool ready = true;
        foreach (FinishResult result in results)
        {
            switch (result)
            {
                case FinishResult.Stepped stepped:
                    FinishStep step = stepped.Step;
                    ready &= step.Ready;
                    targets.Add(new ReportTarget(step.ReferenceId, null, step.Name, step.FromState, step.ToState, mark));
                    steps.Add(step.Summary());
                    from ??= step.From;
                    Added(materials, step.Cost);
                    Said(warnings, step.Warnings);
                    if (step.Problem != null)
                    {
                        problems.Add(new ReportProblem(step.Index, step.ReferenceId, step.Problem));
                    }

                    break;
                case FinishResult.Failed failed:
                    ready = false;
                    problems.Add(new ReportProblem(failed.Index, failed.Id, failed.Error));
                    break;
            }
        }

        ReplyObject body = new ReplyObject().Add("ready", ready).Add("free", free).Add("from", from).Add("materials", materials).Add("steps", steps).Add("warnings", warnings);
        return Report.Of(targets, body, problems);
    }

    /// <summary>The call's whole cost: one entry per material, its quantities over every id added up, what the sources hold.</summary>
    private static void Added(List<BuildCostView> merged, List<BuildCostView> cost)
    {
        foreach (BuildCostView entry in cost)
        {
            int at = At(merged, entry.PrefabName);
            if (at < 0)
            {
                merged.Add(new BuildCostView(entry.PrefabName, entry.DisplayName, entry.Quantity, entry.Available,
                    null));
            }
            else
            {
                BuildCostView was = merged[at];
                merged[at] = new BuildCostView(was.PrefabName, was.DisplayName, was.Quantity + entry.Quantity,
                    was.Available ?? entry.Available, null);
            }
        }
    }

    private static int At(List<BuildCostView> merged, string prefabName)
    {
        for (int at = 0; at < merged.Count; at++)
        {
            if (string.Equals(merged[at].PrefabName, prefabName, StringComparison.Ordinal))
            {
                return at;
            }
        }

        return -1;
    }

    /// <summary>The call's warnings, each code once: the same warning is raised for every id the player pays for.</summary>
    private static void Said(List<BuildIssueView> warnings, List<BuildIssueView> said)
    {
        foreach (BuildIssueView warning in said)
        {
            bool known = false;
            for (int at = 0; !known && at < warnings.Count; at++)
            {
                known = string.Equals(warnings[at].Code, warning.Code, StringComparison.Ordinal);
            }

            if (!known)
            {
                warnings.Add(warning);
            }
        }
    }
}
