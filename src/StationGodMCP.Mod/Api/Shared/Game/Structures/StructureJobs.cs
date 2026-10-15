#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Rooms;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Profiling;
using StationGodMCP.Pure.Scheduling;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game.Structures;

/// <summary>
/// Confirmed swap wall and swap frame runs, on the shared runner (HeldTickJobs), so they never overlap an
/// upgrade or clean job. The tick is held; once it has stopped, the whole preflight runs again over as many frames as
/// it needs and, only if it finds nothing, the pieces are swapped one per step (StructureSwapper), the tick held
/// throughout. Once Unity has destroyed the old pieces the
/// result is checked with the tick still held, then the tick is let go; once the game has run its ticks and emptied
/// its room queue, the tick is held once more for a consistent look at the rooms, and let go for good.
/// </summary>
internal static class StructureJobs
{
    internal static object Start(StructureSwapRequest request, StructureSwapPlan plan, bool wait) =>
        HeldTickJobs.Start("replace", request.Family.Tool, id => new StructureWaitingForTick(id, request,
            StructureReports.Of(plan, StructureReports.Scheduled, id), Time.realtimeSinceStartup), wait, null, false,
            null);
}

/// <summary>A running replace job in one of its states.</summary>
internal abstract class ActiveStructureSwap : HeldTickJob
{
    protected ActiveStructureSwap(string id, StructureSwapRequest request, StructureSwapReportView preflight)
        : base(id)
    {
        Request = request;
        Preflight = preflight;
    }

    internal StructureSwapRequest Request { get; }

    internal StructureSwapReportView Preflight { get; }

    protected virtual string RunningStatus => "waiting";

    internal override object View() => new StructureSwapJobView(Id, Request.Family.Tool, RunningStatus, Preflight,
        null);

    internal override object Failed(ErrorView error) => Refused(null, error);

    protected StructureSwapJobView Refused(StructureSwapReportView? finalCheck, ErrorView error) =>
        new StructureSwapJobView(Id, Request.Family.Tool, "refused", Preflight,
            new StructureSwapJobResult(finalCheck, new StructureSwapLogView(), StructureChecks.None, error));
}

/// <summary>Waiting for the game tick to stop; then the final check (StructurePreflighting).</summary>
internal sealed class StructureWaitingForTick : ActiveStructureSwap
{
    private readonly float _startedAt;

    internal StructureWaitingForTick(string id, StructureSwapRequest request, StructureSwapReportView preflight,
        float startedAt)
        : base(id, request, preflight)
    {
        _startedAt = startedAt;
    }

    internal override JobStep Step(double untilMs)
    {
        if (GameManager.GameState != GameState.Running)
        {
            return JobStep.Finish(Refused(null, new ErrorView("game_not_running",
                "The world stopped running before the swap; nothing was changed.")), false);
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

        return new StructurePreflighting(this).Step(untilMs);
    }
}

/// <summary>
/// The tick has stopped: the whole preflight again (StructureSwapPlanner.Planning) over as many frames as it needs,
/// the tick held; a save or the world stopping meanwhile refuses the run, nothing changed. A plan with problems is
/// refused.
/// </summary>
internal sealed class StructurePreflighting : ActiveStructureSwap
{
    private readonly Work<StructureSwapPlan> _plan;

    internal StructurePreflighting(ActiveStructureSwap waiting)
        : base(waiting.Id, waiting.Request, waiting.Preflight)
    {
        _plan = new Work<StructureSwapPlan>(answer => StructureSwapPlanner.Planning(Request, answer));
    }

    internal override JobStep Step(double untilMs)
    {
        if (GameManager.GameState != GameState.Running)
        {
            return JobStep.Finish(Refused(null, new ErrorView("game_not_running",
                "The world stopped running before the swap; nothing was changed.")), false);
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

        StructureSwapPlan plan = _plan.Value;
        if (!plan.Ready)
        {
            StructureSwapReportView refused = StructureReports.Of(plan, StructureReports.Refused, Id);
            return JobStep.Finish(Refused(refused, new ErrorView("final_check_failed",
                $"The check once the tick had stopped found {plan.Problems.Count} problem(s); nothing was " +
                "changed.")), true);
        }

        return JobStep.Next(new StructureSwapping(this, plan, StructureReports.Of(plan, StructureReports.Scheduled,
            Id)));
    }
}

/// <summary>Checked: the pieces swapped one per step (StructureSwapper.Steps), the tick held throughout.</summary>
internal sealed class StructureSwapping : ActiveStructureSwap
{
    private readonly StructureSwapPlan _plan;
    private readonly StructureSwapReportView _finalCheck;
    private readonly StructureSwapLogView _log = new StructureSwapLogView();
    private readonly Dictionary<long, Structure> _replacements = new Dictionary<long, Structure>();
    private readonly Work<bool> _swap;

    internal StructureSwapping(ActiveStructureSwap checkedRun, StructureSwapPlan plan,
        StructureSwapReportView finalCheck)
        : base(checkedRun.Id, checkedRun.Request, checkedRun.Preflight)
    {
        _plan = plan;
        _finalCheck = finalCheck;
        _swap = new Work<bool>(answer => Swapped(answer));
    }

    // Swapping had begun: what it did so far stands.
    internal override object Failed(ErrorView error) =>
        new StructureSwapJobView(Id, Request.Family.Tool, "stopped", Preflight,
            new StructureSwapJobResult(_finalCheck, _log, StructureChecks.None, error));

    internal override JobStep Step(double untilMs)
    {
        bool done;
        using (Prof.Scope(ProfId.JobApply))
        {
            done = _swap.Step(StopwatchClock.Instance, untilMs);
        }

        return done
            ? JobStep.Next(new StructureAwaitingOldGone(this, new StructureSwapOutcome(_plan, _finalCheck, _log,
                _replacements)))
            : JobStep.Next(this);
    }

    private IEnumerable<object?> Swapped(Answer<bool> answer)
    {
        foreach (object? step in StructureSwapper.Steps(_plan, _replacements, _log))
        {
            yield return step;
        }

        answer.Value = true;
    }
}

/// <summary>A swap as done: the plan, the final check, the log, and each replacement by its old piece's id.</summary>
internal sealed class StructureSwapOutcome
{
    internal StructureSwapOutcome(StructureSwapPlan plan, StructureSwapReportView finalCheck, StructureSwapLogView log,
        Dictionary<long, Structure> replacements)
    {
        Plan = plan;
        FinalCheck = finalCheck;
        Log = log;
        Replacements = replacements;
    }

    internal StructureSwapPlan Plan { get; }

    internal StructureSwapReportView FinalCheck { get; }

    internal StructureSwapLogView Log { get; }

    internal Dictionary<long, Structure> Replacements { get; }

    internal HashSet<long> SwappedIds => new HashSet<long>(Replacements.Keys);

    internal StructureSwapJobView View(string id, string tool, StructureSwapReportView preflight, string status,
        StructureChecks checks, ErrorView? error = null) =>
        new StructureSwapJobView(id, tool, status, preflight,
            new StructureSwapJobResult(FinalCheck, Log, checks, error));
}

/// <summary>
/// Swapped with the tick held; waiting for Unity to destroy the old pieces (end of the swap frame), then the held
/// check (StructureCheck.Held), then the tick is let go.
/// </summary>
internal sealed class StructureAwaitingOldGone : ActiveStructureSwap
{
    private const int MaximumFrames = 30;

    private readonly StructureSwapOutcome _outcome;
    private int _frames;

    internal StructureAwaitingOldGone(ActiveStructureSwap waiting, StructureSwapOutcome outcome)
        : base(waiting.Id, waiting.Request, waiting.Preflight)
    {
        _outcome = outcome;
    }

    protected override string RunningStatus => "verifying";

    internal override object Failed(ErrorView error) =>
        _outcome.View(Id, Request.Family.Tool, Preflight, "applied_unchecked", StructureChecks.None, error);

    internal override JobStep Step(double untilMs)
    {
        _frames++;
        if (OldPiecesRemain() && _frames < MaximumFrames)
        {
            return JobStep.Next(this);
        }

        StructureHeldCheckView held = StructureCheck.Held(_outcome);
        return JobStep.Release(new StructureAwaitingRooms(this, _outcome, held, GameManager.GameTickCount,
            Time.realtimeSinceStartup));
    }

    private bool OldPiecesRemain()
    {
        foreach (long id in _outcome.Replacements.Keys)
        {
            // Thing.OnDestroy deregisters the reference, so Find answers null once Unity has destroyed it.
            if (Thing.Find(id) != null)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// The tick runs again; waiting until the game has run its ticks (the gas events, then RoomEvaluator.ThreadedWork)
/// and its room queue is empty, or until the wait runs out; then the tick is held again for the room check.
/// </summary>
internal sealed class StructureAwaitingRooms : ActiveStructureSwap
{
    // GameTickCount rises after the tick that was running when the hold began, so three rises mean at least two
    // whole ticks ran after the swap.
    private const uint TicksToRun = 3;
    private const float WaitSeconds = 10f;

    private readonly StructureSwapOutcome _outcome;
    private readonly StructureHeldCheckView _held;
    private readonly uint _tickAtRelease;
    private readonly float _releasedAt;

    internal StructureAwaitingRooms(ActiveStructureSwap previous, StructureSwapOutcome outcome,
        StructureHeldCheckView held, uint tickAtRelease, float releasedAt)
        : base(previous.Id, previous.Request, previous.Preflight)
    {
        _outcome = outcome;
        _held = held;
        _tickAtRelease = tickAtRelease;
        _releasedAt = releasedAt;
    }

    protected override string RunningStatus => "verifying";

    internal override object Failed(ErrorView error) =>
        _outcome.View(Id, Request.Family.Tool, Preflight, "applied_unchecked", new StructureChecks(_held, null), error);

    internal override JobStep Step(double untilMs)
    {
        bool ticked = unchecked(GameManager.GameTickCount - _tickAtRelease) >= TicksToRun;
        bool settled = ticked && RoomEvaluator.Instance != null && RoomEvaluator.Instance.PendingCount == 0;
        if (!settled && Time.realtimeSinceStartup - _releasedAt < WaitSeconds)
        {
            return JobStep.Next(this);
        }

        int ticksRun = (int)unchecked(GameManager.GameTickCount - _tickAtRelease);
        return JobStep.Hold(new StructureCheckingRooms(this, _outcome, _held, settled, ticksRun));
    }
}

/// <summary>The tick is held again: once it has stopped, the rooms are checked and it is let go.</summary>
internal sealed class StructureCheckingRooms : ActiveStructureSwap
{
    private readonly StructureSwapOutcome _outcome;
    private readonly StructureHeldCheckView _held;
    private readonly bool _settled;
    private readonly int _ticksRun;
    private readonly float _heldAt;

    internal StructureCheckingRooms(ActiveStructureSwap previous, StructureSwapOutcome outcome,
        StructureHeldCheckView held, bool settled, int ticksRun)
        : base(previous.Id, previous.Request, previous.Preflight)
    {
        _outcome = outcome;
        _held = held;
        _settled = settled;
        _ticksRun = ticksRun;
        _heldAt = Time.realtimeSinceStartup;
    }

    protected override string RunningStatus => "verifying";

    internal override object Failed(ErrorView error) =>
        _outcome.View(Id, Request.Family.Tool, Preflight, "applied_unchecked", new StructureChecks(_held, null), error);

    internal override JobStep Step(double untilMs)
    {
        if (!GameManager.GameTickPaused && !HeldTickJobs.TickTimedOut(_heldAt) &&
            GameManager.GameState == GameState.Running)
        {
            return JobStep.Next(this);
        }

        StructureRoomCheckView rooms = _outcome.Plan.Air!.CheckRooms(_outcome.SwappedIds, _settled, _ticksRun);
        string status = _outcome.Log.StoppedAt != null ? "stopped"
            : _held.Ok && rooms.Ok ? "applied"
            : "applied_with_differences";
        return JobStep.Finish(_outcome.View(Id, Request.Family.Tool, Preflight, status,
            new StructureChecks(_held, rooms)), true);
    }
}
