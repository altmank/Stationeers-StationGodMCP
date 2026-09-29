#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networking;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game.Build;

/// <summary>
/// What a place_structure or remove_structure job does, apart from the running: the final check (the whole preflight
/// again once the tick has stopped), the work in that one frame, and the checks once Unity has finished it.
/// </summary>
internal abstract class BuildWork
{
    internal abstract string Tool { get; }

    internal abstract object Preflight { get; }

    /// <summary>
    /// Whether it places or removes anything that is part of a pipe network or has a pipe end (PipeContact): only such
    /// a job can change pipe network contents, so only it is checked (JobGas) and held after a failed check (GasHold).
    /// </summary>
    internal abstract bool TouchesPipes { get; }

    /// <summary>The final check's report, or null (with the error) when it refuses.</summary>
    internal abstract object? FinalCheck(out ErrorView? refusal);

    /// <summary>
    /// Places or removes everything, in this frame, as far as it goes; the gas changes each placement queues are
    /// applied before the next one (JobGas.Settle).
    /// </summary>
    internal abstract void Apply(BuildLog log, JobGas gas);

    /// <summary>Whether Unity has finished what Apply started (removed pieces destroyed).</summary>
    internal abstract bool Settled();

    internal abstract List<BuildCheckView> Verify(BuildLog log);
}

/// <summary>
/// Confirmed place_structure and remove_structure runs, on the shared runner (HeldTickJobs). Either can join or split
/// pipe networks (an in-line tank, a passive vent, a pipe piece, a device on a pipe end); such a run has its contents
/// checked (JobGas) and waits out a failed check (GasHold). A run that touches no pipe (a locker, a frame, a wall) is
/// neither.
/// </summary>
internal static class BuildJobs
{
    internal static object Start(string prefix, BuildWork work, bool wait, string? acknowledgeGasLost) =>
        HeldTickJobs.Start(prefix, work.Tool, id => new BuildWaiting(id, work, Time.realtimeSinceStartup), wait,
            work.Preflight, work.TouchesPipes, acknowledgeGasLost);
}

/// <summary>What can change a pipe network: a pipe network member (a pipe piece, in-line tank, passive vent) or a thing
/// with a gas or liquid pipe end (a device joins the network there, or two networks through itself).</summary>
internal static class PipeContact
{
    private const NetworkType PipeEnds = NetworkType.Pipe | NetworkType.PipeLiquid;

    internal static bool Touches(Structure thing)
    {
        if (thing is INetworkedPipe)
        {
            return true;
        }

        if (thing is SmallGrid grid && grid.OpenEnds != null)
        {
            foreach (Connection end in grid.OpenEnds)
            {
                if (end != null && (end.ConnectionType & PipeEnds) != NetworkType.None)
                {
                    return true;
                }
            }
        }

        return false;
    }
}

/// <summary>Waiting for the game tick to stop; then the final check and the work, in one frame.</summary>
internal sealed class BuildWaiting : HeldTickJob
{
    private readonly BuildWork _work;
    private readonly float _startedAt;

    internal BuildWaiting(string id, BuildWork work, float startedAt)
        : base(id)
    {
        _work = work;
        _startedAt = startedAt;
    }

    internal override object View() => new BuildJobView(Id, _work.Tool, "waiting", _work.Preflight, null);

    internal override object Failed(ErrorView error) => Refused(null, error);

    internal override JobStep Step()
    {
        if (GameManager.GameState != GameState.Running)
        {
            return JobStep.Finish(Refused(null, new ErrorView("game_not_running",
                "The world stopped running first; nothing was changed.")), false);
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

        JobGas gas = JobGas.Open(_work.TouchesPipes);
        object? finalCheck;
        ErrorView? refusal;
        try
        {
            finalCheck = _work.FinalCheck(out refusal);
        }
        catch (ApiException exception)
        {
            return JobStep.Finish(Refused(null, new ErrorView(exception.Code, exception.Message)), true);
        }

        if (refusal != null)
        {
            return JobStep.Finish(Refused(finalCheck, refusal), true);
        }

        BuildLog log = new BuildLog();
        _work.Apply(log, gas);
        return JobStep.Next(new BuildSettling(Id, _work, finalCheck, log, gas));
    }

    private BuildJobView Refused(object? finalCheck, ErrorView error) =>
        new BuildJobView(Id, _work.Tool, "refused", _work.Preflight,
            new BuildJobResult(finalCheck, new BuildLog(), new List<BuildCheckView>(), error));
}

/// <summary>Done in the held frame; waiting for Unity to finish it (at most 30 frames), then checked, tick let go.</summary>
internal sealed class BuildSettling : HeldTickJob
{
    private const int MaximumFrames = 30;

    private readonly BuildWork _work;
    private readonly object? _finalCheck;
    private readonly BuildLog _log;
    private readonly JobGas _gas;
    private int _frames;

    internal BuildSettling(string id, BuildWork work, object? finalCheck, BuildLog log, JobGas gas)
        : base(id)
    {
        _work = work;
        _finalCheck = finalCheck;
        _log = log;
        _gas = gas;
    }

    internal override object View() => new BuildJobView(Id, _work.Tool, "verifying", _work.Preflight, null);

    internal override object Failed(ErrorView error) =>
        new BuildJobView(Id, _work.Tool, "applied_unchecked", _work.Preflight,
            new BuildJobResult(_finalCheck, _log, new List<BuildCheckView>(), error));

    internal override JobStep Step()
    {
        _frames++;
        if (!_work.Settled() && _frames < MaximumFrames)
        {
            return JobStep.Next(this);
        }

        GasCheckView? gasCheck = _gas.Close(Id);
        List<BuildCheckView> checks = _work.Verify(_log);
        string status = _log.StoppedAt != null ? "stopped"
            : checks.TrueForAll(check => check.Ok) ? "applied"
            : "applied_with_differences";
        return JobStep.Finish(new BuildJobView(Id, _work.Tool, GasCheckView.JobStatus(status, gasCheck),
            _work.Preflight, new BuildJobResult(_finalCheck, _log, checks, null, gasCheck)), true);
    }
}

/// <summary>
/// place_structure's work. Each placement in order: the cursor check again (earlier pieces of the run now stand),
/// its materials taken as a kit's placement takes them (Stackable.OnUseItem), the piece built as a kit builds it
/// (Constructor.SpawnConstruct: position, rotation, owner, colour) and raised to its build state as the authoring
/// tool does (CurrentBuildStateIndex, then UpdateStateVisualizer), then labelled as the Labeller does. The first
/// failure stops the run there.
/// </summary>
internal sealed class PlaceWork : BuildWork
{
    private readonly PlaceArguments _arguments;
    private readonly PlaceReportView _preflight;
    private readonly Dictionary<int, Structure> _built = new Dictionary<int, Structure>();
    private PlacePlan? _plan;

    internal PlaceWork(PlaceArguments arguments, PlaceReportView preflight, bool touchesPipes)
    {
        _arguments = arguments;
        _preflight = preflight;
        TouchesPipes = touchesPipes;
    }

    internal override string Tool => "place_structure";

    internal override object Preflight => _preflight;

    internal override bool TouchesPipes { get; }

    internal override object? FinalCheck(out ErrorView? refusal)
    {
        _plan = PlacePlanner.Plan(_arguments);
        refusal = _plan.Ready ? null : new ErrorView("final_check_failed",
            $"The check once the tick had stopped found {_plan.Problems.Count} problem(s); nothing was changed.");
        return BuildReports.Of(_plan, _plan.Ready ? BuildReports.Scheduled : BuildReports.Refused, null);
    }

    internal override void Apply(BuildLog log, JobGas gas)
    {
        PlacePlan plan = _plan!;
        Dictionary<int, ItemStock> stocks = new Dictionary<int, ItemStock>();
        foreach (ItemStock stock in plan.Stocks)
        {
            stocks[stock.Item.PrefabHash] = stock;
        }

        Dictionary<int, int> used = new Dictionary<int, int>();
        foreach (PlannedPlacement placement in plan.Placements)
        {
            if (!PlaceOne(plan, placement, stocks, used, log))
            {
                break;
            }

            gas.Settle();
        }

        foreach (ItemStock stock in plan.Stocks)
        {
            if (used.TryGetValue(stock.Item.PrefabHash, out int quantity))
            {
                log.Used.Add(new UpgradeAmountView(stock.Item.PrefabName, quantity));
            }
        }
    }

    internal override bool Settled() => true;

    internal override List<BuildCheckView> Verify(BuildLog log)
    {
        List<BuildCheckView> checks = new List<BuildCheckView>();
        foreach (PlannedPlacement placement in _plan!.Placements)
        {
            if (_built.TryGetValue(placement.Index, out Structure built))
            {
                checks.Add(new BuildCheckView(placement.Index, new ThingId(built.ReferenceId), Check(placement, built)));
            }
        }

        return checks;
    }

    private bool PlaceOne(PlacePlan plan, PlannedPlacement placement, Dictionary<int, ItemStock> stocks,
        Dictionary<int, int> used, BuildLog log)
    {
        Structure prefab = placement.Prefab!;
        try
        {
            string? refusal = PlacePlanner.Recheck(placement);
            if (refusal != null)
            {
                return Stop(log, placement, "cannot_place", $"{prefab.PrefabName}: {refusal}; nothing more was built.");
            }

            List<ItemAmount> taken = new List<ItemAmount>();
            foreach (ItemAmount amount in placement.Cost)
            {
                ItemStock stock = stocks.TryGetValue(amount.Prefab.PrefabHash, out ItemStock found)
                    ? found
                    : ItemStock.Empty(amount.Prefab);
                int got = stock.Take(amount.Quantity);
                used[amount.Prefab.PrefabHash] =
                    (used.TryGetValue(amount.Prefab.PrefabHash, out int sum) ? sum : 0) + got;
                taken.Add(new ItemAmount(amount.Prefab, got));
                if (got < amount.Quantity)
                {
                    GiveBack(plan, taken, log);
                    return Stop(log, placement, "materials_short",
                        $"Only {got} of {amount.Quantity} {Names.Of(amount.Prefab)} could be taken; it was not " +
                        "built (what was taken for it is given back) and nothing more was.");
                }
            }

            Structure? built = Constructor.SpawnConstruct(new CreateStructureInstance(prefab,
                GridController.World.WorldToLocal(placement.Position!.Value), placement.Rotation, plan.Owner,
                placement.ColorIndex));
            if (built == null)
            {
                GiveBack(plan, taken, log);
                return Stop(log, placement, "create_failed", $"The game built no {prefab.PrefabName}.");
            }

            _built[placement.Index] = built;
            int state = placement.State!.Value;
            if (built.CurrentBuildStateIndex != state)
            {
                built.CurrentBuildStateIndex = state;
                built.UpdateStateVisualizer();
            }

            if (placement.Args.Label != null)
            {
                Thing.RenameThing(built.ReferenceId, LabelApi.AsTheLabellerWrites(built, placement.Args.Label));
            }

            log.Placed.Add(new BuiltPieceView(placement.Index, GameLookup.ViewOf(built)));
            return true;
        }
        catch (Exception exception)
        {
            // Thing.Create, the build state, the rename or a stack's OnUseItem failing part way: stop at this one.
            StationGodMod.LogWarning($"place_structure placement {placement.Index} failed: {exception}");
            return Stop(log, placement, "build_failed", exception.Message);
        }
    }

    private static void GiveBack(PlacePlan plan, List<ItemAmount> taken, BuildLog log)
    {
        List<ItemAmount> some = taken.FindAll(amount => amount.Quantity > 0);
        if (plan.From != null && some.Count > 0)
        {
            Refunds.Deliver(plan.From, some, log.Refunded);
        }
    }

    private static bool Stop(BuildLog log, PlannedPlacement placement, string code, string message)
    {
        log.StoppedAt = new BuildStopView(placement.Index, new ErrorView(code, message));
        return false;
    }

    // The piece stands as planned: its prefab, position, rotation, build state, label and colour.
    private static List<string> Check(PlannedPlacement placement, Structure built)
    {
        List<string> issues = new List<string>();
        Thing? found = Thing.Find(built.ReferenceId);
        if (found == null || built == null || built.IsBeingDestroyed)
        {
            issues.Add("it is gone");
            return issues;
        }

        if (built.PrefabHash != placement.Prefab!.PrefabHash)
        {
            issues.Add($"it is a {built.PrefabName}, not a {placement.Prefab.PrefabName}");
        }

        if (Vector3.Distance(built.ThingTransformPosition, placement.Position!.Value) > 0.05f)
        {
            issues.Add($"it stands at {PlacePlanner.Describe(built.ThingTransformPosition)}, not " +
                       $"{PlacePlanner.Describe(placement.Position.Value)}");
        }

        if (Quaternion.Angle(built.ThingTransformRotation, placement.Rotation) > 1f)
        {
            issues.Add($"it is turned {built.ThingTransformRotation.eulerAngles}, not " +
                       $"{placement.Rotation.eulerAngles}");
        }

        if (built.CurrentBuildStateIndex != placement.State)
        {
            issues.Add($"it is at build state {built.CurrentBuildStateIndex}, not {placement.State}");
        }

        if (placement.Args.Label != null && string.IsNullOrEmpty(built.CustomName))
        {
            issues.Add("it has no label");
        }

        if (placement.ColorIndex >= 0 && GameManager.GetColorIndex(built.CustomColor) != placement.ColorIndex)
        {
            issues.Add($"its colour is {GameManager.GetColorIndex(built.CustomColor)}, not {placement.ColorIndex}");
        }

        return issues;
    }
}

/// <summary>
/// remove_structure's work. Cable, pipe and chute pieces as their remove tool removes them (RunBuilder.Remove: a
/// network kept whole keeps its id and contents); every other piece as deconstruction ends (with allow_contents its
/// slots' items are dropped where it stood, as Structure.StructureDestroyed drops them; OnServer.Destroy). Then the
/// refund: what deconstructing every removed piece gives back, to the source (worn items collect, the rest at its
/// feet), or on the ground where each piece stood.
/// </summary>
internal sealed class RemoveWork : BuildWork
{
    private static readonly PipeFamily Pipes = new PipeFamily();

    private readonly RemoveArguments _arguments;
    private readonly RemoveReportView _preflight;
    private readonly Dictionary<int, long> _removed = new Dictionary<int, long>();
    private RemovePlan? _plan;

    internal RemoveWork(RemoveArguments arguments, RemoveReportView preflight, bool touchesPipes)
    {
        _arguments = arguments;
        _preflight = preflight;
        TouchesPipes = touchesPipes;
    }

    internal override string Tool => "remove_structure";

    internal override object Preflight => _preflight;

    internal override bool TouchesPipes { get; }

    internal override object? FinalCheck(out ErrorView? refusal)
    {
        _plan = RemovePlanner.Plan(_arguments);
        refusal = _plan.Ready ? null : new ErrorView("final_check_failed",
            $"The check once the tick had stopped found {_plan.Problems.Count} problem(s); nothing was changed.");
        return BuildReports.Of(_plan, _plan.Ready ? BuildReports.Scheduled : BuildReports.Refused, null);
    }

    // Removals only split networks, and the game chains splits made in one frame itself (Pipe.OnDestroy divides from
    // the live atmosphere of a network still awaiting its share): the gas is applied and checked once, at the end.
    // The gas the plan forecasts it deletes (allowed, or it would not have passed) is expected by the check, which
    // would otherwise put it back into the parts left and could burst them (structures-34).
    internal override void Apply(BuildLog log, JobGas gas)
    {
        RemovePlan plan = _plan!;
        gas.Expect(plan.GasLosses);
        List<PlannedTakedown> done = new List<PlannedTakedown>();
        foreach (RunPlan runPlan in plan.NetworkPlans)
        {
            if (log.StoppedAt == null)
            {
                RemoveNetworkPieces(plan, runPlan, done, log);
            }
        }

        foreach (PlannedTakedown takedown in plan.Takedowns)
        {
            if (log.StoppedAt != null)
            {
                break;
            }

            if (takedown.Kind == null)
            {
                RemoveOne(plan, takedown, done, log);
            }
        }

        Refund(plan, done, log);
    }

    internal override bool Settled()
    {
        foreach (long id in _removed.Values)
        {
            if (Thing.Find(id) != null)
            {
                return false;
            }
        }

        return true;
    }

    internal override List<BuildCheckView> Verify(BuildLog log)
    {
        List<BuildCheckView> checks = new List<BuildCheckView>();
        foreach (KeyValuePair<int, long> removed in _removed)
        {
            List<string> issues = new List<string>();
            if (Thing.Find(removed.Value) != null)
            {
                issues.Add("it still exists");
            }

            checks.Add(new BuildCheckView(removed.Key, new ThingId(removed.Value), issues));
        }

        return checks;
    }

    private void RemoveNetworkPieces(RemovePlan plan, RunPlan runPlan, List<PlannedTakedown> done, BuildLog log)
    {
        RunOutcome outcome = new RunOutcome();
        RunBuilder.Remove(runPlan, outcome);
        HashSet<long> removed = new HashSet<long>();
        foreach (ThingId id in outcome.Log.Removed)
        {
            removed.Add(id.Value);
        }

        foreach (PlannedTakedown takedown in plan.Takedowns)
        {
            if (takedown.Kind != runPlan.Request.Kind || !removed.Contains(takedown.Piece.ReferenceId))
            {
                continue;
            }

            Done(takedown, done, log);
        }

        if (outcome.Log.StoppedAt != null)
        {
            PlannedTakedown? first = plan.Takedowns.Find(takedown =>
                takedown.Kind == runPlan.Request.Kind && !removed.Contains(takedown.Piece.ReferenceId));
            log.StoppedAt = new BuildStopView(first?.Index ?? -1, outcome.Log.StoppedAt);
        }
    }

    private void RemoveOne(RemovePlan plan, PlannedTakedown takedown, List<PlannedTakedown> done, BuildLog log)
    {
        Structure piece = takedown.Piece;
        try
        {
            // An in-line tank or passive vent of a network the job keeps whole leaves it first, as the pipe pieces
            // removed with it did (RunBuilder.Remove): the network keeps its id and every mole in what is left.
            if (piece is Pipe { PipeNetwork: { } network } member && plan.KeptWhole.Contains(network.ReferenceId))
            {
                Pipes.Leave(member, new List<SmallGrid>(), network);
            }

            if (_arguments.Allow.Contents && piece.Slots != null)
            {
                foreach (Slot slot in piece.Slots)
                {
                    DynamicThing? occupant = slot?.Get();
                    if (occupant != null && !occupant.IsBeingDestroyed)
                    {
                        OnServer.MoveToWorld(occupant);
                    }
                }
            }

            OnServer.Destroy(piece);
            Done(takedown, done, log);
        }
        catch (Exception exception)
        {
            // OnServer.MoveToWorld or OnServer.Destroy failing: stop here; what was removed stays removed.
            StationGodMod.LogWarning($"remove_structure of {piece.ReferenceId} failed: {exception}");
            log.StoppedAt = new BuildStopView(takedown.Index, new ErrorView("remove_failed",
                $"Removing {piece.PrefabName} {piece.ReferenceId} failed: {exception.Message}"));
        }
    }

    private void Done(PlannedTakedown takedown, List<PlannedTakedown> done, BuildLog log)
    {
        _removed[takedown.Index] = takedown.Piece.ReferenceId;
        done.Add(takedown);
        log.Removed.Add(new BuiltPieceView(takedown.Index, GameLookup.ViewOf(takedown.Piece)));
    }

    private static void Refund(RemovePlan plan, List<PlannedTakedown> done, BuildLog log)
    {
        try
        {
            if (plan.Arguments.RefundTo == RefundRoute.WherePieceStood)
            {
                done.ForEach(takedown => Refunds.DeliverAt(takedown.Position, takedown.Refund, log.Refunded));
            }
            else if (plan.Refunds != null)
            {
                List<ItemAmount> all = new List<ItemAmount>();
                done.ForEach(takedown => all.AddRange(takedown.Refund));
                Refunds.Deliver(plan.Refunds, all, log.Refunded);
            }
        }
        catch (Exception exception)
        {
            // OnServer.CreateOrStack after the removals: they stand, the refund stops where it failed.
            StationGodMod.LogWarning($"remove_structure refund failed: {exception}");
            log.RefundError = new ErrorView("refund_failed", exception.Message);
        }
    }
}
