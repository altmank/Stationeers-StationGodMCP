#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// build finish's call, checked without the game: the structures to build on, one or a list in order (a ref the hub
/// expanded to one); the build state to reach, default the one above the current state; free: true, the creative
/// world's cheat that takes no materials; the holders that pay (materials); and confirm: until confirm the call reports
/// what it would do, and confirm acts at once, one structure at a time, each failing alone.
/// </summary>
internal sealed class FinishOrder
{
    private FinishOrder(PerIdList ids, long? state, bool free, MaterialsSource materials, ReportConfirm report)
    {
        Label = BuildFinishOptions.Spec.Label;
        Mark = BuildFinishOptions.Spec.Write!.UndoMark;
        Ids = ids;
        State = state;
        Free = free;
        Materials = materials;
        Report = report;
    }

    /// <summary>The call's label, as its refusals prefix it: "build finish".</summary>
    public string Label { get; }

    /// <summary>The what's undo mark, as its report targets carry it ("[cannot undo: change job]").</summary>
    public string Mark { get; }

    /// <summary>The structures to build on, in order: one id, or a list of them.</summary>
    public PerIdList Ids { get; }

    /// <summary>The build state to reach; null: the one above the current state.</summary>
    public long? State { get; }

    /// <summary>free: a creative world's cheat, with no materials taken.</summary>
    public bool Free { get; }

    /// <summary>Who pays the materials, in order.</summary>
    public MaterialsSource Materials { get; }

    /// <summary>confirm and rows, as the call gives them.</summary>
    public ReportConfirm Report { get; }

    /// <summary>True when the call acts (confirm: true), false when it only reports.</summary>
    internal bool Acts => Report.Acts;

    /// <summary>The state a step reaches from current: the state the call asks for, else the next one.</summary>
    internal int Target(int current) => State is { } asked ? (int)asked : current + 1;

    /// <summary>
    /// The order build finish's options give. The what's id is required, so a call that reaches this names one
    /// structure or a list of them; every other option is its own default when left out.
    /// </summary>
    internal static FinishOrder Of(BuildFinishOptions options) =>
        new FinishOrder(options.Selector.Ids!, options.State, options.Free ?? false, options.Materials, options.Report);
}

/// <summary>
/// Why a build step is refused, as the reply writes it: the rule's word as its code and message (BuildStepRule), and a
/// state the call cannot reach as a problem about its state option, in verb words.
/// </summary>
internal static class FinishRefusals
{
    /// <summary>The refusal's error, or null when the step is refused for nothing (BuildStepRefusal.None).</summary>
    internal static VerbError? Of(BuildStepRefusal refusal, string name, int current, int last, string label) =>
        refusal switch
        {
            BuildStepRefusal.None => null,
            BuildStepRefusal.SingleState => new VerbError("no_build_states",
                $"{name} has a single build state, so nothing to build on."),
            BuildStepRefusal.Broken => new VerbError("structure_broken",
                $"{name} is in its broken build state; the game cannot build on it."),
            BuildStepRefusal.Complete => new VerbError("already_complete",
                $"{name} is complete (build state {current} of {last})."),
            BuildStepRefusal.Damaged => new VerbError("structure_damaged",
                $"{name} is damaged; the game builds on it only once it is repaired."),
            _ => OptionProblem.Refusal(label,
                new[] { new OptionProblem("state", $"must be above {current} and at most {last}") })
        };
}
