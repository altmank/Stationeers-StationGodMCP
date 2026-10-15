#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Scheduling;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Api.Shared;

/// <summary>
/// A write checked against the world: the plan its act applies, or the refusal that stops it. The acting call and its
/// report both start from one Run of the checks, so the report cannot check less than the act does. Without confirm the
/// report answers the plan (a Report.Of reply) and lists what would refuse it, each problem with the entry it is about;
/// a report whose checks stopped where the acting call would be refused answers report_only and that refusal as its one
/// problem. What is not a check of the game is refused, report or not: arguments that do not read (a missing or
/// malformed argument, one that does not go with the call's form; the handler reads them before it runs its checks) and
/// a fault of the mod itself (game_changed).
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
    /// A write whose checks are work spread over frames (A3): the planner body is stepped in the call's frame and then in
    /// the slice lane (WorkResult); a refusal it throws at any step is the check's refusal, as Run's is; then Outcome
    /// answers. act and describe run in the step the plan is ready in.
    /// </summary>
    internal static VerbOutcome Planned(Func<Answer<TPlan>, IEnumerable<object?>> checks, ReportConfirm report,
        Func<TPlan, VerbOutcome> act, Func<TPlan, object> describe) =>
        WorkResult<WriteCheck<TPlan>>.Of(answer => Stepped(checks, answer),
            check => check.Outcome(report, act, describe));

    /// <summary>
    /// The checks as a body (a planner's work over frames), its plan or the refusal one of its steps threw. Any other
    /// exception is a fault and is raised as it is.
    /// </summary>
    internal static IEnumerable<object?> Stepped(Func<Answer<TPlan>, IEnumerable<object?>> checks,
        Answer<WriteCheck<TPlan>> answer)
    {
        Answer<TPlan> plan = new Answer<TPlan>();
        using IEnumerator<object?> steps = checks(plan).GetEnumerator();
        ApiException? refused = null;
        while (true)
        {
            bool more;
            try
            {
                more = steps.MoveNext();
            }
            catch (ApiException refusal)
            {
                refused = refusal;
                break;
            }

            if (!more)
            {
                break;
            }

            yield return null;
        }

        answer.Value = refused != null ? new WriteCheck<TPlan>(null, refused) : new WriteCheck<TPlan>(plan.Value, null);
    }

    /// <summary>
    /// A verb write's answer (PLAN 2.0, report and confirm): acting, act on the plan, or the refusal as the call's;
    /// reporting, describe the plan (a Report.Of reply), or a report that lists the refusal as its one problem. act is
    /// only reached with a plan, and never on a report.
    /// </summary>
    internal VerbOutcome Outcome(ReportConfirm report, Func<TPlan, VerbOutcome> act, Func<TPlan, object> describe)
    {
        if (Plan == null)
        {
            VerbError refused = VerbError.Of(Refusal!);
            return report.Acts
                ? new VerbOutcome.Refused(refused)
                : new VerbOutcome.Answered(Report.Of(Array.Empty<ReportTarget>(), null,
                    new[] { new ReportProblem(null, null, refused) }));
        }

        return report.Acts ? act(Plan) : VerbOutcome.Of(describe(Plan));
    }
}
