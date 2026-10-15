#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Profiling;
using StationGodMCP.Pure.Scheduling;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game.Runs;

/// <summary>
/// Confirmed runs of place_* and remove_*, on the shared runner (HeldTickJobs), so they never overlap a swap job.
/// With the game tick held: once it has stopped, the whole preflight runs again over as many frames as it needs and,
/// only if it finds nothing, the removals are made in one frame; once they are gone, the changes and new pieces are
/// built a piece per step (RunBuilder.Building); once the replaced pieces are gone, the result is checked and the tick
/// let go. No power, atmospherics or logic tick
/// sees a device between its old cable and its new one. A pipe run's queued gas changes are applied after the
/// removals and after every piece built, and its contents are checked at the end (JobGas).
/// </summary>
internal static class RunJobs
{
    internal static object Start(RunRequest request, RunReportView preflight, bool wait, string? acknowledgeGasLost) =>
        HeldTickJobs.Start("run", request.Tool,
            id => new RunWaiting(id, request, preflight, Time.realtimeSinceStartup), wait, preflight,
            request.Kind.Family is PipeFamily, acknowledgeGasLost);
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

/// <summary>Waiting for the game tick to stop; then the final check (RunPreflighting).</summary>
internal sealed class RunWaiting : ActiveRun
{
    private readonly float _startedAt;

    internal RunWaiting(string id, RunRequest request, RunReportView preflight, float startedAt)
        : base(id, request, preflight)
    {
        _startedAt = startedAt;
    }

    internal override JobStep Step(double untilMs)
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

        return new RunPreflighting(this, Request.Kind.Family is PipeFamily).Step(untilMs);
    }
}

/// <summary>
/// The tick has stopped: the pipe networks read (JobGas.Opening), then the whole preflight again (RunPlanner.Planning,
/// without the report lint rules), over as many frames as they need, the tick held; a save or the world stopping
/// meanwhile refuses the run, nothing changed. Only a plan with no problem goes on: its removals are made in the step
/// it is ready in.
/// </summary>
internal sealed class RunPreflighting : ActiveRun
{
    private readonly bool _pipes;
    private readonly Work<RunPlan> _plan;
    private JobGas _gas = JobGas.Untracked;

    internal RunPreflighting(ActiveRun waiting, bool pipes)
        : base(waiting.Id, waiting.Request, waiting.Preflight)
    {
        _pipes = pipes;
        _plan = new Work<RunPlan>(answer => Checked(answer));
    }

    internal override JobStep Step(double untilMs)
    {
        if (GameManager.GameState != GameState.Running)
        {
            return JobStep.Finish(Refused(null, new ErrorView("game_not_running",
                "The world stopped running before the run; nothing was changed.")), false);
        }

        if (HeldTickJobs.IsSaving())
        {
            return JobStep.Finish(Refused(null, new ErrorView("save_started",
                "A save started during the final check; nothing was changed. Run it again.")), false);
        }

        try
        {
            if (!_plan.Step(StopwatchClock.Instance, untilMs))
            {
                return JobStep.Next(this);
            }
        }
        catch (ApiException refusal)
        {
            return JobStep.Finish(Refused(null, new ErrorView(refusal.Code, refusal.Message)), true);
        }

        RunPlan plan = _plan.Value;
        RunPlanner.RequireAssumedGone(plan);
        if (!plan.Ready)
        {
            return JobStep.Finish(Refused(RunReports.Of(plan, RunReports.Refused, Id), new ErrorView(
                "final_check_failed",
                $"The check once the tick had stopped found {plan.Problems.Count} problem(s); nothing was " +
                "changed.")), true);
        }

        RunReportView finalCheck = RunReports.Of(plan, RunReports.Scheduled, Id);
        RunOutcome outcome = new RunOutcome();
        using (Prof.Scope(ProfId.JobApply))
        {
            RunBuilder.Remove(plan, outcome);
        }

        return JobStep.Next(new RunAwaitingRemovals(this, plan, outcome, finalCheck, _gas));
    }

    private IEnumerable<object?> Checked(Answer<RunPlan> answer)
    {
        Answer<JobGas> gas = new Answer<JobGas>();
        foreach (object? step in JobGas.Opening(_pipes, gas))
        {
            yield return step;
        }

        _gas = gas.Value;
        foreach (object? step in RunPlanner.Planning(Request, false, answer))
        {
            yield return step;
        }
    }
}

/// <summary>Removed; waiting for Unity to destroy the removed pieces, then the build (RunBuilding).</summary>
internal sealed class RunAwaitingRemovals : ActiveRun
{
    private const int MaximumFrames = 30;

    private readonly RunPlan _plan;
    private readonly RunOutcome _outcome;
    private readonly RunReportView _finalCheck;
    private readonly JobGas _gas;
    private int _frames;

    internal RunAwaitingRemovals(ActiveRun waiting, RunPlan plan, RunOutcome outcome, RunReportView finalCheck,
        JobGas gas)
        : base(waiting.Id, waiting.Request, waiting.Preflight)
    {
        _plan = plan;
        _outcome = outcome;
        _finalCheck = finalCheck;
        _gas = gas;
    }

    internal override object Failed(ErrorView error) =>
        new RunJobView(Id, Request.Tool, "stopped", Preflight,
            new RunJobResultView(_finalCheck, _outcome.Log, null, error));

    internal override JobStep Step(double untilMs)
    {
        _frames++;
        List<SmallGrid> removed = new List<SmallGrid>();
        foreach (PlannedRemoval removal in _plan.Removals)
        {
            if (!removal.Assumed && !RunBuilder.SwappedInBuild(_plan, removal))
            {
                removed.Add(removal.Piece);
            }
        }

        if (RunGone.AnyRemain(removed) && _frames < MaximumFrames)
        {
            return JobStep.Next(this);
        }

        // The removed pieces' splits (Pipe.OnDestroy, at the end of the removal frame) land before any merge.
        _gas.Settle();
        return _outcome.Log.StoppedAt == null
            ? JobStep.Next(new RunBuilding(this, _plan, _outcome, _finalCheck, _gas))
            : JobStep.Next(new RunAwaitingCheck(this, _plan, _outcome, _finalCheck, _gas));
    }
}

/// <summary>The removals gone: the changes and new pieces built a piece per step (RunBuilder.Building).</summary>
internal sealed class RunBuilding : ActiveRun
{
    private readonly RunPlan _plan;
    private readonly RunOutcome _outcome;
    private readonly RunReportView _finalCheck;
    private readonly JobGas _gas;
    private readonly Work<bool> _build;

    internal RunBuilding(ActiveRun removed, RunPlan plan, RunOutcome outcome, RunReportView finalCheck, JobGas gas)
        : base(removed.Id, removed.Request, removed.Preflight)
    {
        _plan = plan;
        _outcome = outcome;
        _finalCheck = finalCheck;
        _gas = gas;
        _build = new Work<bool>(answer => Built(answer));
    }

    internal override object Failed(ErrorView error) =>
        new RunJobView(Id, Request.Tool, "stopped", Preflight,
            new RunJobResultView(_finalCheck, _outcome.Log, null, error));

    internal override JobStep Step(double untilMs)
    {
        bool done;
        using (Prof.Scope(ProfId.JobApply))
        {
            done = _build.Step(StopwatchClock.Instance, untilMs);
        }

        return done
            ? JobStep.Next(new RunAwaitingCheck(this, _plan, _outcome, _finalCheck, _gas))
            : JobStep.Next(this);
    }

    private IEnumerable<object?> Built(Answer<bool> answer)
    {
        foreach (object? step in RunBuilder.Building(_plan, _outcome, _gas))
        {
            yield return step;
        }

        answer.Value = true;
    }
}

/// <summary>Built; waiting for Unity to destroy the replaced pieces, then checking the result (over frames).</summary>
internal sealed class RunAwaitingCheck : ActiveRun
{
    private const int MaximumFrames = 30;

    private readonly RunPlan _plan;
    private readonly RunOutcome _outcome;
    private readonly RunReportView _finalCheck;
    private readonly JobGas _gas;
    private int _frames;
    private GasCheckView? _gasCheck;
    private Work<RunVerificationView>? _check;

    internal RunAwaitingCheck(ActiveRun previous, RunPlan plan, RunOutcome outcome, RunReportView finalCheck,
        JobGas gas)
        : base(previous.Id, previous.Request, previous.Preflight)
    {
        _plan = plan;
        _outcome = outcome;
        _finalCheck = finalCheck;
        _gas = gas;
    }

    // The run is done; only the check after it failed.
    internal override object Failed(ErrorView error) =>
        new RunJobView(Id, Request.Tool, "applied_unchecked", Preflight,
            new RunJobResultView(_finalCheck, _outcome.Log, null, error));

    internal override JobStep Step(double untilMs)
    {
        if (_check == null)
        {
            _frames++;
            if (RunGone.AnyRemain(_outcome.Replaced) && _frames < MaximumFrames)
            {
                return JobStep.Next(this);
            }

            _check = new Work<RunVerificationView>(answer => Checked(answer));
        }

        if (!_check.Step(StopwatchClock.Instance, untilMs))
        {
            return JobStep.Next(this);
        }

        GasCheckView? gasCheck = _gasCheck;
        RunVerificationView verification = _check.Value;
        string status = _outcome.Log.StoppedAt != null ? "stopped"
            : verification.Ok ? "applied"
            : "applied_with_differences";
        return JobStep.Finish(new RunJobView(Id, Request.Tool, GasCheckView.JobStatus(status, gasCheck), Preflight,
            new RunJobResultView(_finalCheck, _outcome.Log, verification, null, gasCheck)), true);
    }

    // The gas check (JobGas.Closing), then the run's check, each in steps of its own.
    private IEnumerable<object?> Checked(Answer<RunVerificationView> answer)
    {
        Answer<GasCheckView?> gas = new Answer<GasCheckView?>();
        foreach (object? step in _gas.Closing(Id, gas))
        {
            yield return step;
        }

        _gasCheck = gas.Value;
        foreach (object? step in RunCheck.Verifying(_plan, _outcome, answer))
        {
            yield return step;
        }
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
