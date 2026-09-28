#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using StationGodMCP.Api.Views;
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
/// preflight again, so the world the earlier jobs left is what it is checked against.
/// </summary>
internal static class HeldTickJobs
{
    private const float TickWaitSeconds = 15f;
    private const int KeptJobs = 16;
    internal const int MaximumWaiting = 8;

    private static readonly Dictionary<string, object> Finished = new Dictionary<string, object>();
    private static readonly Queue<string> FinishedOrder = new Queue<string>();
    private static readonly JobLine<QueuedJob> Waiting = new JobLine<QueuedJob>(MaximumWaiting);
    private static HeldTickJob? _active;
    private static bool _tickHeld;
    private static long _next;

    /// <summary>
    /// Starts a job made for its id (prefix-number) and returns its view. While another job runs (or a save or
    /// something else holds the tick): busy with the running job's id, or with wait the job is queued and its
    /// queued view returned.
    /// </summary>
    internal static object Start(string prefix, string tool, Func<string, HeldTickJob> create, bool wait,
        object? preflight)
    {
        if (GameManager.GameState != GameState.Running)
        {
            throw ApiErrors.Refused("game_not_running", "The world is not running.");
        }

        bool tickTaken = IsSaving() || GameManager.GameTickPaused;
        if (_active == null && Waiting.Count == 0 && !tickTaken)
        {
            return Launch(NextId(prefix), create).View();
        }

        if (!wait)
        {
            if (_active == null && tickTaken)
            {
                throw ApiErrors.Refused("tick_held",
                    "The game tick is held by something else (a save or a world settings change); try again, or " +
                    "pass wait: true to queue the run.");
            }

            return new JobBusyView(tool, _active?.Id ?? string.Empty, Waiting.Count,
                (_active != null ? $"Job {_active.Id} is still running" : "Jobs are waiting for the slot") +
                "; nothing was changed. Poll it with job_id, or pass wait: true to queue this run behind it.");
        }

        string id = NextId(prefix);
        if (!Waiting.Add(id, new QueuedJob(tool, create)))
        {
            return new JobBusyView(tool, _active?.Id ?? string.Empty, Waiting.Count,
                $"{MaximumWaiting} runs are already queued; nothing was changed. Try again once one has started.");
        }

        return new JobQueuedView(id, tool, Waiting.PositionOf(id) ?? 1, _active?.Id, preflight);
    }

    private static string NextId(string prefix) => prefix + "-" + (++_next).ToString(CultureInfo.InvariantCulture);

    private static HeldTickJob Launch(string id, Func<string, HeldTickJob> create)
    {
        HeldTickJob job = create(id);
        GameManager.PauseGameTick();
        _tickHeld = true;
        _active = job;
        return job;
    }

    internal static object Status(string id)
    {
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
        if (_active == null)
        {
            StartWaiting();
            return;
        }
        JobStep step;
        try
        {
            step = _active.Step();
        }
        catch (Exception exception)
        {
            // Everything a step calls into the game (a preflight, the checks after a swap). The swap loops catch their
            // own failures, so an exception here left the world as the job's state says.
            StationGodMod.LogWarning($"job {_active.Id} failed: {exception}");
            step = JobStep.Finish(_active.Failed(new ErrorView("internal_error", exception.Message)), true);
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
        _tickHeld = false;
    }

    // A save that is running holds the tick itself and lets it go when it ends.
    private static void ReleaseTick()
    {
        if (_tickHeld && !IsSaving())
        {
            GameManager.UnpauseGameTick();
        }

        _tickHeld = false;
    }

    /// <summary>The mod is unloading: let the tick go if a job holds it.</summary>
    internal static void Abandon()
    {
        if (_active != null)
        {
            ReleaseTick();
        }

        _active = null;
        Waiting.Clear();
    }

    // The next queued job, once nothing holds the tick; one the world can no longer run is dropped, refused.
    private static void StartWaiting()
    {
        if (Waiting.Count == 0 || IsSaving() || GameManager.GameTickPaused)
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

        try
        {
            Launch(id, queued.Create);
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
            Finished.Remove(FinishedOrder.Dequeue());
        }
    }

    private sealed class QueuedJob
    {
        internal QueuedJob(string tool, Func<string, HeldTickJob> create)
        {
            Tool = tool;
            Create = create;
        }

        internal string Tool { get; }

        internal Func<string, HeldTickJob> Create { get; }
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
