#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Api.Shared;

/// <summary>
/// The report path of the per-tool writes that take dry_run: dry_run true runs the acting call's checks (WriteCheck:
/// the same code, in the same order) and acts on nothing. The reply starts report_only: true. A verb write reports
/// through its options' ReportConfirm instead (WriteCheck.Outcome).
/// <para>
/// A check that would refuse the acting call does not refuse the report: the report answers ok and lists that refusal
/// in problems, code and message as the acting call would answer them, so a report pass over many writes shows every
/// one that would fail. The report stops where the acting call would stop; in a list each entry's refusal is a problem
/// with its index, and the other entries are still reported. What is not a check of the game is refused, report or
/// not: arguments that do not read (a missing or malformed argument, one that does not go with the call's form; the
/// handler reads them before it runs its checks) and a fault of the mod itself (game_changed).
/// </para>
/// </summary>
internal static class ReportOnly
{
    internal const string Argument = "dry_run";

    /// <summary>Whether the call asks for its report; false (act) when dry_run is left out.</summary>
    internal static bool Asked(Args args) => args.OptionalBool("dry_run") ?? false;
}

/// <summary>A list entry that was refused: its error, which a report lists as a problem.</summary>
internal interface IBatchFailure
{
    ErrorView Error { get; }
}

/// <summary>
/// A write checked against the world: the plan its act applies, or the refusal that stops it. The acting call and its
/// report both start from one Run of the checks, so the report cannot check less than the act does.
/// </summary>
internal sealed class WriteCheck<TPlan> where TPlan : class
{
    private WriteCheck(TPlan? plan, ApiException? refusal)
    {
        Plan = plan;
        Refusal = refusal;
    }

    /// <summary>The checked plan; null when a check refused.</summary>
    internal TPlan? Plan { get; }

    /// <summary>What the acting call would be refused with; null when every check passed.</summary>
    internal ApiException? Refusal { get; }

    /// <summary>
    /// Runs the checks: their plan, or the refusal one of them threw. They must not act. Any other exception is a fault
    /// and is raised as it is.
    /// </summary>
    internal static WriteCheck<TPlan> Run(Func<TPlan> checks)
    {
        try
        {
            return new WriteCheck<TPlan>(checks(), null);
        }
        catch (ApiException refusal)
        {
            return new WriteCheck<TPlan>(null, refusal);
        }
    }

    /// <summary>
    /// The acting call: act on the plan, or raise the refusal. The report: describe the plan, or answer the refusal as
    /// its problem. act is only reached with a plan, and never on a report.
    /// </summary>
    internal object Answer(bool report, Func<TPlan, object> act, Func<TPlan, object> describe)
    {
        if (Plan == null)
        {
            return report ? new ReportRefusedView(Refusal!) : throw Refusal!;
        }

        return report ? describe(Plan) : act(Plan);
    }

    /// <summary>
    /// A verb write's answer (PLAN 2.0, report and confirm): acting, act on the plan, or the refusal as the call's;
    /// reporting, describe the plan (a Report.Of reply), or a report that lists the refusal as its one problem. act is
    /// only reached with a plan, and never on a report.
    /// </summary>
    internal VerbOutcome Outcome(ReportConfirm report, Func<TPlan, VerbOutcome> act, Func<TPlan, JObject> describe)
    {
        if (Plan == null)
        {
            VerbError refused = VerbError.Of(Refusal!);
            return report.Acts
                ? new VerbOutcome.Refused(refused)
                : new VerbOutcome.Answered(Report.Of(Array.Empty<ReportTarget>(), null,
                    new[] { new ReportProblem(null, null, refused) }));
        }

        return report.Acts ? act(Plan) : new VerbOutcome.Answered(describe(Plan));
    }
}

/// <summary>One reason the acting call, or one entry of its list, would be refused.</summary>
internal sealed class ProblemView
{
    internal ProblemView(int? index, string code, string message, object? data = null)
    {
        Index = index;
        Code = code;
        Message = message;
        Data = data;
    }

    /// <summary>The list entry refused; absent for a refusal of the whole call.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public int? Index { get; }

    public string Code { get; }

    public string Message { get; }

    /// <summary>The refusal's data, as the acting call's error carries it (lint_refused's rules); absent for none.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public object? Data { get; }

    /// <summary>Every refused entry of a list's results, as a problem with its index.</summary>
    internal static List<ProblemView> Of(List<BatchItemView> results)
    {
        List<ProblemView> problems = new List<ProblemView>();
        for (int index = 0; index < results.Count; index++)
        {
            if (results[index] is IBatchFailure failure)
            {
                problems.Add(new ProblemView(results[index].Index, failure.Error.Code, failure.Error.Message,
                    failure.Error.Data));
            }
        }

        return problems;
    }
}

/// <summary>A report whose checks stopped where the acting call would be refused: report_only and that problem.</summary>
internal sealed class ReportRefusedView
{
    internal ReportRefusedView(ApiException refusal)
    {
        Problems = new List<ProblemView>(1) { new ProblemView(null, refusal.Code, refusal.Message, refusal.Details) };
    }

    public bool ReportOnly => true;

    public List<ProblemView> Problems { get; }
}
