#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Profiling;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// The confirmed runs of every swap tool (upgrade_*, clean_*, replace_*), one at a time across all of them. Starting
/// one asks the game to hold its tick (GameManager.PauseGameTick, what a save does); every frame after that
/// (StationGodMod.Update) moves the job on. A job lets the tick go when it finishes, or earlier when a step says so
/// and goes on without it (a check that needs the game to run), and may hold it again later. A save that starts
/// meanwhile owns the tick: the runner never lets the tick go while a save is running. A run that finds the slot
/// taken answers busy with the running job's id; with wait it is queued (up to MaximumWaiting) and started, in
/// order, once the slot is free and nothing else holds the tick. A queued job's own first step runs its whole
/// preflight again, so the world the earlier jobs left is what it is checked against. While a job's atmosphere work
/// that overran its limit is still running (AtmosphericsThread.Busy) no step runs, no queued job starts and the tick is
/// not let go: a release asked for meanwhile is owed and made once that work has ended. With clients connected, no
/// step runs until a state packet written after the end of the last step's frame has gone out (JobReplication), so a
/// client replays each step's changes in the order the host made them.
/// </summary>
internal static class HeldTickJobs
{
    private const float TickWaitSeconds = 15f;
    private const int KeptJobs = 16;
    internal const int MaximumWaiting = 8;

    private static readonly Dictionary<string, object> Finished = new Dictionary<string, object>();
    private static readonly Queue<string> FinishedOrder = new Queue<string>();
    private static readonly JobLine<QueuedJob> Waiting = new JobLine<QueuedJob>(MaximumWaiting);

    // What the gas hold meant for each job that acknowledged a loss, so a poll's reply repeats its gas_hold.
    private static readonly Dictionary<string, JobHold> Holds = new Dictionary<string, JobHold>();
    private static readonly HoldLengths HoldTimes = new HoldLengths();
    private static HeldTickJob? _active;
    private static bool _tickHeld;
    private static bool _releaseOwed;
    private static long _next;

    /// <summary>A job holds the game tick now (the dispatcher's request budget is smaller then).</summary>
    internal static bool HoldsTick => _tickHeld;

    /// <summary>How long jobs held the tick since the mod loaded, by HoldLengths bucket (mod_info runtime.job_holds).</summary>
    internal static long[] HoldCounts() => HoldTimes.Counts();

    /// <summary>
    /// Starts a job made for its id (prefix-number) and returns its view. While another job runs (or a save or
    /// something else holds the tick): busy with the running job's id, or with wait the job is queued and its
    /// queued view returned. A job that changes pipe networks is refused while a gas check has failed (GasHold),
    /// unless acknowledge names the job that failed it: the hold is then lifted once this job starts (a queued one when
    /// it leaves the queue, if the hold is still that job's then). The reply's gas_hold says which (GasHoldReply).
    /// </summary>
    internal static object Start(string prefix, string tool, Func<string, HeldTickJob> create, bool wait,
        object? preflight, bool pipeNetworks, string? acknowledge)
    {
        if (GameManager.GameState != GameState.Running)
        {
            throw ApiErrors.Refused("game_not_running", "The world is not running.");
        }

        GasHoldVerdict hold = GasHold.Judge(pipeNetworks, acknowledge);
        if (hold is GasHoldVerdict.Refusing refusing)
        {
            throw GasHold.Refusal(refusing);
        }

        bool tickTaken = IsSaving() || GameManager.GameTickPaused || AtmosphericsThread.Busy;
        if (_active == null && Waiting.Count == 0 && !tickTaken)
        {
            string started = NextId(prefix);
            object view = Launch(started, tool, create).View();
            GasHold.Lift(hold);
            GasHoldReply.Record(hold, GasHoldStage.Started);
            Keep(started, hold, GasHoldStage.Started);
            return view;
        }

        if (!wait)
        {
            if (_active == null && AtmosphericsThread.Busy)
            {
                throw ApiErrors.Refused("tick_held",
                    "An earlier job's atmosphere work ran past its time limit and is still running; the tick stays " +
                    "held until it ends. Try again, or pass wait: true to queue the run.");
            }

            if (_active == null && tickTaken)
            {
                throw ApiErrors.Refused("tick_held",
                    "The game tick is held by something else (a save or a world settings change); try again, or " +
                    "pass wait: true to queue the run.");
            }

            GasHoldReply.Record(hold, GasHoldStage.NotStarted);
            return new JobBusyView(tool, _active?.Id ?? string.Empty, Waiting.Count,
                (_active != null ? $"Job {_active.Id} is still running" : "Jobs are waiting for the slot") +
                "; nothing was changed. Poll it with job_id, or pass wait: true to queue this run behind it.");
        }

        string id = NextId(prefix);
        if (!Waiting.Add(id, new QueuedJob(tool, create, pipeNetworks, acknowledge)))
        {
            GasHoldReply.Record(hold, GasHoldStage.NotStarted);
            return new JobBusyView(tool, _active?.Id ?? string.Empty, Waiting.Count,
                $"{MaximumWaiting} runs are already queued; nothing was changed. Try again once one has started.");
        }

        GasHoldReply.Record(hold, GasHoldStage.Queued);
        Keep(id, hold, GasHoldStage.Queued);
        return new JobQueuedView(id, tool, Waiting.PositionOf(id) ?? 1, _active?.Id, preflight);
    }

    private static string NextId(string prefix) => prefix + "-" + (++_next).ToString(CultureInfo.InvariantCulture);

    private static HeldTickJob Launch(string id, string tool, Func<string, HeldTickJob> create)
    {
        HeldTickJob job;
        AtmosphericsThread.CurrentJob = id;
        try
        {
            job = create(id);
        }
        finally
        {
            AtmosphericsThread.CurrentJob = null;
        }

        GameManager.PauseGameTick();
        _tickHeld = true;
        NoteHeld(id, tool);
        _active = job;
        return job;
    }

    /// <summary>A job runs or waits, or something else holds the tick: a run started with wait now is queued.</summary>
    internal static bool Occupied => _active != null || Waiting.Count > 0 || IsSaving() || GameManager.GameTickPaused ||
                                     AtmosphericsThread.Busy;

    internal static object Status(string id)
    {
        if (Holds.TryGetValue(id, out JobHold kept))
        {
            GasHoldReply.Record(kept.Verdict, kept.Stage);
        }

        if (_active != null && _active.Id == id)
        {
            return _active.View();
        }

        int? position = Waiting.PositionOf(id);
        if (position.HasValue)
        {
            return new JobQueuedView(id, ToolOf(id), position.Value, _active?.Id, null);
        }

        return Finished.TryGetValue(id, out object view)
            ? view
            : throw ApiErrors.Refused("job_not_found", $"No job {id} is running, queued or among the last {KeptJobs}.");
    }

    private static string ToolOf(string id)
    {
        string tool = string.Empty;
        foreach (KeyValuePair<string, QueuedJob> entry in Waiting.Entries())
        {
            if (entry.Key == id)
            {
                tool = entry.Value.Tool;
            }
        }

        return tool;
    }

    /// <summary>Moves the running job on, or starts the next queued one; called every frame on the host.</summary>
    internal static void Tick()
    {
        if (!AtmosphereWorkSettled())
        {
            return;
        }

        // Each step's changes reach the clients in a state packet of their own (ClientReplication).
        if (!JobReplication.MayStep())
        {
            return;
        }

        if (_active == null)
        {
            StartWaiting();
            return;
        }

        JobStep step;
        AtmosphericsThread.CurrentJob = _active.Id;
        try
        {
            using (Prof.Scope(ProfId.JobStep))
            {
                step = _active.Step();
            }
        }
        catch (Exception exception)
        {
            // Everything a step calls into the game (a preflight, the checks after a swap). The swap loops catch their
            // own failures, so an exception here left the world as the job's state says.
            StationGodMod.LogWarning($"job {_active.Id} failed: {exception}");
            step = JobStep.Finish(_active.Failed(new ErrorView("internal_error", exception.Message)), true);
        }
        finally
        {
            AtmosphericsThread.CurrentJob = null;
            JobReplication.Changed();
        }

        switch (step)
        {
            case JobStep.Continue next:
                _active = next.State;
                break;
            case JobStep.Released released:
                ReleaseTick();
                _active = released.State;
                break;
            case JobStep.Held held:
                GameManager.PauseGameTick();
                _tickHeld = true;
                NoteHeld(held.State.Id, null);
                _active = held.State;
                break;
            case JobStep.Done done:
                Complete(done);
                break;
        }
    }

    private static void Complete(JobStep.Done done)
    {
        if (done.ReleaseTick)
        {
            ReleaseTick();
        }

        Remember(_active!.Id, done.View);
        _active = null;
        if (!_releaseOwed)
        {
            _tickHeld = false;
            NoteReleased();
        }
    }

    // Overrun atmosphere work (AtmosphericsThread) still changing pipe atmospheres: false, and nothing moves this
    // frame. When it has just ended: its failure is logged, pipe jobs are held (its contents were never checked) and a
    // release owed meanwhile is made.
    private static bool AtmosphereWorkSettled()
    {
        switch (AtmosphericsThread.Settle())
        {
            case WorkSettlement.Running:
                return false;
            case WorkSettlement.Ended ended:
                string owner = ended.Owner ?? "unknown";
                StationGodMod.LogWarning(ended.Failure != null
                    ? $"job {owner}'s overrun atmosphere work ended with an error: {ended.Failure}"
                    : $"job {owner}'s overrun atmosphere work has ended" + (ended.Canceled ? " (canceled)." : "."));
                GasHold.SetUnchecked(owner);
                if (_releaseOwed)
                {
                    _releaseOwed = false;
                    ReleaseTick();
                }

                return true;
            default:
                return true;
        }
    }

    // A save that is running holds the tick itself and lets it go when it ends. While overrun atmosphere work runs the
    // tick stays held and the release is owed (AtmosphereWorkSettled makes it).
    private static void ReleaseTick()
    {
        if (AtmosphericsThread.Busy)
        {
            _releaseOwed |= _tickHeld;
            return;
        }

        UnpauseNow();
    }

    private static void NoteHeld(string id, string? tool)
    {
        HoldTimes.Held(ProfileClock.System.Timestamp());
        Prof.TickHeld(id, tool, Time.frameCount);
    }

    private static void NoteReleased()
    {
        HoldTimes.Released(ProfileClock.System.Timestamp(), ProfileClock.System.Frequency);
        Prof.TickReleased(Time.frameCount);
    }

    private static void UnpauseNow()
    {
        if (_tickHeld && !IsSaving())
        {
            GameManager.UnpauseGameTick();
        }

        _tickHeld = false;
        NoteReleased();
    }

    /// <summary>The mod is unloading: let the tick go if a job holds it (also with atmosphere work still running).</summary>
    internal static void Abandon()
    {
        if (_active != null || _releaseOwed)
        {
            _releaseOwed = false;
            UnpauseNow();
        }

        _active = null;
        Waiting.Clear();
    }

    /// <summary>
    /// The world was left (WorldStores): queued runs are dropped and finished jobs forgotten, so a poll in the next
    /// world answers job_not_found rather than a job of the last one. A running job is left to end on its own.
    /// </summary>
    internal static void ForgetWorld()
    {
        Waiting.Clear();
        Finished.Clear();
        FinishedOrder.Clear();
        Holds.Clear();
    }

    // The next queued job, once nothing holds the tick; one the world can no longer run is dropped, refused.
    private static void StartWaiting()
    {
        if (Waiting.Count == 0 || IsSaving() || GameManager.GameTickPaused || AtmosphericsThread.Busy)
        {
            return;
        }

        if (!Waiting.TryTake(out string id, out QueuedJob queued))
        {
            return;
        }

        if (GameManager.GameState != GameState.Running)
        {
            Remember(id, new JobDroppedView(id, queued.Tool, new ErrorView("game_not_running",
                "The world stopped running before the queued run started; nothing was changed.")));
            return;
        }

        GasHoldVerdict hold = GasHold.Judge(queued.PipeNetworks, queued.Acknowledge);
        if (hold is GasHoldVerdict.Refusing refusing)
        {
            Keep(id, hold, GasHoldStage.NotStarted);
            Remember(id, new JobDroppedView(id, queued.Tool, new ErrorView(refusing.Code, refusing.Message)));
            return;
        }

        try
        {
            Launch(id, queued.Tool, queued.Create);
            GasHold.Lift(hold);
            Keep(id, hold, GasHoldStage.Started);
        }
        catch (Exception exception)
        {
            // Building the job's first state: nothing of the world has been touched yet.
            StationGodMod.LogWarning($"queued job {id} failed to start: {exception}");
            ReleaseTick();
            _active = null;
            Remember(id, new JobDroppedView(id, queued.Tool, new ErrorView("internal_error", exception.Message)));
        }
    }

    private static void Remember(string id, object view)
    {
        Finished[id] = view;
        FinishedOrder.Enqueue(id);
        while (FinishedOrder.Count > KeptJobs)
        {
            string forgotten = FinishedOrder.Dequeue();
            Finished.Remove(forgotten);
            Holds.Remove(forgotten);
        }
    }

    private static void Keep(string id, GasHoldVerdict hold, GasHoldStage stage)
    {
        if (hold.Reportable)
        {
            Holds[id] = new JobHold(hold, stage);
        }
        else
        {
            Holds.Remove(id);
        }
    }

    private sealed class JobHold
    {
        internal JobHold(GasHoldVerdict verdict, GasHoldStage stage)
        {
            Verdict = verdict;
            Stage = stage;
        }

        internal GasHoldVerdict Verdict { get; }

        internal GasHoldStage Stage { get; }
    }

    private sealed class QueuedJob
    {
        internal QueuedJob(string tool, Func<string, HeldTickJob> create, bool pipeNetworks, string? acknowledge)
        {
            Tool = tool;
            Create = create;
            PipeNetworks = pipeNetworks;
            Acknowledge = acknowledge;
        }

        internal string Tool { get; }

        internal Func<string, HeldTickJob> Create { get; }

        internal bool PipeNetworks { get; }

        /// <summary>The run's acknowledge_gas_lost, judged again when it leaves the queue.</summary>
        internal string? Acknowledge { get; }
    }

    internal static bool IsSaving() => (bool)GameMembers.SaveIsSaving.Invoke(null);

    internal static bool TickTimedOut(float startedAt) => Time.realtimeSinceStartup - startedAt > TickWaitSeconds;
}

/// <summary>A running job in one of its states: its id, its view while running, and what its next step is.</summary>
internal abstract class HeldTickJob
{
    protected HeldTickJob(string id)
    {
        Id = id;
    }

    internal string Id { get; }

    internal abstract JobStep Step();

    /// <summary>The job as polled while it runs.</summary>
    internal abstract object View();

    /// <summary>The job as it stands when one of its steps threw.</summary>
    internal abstract object Failed(ErrorView error);
}

/// <summary>
/// What a job's step leads to: its next state as the tick is (Continue), with the tick let go (Released) or held again
/// (Held), or the finished job and whether to let the tick go (Done).
/// </summary>
internal abstract class JobStep
{
    private JobStep()
    {
    }

    internal static JobStep Next(HeldTickJob state) => new Continue(state);

    internal static JobStep Release(HeldTickJob state) => new Released(state);

    internal static JobStep Hold(HeldTickJob state) => new Held(state);

    internal static JobStep Finish(object view, bool releaseTick) => new Done(view, releaseTick);

    internal sealed class Continue : JobStep
    {
        internal Continue(HeldTickJob state)
        {
            State = state;
        }

        internal HeldTickJob State { get; }
    }

    internal sealed class Released : JobStep
    {
        internal Released(HeldTickJob state)
        {
            State = state;
        }

        internal HeldTickJob State { get; }
    }

    internal sealed class Held : JobStep
    {
        internal Held(HeldTickJob state)
        {
            State = state;
        }

        internal HeldTickJob State { get; }
    }

    internal sealed class Done : JobStep
    {
        internal Done(object view, bool releaseTick)
        {
            View = view;
            ReleaseTick = releaseTick;
        }

        internal object View { get; }

        internal bool ReleaseTick { get; }
    }
}
