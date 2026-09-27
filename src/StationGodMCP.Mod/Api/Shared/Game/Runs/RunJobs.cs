#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using StationGodMCP.Api.Views;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game.Runs;

/// <summary>
/// Confirmed runs of place_* and remove_*, on the shared runner (HeldTickJobs), so they never overlap a swap job.
/// With the game tick held: once it has stopped, the whole preflight runs again and, only if it finds nothing, the
/// removals are made in that frame; the frame after they are gone, every change and new piece is built in one frame;
/// once the replaced pieces are gone, the result is checked and the tick let go. No power, atmospherics or logic tick
/// sees a device between its old cable and its new one.
/// </summary>
internal static class RunJobs
{
    internal static object Start(RunRequest request, RunReportView preflight) =>
        HeldTickJobs.Start("run", id => new RunWaiting(id, request, preflight, Time.realtimeSinceStartup)).View();
}

/// <summary>A running run job in one of its states.</summary>
internal abstract class ActiveRun : HeldTickJob
{
    protected ActiveRun(string id, RunRequest request, RunReportView preflight)
        : base(id)
    {
        Request = request;
        Preflight = preflight;
    }

    internal RunRequest Request { get; }

    internal RunReportView Preflight { get; }

    internal override object View() => new RunJobView(Id, Request.Tool, "waiting", Preflight, null);

    internal override object Failed(ErrorView error) =>
        new RunJobView(Id, Request.Tool, "refused", Preflight, new RunJobResultView(null, null, null, error));

    protected RunJobView Refused(RunReportView? finalCheck, ErrorView error) =>
        new RunJobView(Id, Request.Tool, "refused", Preflight, new RunJobResultView(finalCheck, null, null, error));
}

/// <summary>Waiting for the game tick to stop; then the final check and the removals, in one frame.</summary>
internal sealed class RunWaiting : ActiveRun
{
    private readonly float _startedAt;

    internal RunWaiting(string id, RunRequest request, RunReportView preflight, float startedAt)
        : base(id, request, preflight)
    {
        _startedAt = startedAt;
    }

    internal override JobStep Step()
    {
        if (GameManager.GameState != GameState.Running)
        {
            return JobStep.Finish(Refused(null, new ErrorView("game_not_running",
                "The world stopped running before the run; nothing was changed.")), false);
        }

        if (HeldTickJobs.IsSaving())
        {
            return JobStep.Finish(Refused(null, new ErrorView("save_started",
                "A save started while waiting for the game tick; nothing was changed. Run it again.")), false);
        }

        if (!GameManager.GameTickPaused)
        {
            return HeldTickJobs.TickTimedOut(_startedAt)
                ? JobStep.Finish(Refused(null, new ErrorView("tick_not_stopped",
                    "The game tick did not stop in time; nothing was changed.")), true)
                : JobStep.Next(this);
        }

        RunPlan plan;
        try
        {
            plan = RunPlanner.Plan(Request);
        }
        catch (ApiException refusal)
        {
            return JobStep.Finish(Refused(null, new ErrorView(refusal.Code, refusal.Message)), true);
        }

        if (!plan.Ready)
        {
            return JobStep.Finish(Refused(RunReports.Of(plan, RunReports.Refused, Id), new ErrorView(
                "final_check_failed",
                $"The check once the tick had stopped found {plan.Problems.Count} problem(s); nothing was " +
                "changed.")), true);
        }

        RunReportView finalCheck = RunReports.Of(plan, RunReports.Scheduled, Id);
        RunOutcome outcome = new RunOutcome();
        RunBuilder.Remove(plan, outcome);
        return JobStep.Next(new RunAwaitingRemovals(this, plan, outcome, finalCheck));
    }
}

/// <summary>Removed; waiting for Unity to destroy the removed pieces, then building every cell in one frame.</summary>
internal sealed class RunAwaitingRemovals : ActiveRun
{
    private const int MaximumFrames = 30;

    private readonly RunPlan _plan;
    private readonly RunOutcome _outcome;
    private readonly RunReportView _finalCheck;
    private int _frames;

    internal RunAwaitingRemovals(ActiveRun waiting, RunPlan plan, RunOutcome outcome, RunReportView finalCheck)
        : base(waiting.Id, waiting.Request, waiting.Preflight)
    {
        _plan = plan;
        _outcome = outcome;
        _finalCheck = finalCheck;
    }

    internal override object Failed(ErrorView error) =>
        new RunJobView(Id, Request.Tool, "stopped", Preflight,
            new RunJobResultView(_finalCheck, _outcome.Log, null, error));

    internal override JobStep Step()
    {
        _frames++;
        List<SmallGrid> removed = new List<SmallGrid>();
        foreach (PlannedRemoval removal in _plan.Removals)
        {
            removed.Add(removal.Piece);
        }

        if (RunGone.AnyRemain(removed) && _frames < MaximumFrames)
        {
            return JobStep.Next(this);
        }

        if (_outcome.Log.StoppedAt == null)
        {
            RunBuilder.Build(_plan, _outcome);
        }

        return JobStep.Next(new RunAwaitingCheck(this, _plan, _outcome, _finalCheck));
    }
}

/// <summary>Built; waiting for Unity to destroy the replaced pieces, then checking the result.</summary>
internal sealed class RunAwaitingCheck : ActiveRun
{
    private const int MaximumFrames = 30;

    private readonly RunPlan _plan;
    private readonly RunOutcome _outcome;
    private readonly RunReportView _finalCheck;
    private int _frames;

    internal RunAwaitingCheck(ActiveRun previous, RunPlan plan, RunOutcome outcome, RunReportView finalCheck)
        : base(previous.Id, previous.Request, previous.Preflight)
    {
        _plan = plan;
        _outcome = outcome;
        _finalCheck = finalCheck;
    }

    // The run is done; only the check after it failed.
    internal override object Failed(ErrorView error) =>
        new RunJobView(Id, Request.Tool, "applied_unchecked", Preflight,
            new RunJobResultView(_finalCheck, _outcome.Log, null, error));

    internal override JobStep Step()
    {
        _frames++;
        if (RunGone.AnyRemain(_outcome.Replaced) && _frames < MaximumFrames)
        {
            return JobStep.Next(this);
        }

        RunVerificationView verification = RunCheck.Verify(_plan, _outcome);
        string status = _outcome.Log.StoppedAt != null ? "stopped"
            : verification.Ok ? "applied"
            : "applied_with_differences";
        return JobStep.Finish(new RunJobView(Id, Request.Tool, status, Preflight,
            new RunJobResultView(_finalCheck, _outcome.Log, verification, null)), true);
    }
}

internal static class RunGone
{
    // Thing.OnDestroy deregisters the reference, so Find answers null once Unity has destroyed it.
    internal static bool AnyRemain(List<SmallGrid> pieces)
    {
        foreach (SmallGrid piece in pieces)
        {
            if (piece != null && Thing.Find(piece.ReferenceId) != null)
            {
                return true;
            }
        }

        return false;
    }
}
