#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game.Upgrades;

/// <summary>
/// Confirmed runs of the upgrade and clean tools, on the shared runner (HeldTickJobs). Once GameManager.GameTickPaused
/// says the tick has stopped between two ticks, the whole preflight runs again and, only if it finds nothing, every
/// piece is swapped in that frame. The frame after (once the old pieces are gone), the result is checked against what
/// was recorded, and the tick is let go. A save that starts meanwhile owns the tick from then on: the run is refused
/// before any change, or, if the swap is done, the tick is left for the save to let go.
/// </summary>
internal static class UpgradeJobs
{
    internal static object Start(UpgradeRequest request, UpgradePlan plan, bool wait, string? acknowledgeGasLost) =>
        HeldTickJobs.Start("upgrade", request.Goal.Tool, id => new WaitingForTick(id, request,
            UpgradeReports.Of(plan, UpgradeReports.Scheduled, id), Time.realtimeSinceStartup), wait, null,
            request.Family is PipeFamily, acknowledgeGasLost);
}

/// <summary>A running upgrade or clean job in one of its states.</summary>
internal abstract class ActiveUpgrade : HeldTickJob
{
    protected ActiveUpgrade(string id, UpgradeRequest request, UpgradeReportView preflight)
        : base(id)
    {
        Request = request;
        Preflight = preflight;
    }

    internal UpgradeRequest Request { get; }

    internal UpgradeReportView Preflight { get; }

    internal override object View() => new UpgradeJobView(Id, Request.Goal.Tool, "waiting", Preflight, null);

    internal override object Failed(ErrorView error) =>
        new UpgradeJobView(Id, Request.Goal.Tool, "refused", Preflight,
            new UpgradeJobResult(null, new UpgradeSwapLog(), null, error));

    protected UpgradeJobView Refused(UpgradeReportView? finalCheck, ErrorView error) =>
        new UpgradeJobView(Id, Request.Goal.Tool, "refused", Preflight,
            new UpgradeJobResult(finalCheck, new UpgradeSwapLog(), null, error));
}

/// <summary>Waiting for the game tick to stop; then the final check and the swap, in one frame.</summary>
internal sealed class WaitingForTick : ActiveUpgrade
{
    private readonly float _startedAt;

    internal WaitingForTick(string id, UpgradeRequest request, UpgradeReportView preflight, float startedAt)
        : base(id, request, preflight)
    {
        _startedAt = startedAt;
    }

    internal override JobStep Step()
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

        return SwapNow();
    }

    private JobStep SwapNow()
    {
        JobGas gas = JobGas.Open(Request.Family is PipeFamily);
        UpgradePlan plan;
        try
        {
            plan = UpgradePlanner.Plan(Request);
        }
        catch (ApiException refusal)
        {
            return JobStep.Finish(Refused(null, new ErrorView(refusal.Code, refusal.Message)), true);
        }

        if (!plan.Ready)
        {
            UpgradeReportView refused = UpgradeReports.Of(plan, UpgradeReports.Refused, Id);
            return JobStep.Finish(Refused(refused, new ErrorView("final_check_failed",
                $"The check once the tick had stopped found {plan.Problems.Count} problem(s); nothing was " +
                "changed.")), true);
        }

        UpgradeReportView finalCheck = UpgradeReports.Of(plan, UpgradeReports.Scheduled, Id);
        Dictionary<long, List<SmallGrid>> replacements = new Dictionary<long, List<SmallGrid>>();
        UpgradeSwapLog log = UpgradeSwap.Run(plan, replacements, gas);
        return JobStep.Next(new AwaitingCheck(this, plan, new SwapOutcome(finalCheck, log, replacements), gas));
    }
}

internal sealed class SwapOutcome
{
    internal SwapOutcome(UpgradeReportView finalCheck, UpgradeSwapLog log,
        Dictionary<long, List<SmallGrid>> replacements)
    {
        FinalCheck = finalCheck;
        Log = log;
        Replacements = replacements;
    }

    internal UpgradeReportView FinalCheck { get; }

    internal UpgradeSwapLog Log { get; }

    /// <summary>Each replacement by the old piece's id.</summary>
    internal Dictionary<long, List<SmallGrid>> Replacements { get; }
}

/// <summary>
/// Swapped; waiting for Unity to destroy the old pieces (end of the swap frame, where their OnDestroy runs), then
/// checking the result against what was recorded before the swap.
/// </summary>
internal sealed class AwaitingCheck : ActiveUpgrade
{
    private const int MaximumFrames = 30;

    private readonly UpgradePlan _plan;
    private readonly SwapOutcome _outcome;
    private readonly JobGas _gas;
    private int _frames;

    internal AwaitingCheck(ActiveUpgrade waiting, UpgradePlan plan, SwapOutcome outcome, JobGas gas)
        : base(waiting.Id, waiting.Request, waiting.Preflight)
    {
        _plan = plan;
        _outcome = outcome;
        _gas = gas;
    }

    // The swap is done; only the check after it failed.
    internal override object Failed(ErrorView error) =>
        new UpgradeJobView(Id, Request.Goal.Tool, "applied_unchecked", Preflight,
            new UpgradeJobResult(_outcome.FinalCheck, _outcome.Log, null, error));

    internal override JobStep Step()
    {
        _frames++;
        if (OldPiecesRemain() && _frames < MaximumFrames)
        {
            return JobStep.Next(this);
        }

        GasCheckView? gasCheck = _gas.Close(Id);
        UpgradeVerificationView verification = UpgradeCheck.Verify(_plan, _outcome);
        string status = _outcome.Log.StoppedAt != null ? "stopped"
            : verification.Ok ? "applied"
            : "applied_with_differences";
        UpgradeJobView view = new UpgradeJobView(Id, Request.Goal.Tool, GasCheckView.JobStatus(status, gasCheck),
            Preflight, new UpgradeJobResult(_outcome.FinalCheck, _outcome.Log, verification, null, gasCheck));
        return JobStep.Finish(view, true);
    }

    private bool OldPiecesRemain()
    {
        foreach (PlannedSwap swap in _plan.Swaps)
        {
            if (!_outcome.Replacements.ContainsKey(swap.GroupId))
            {
                continue;
            }

            foreach (OldPiece old in swap.Olds)
            {
                // Thing.OnDestroy deregisters the reference, so Find answers null once Unity has destroyed it.
                if (Thing.Find(old.Piece.ReferenceId) != null)
                {
                    return true;
                }
            }
        }

        return false;
    }
}

/// <summary>
/// The check after a swap. The game's links around the replacements (and any planned piece left as it was), with
/// each replacement's id read as its old piece's, must equal the links recorded before the swap; each mounted device
/// must be attached as it was; each device next to a swapped piece must be on the same networks (after
/// Device.InitializeDataConnection, which the game runs when a neighbour changes); each network must hold the same
/// devices and every replacement, and a pipe network the same Atmosphere with the predicted volume.
/// </summary>
internal static class UpgradeCheck
{
    internal static UpgradeVerificationView Verify(UpgradePlan plan, SwapOutcome outcome)
    {
        List<UpgradeProblemView> problems = new List<UpgradeProblemView>();
        LinkSurvey survey = plan.Links!;
        Dictionary<long, long> groupOf = new Dictionary<long, long>(survey.GroupOf);
        List<PieceModel> models = new List<PieceModel>();
        HashSet<long> focus = new HashSet<long>();
        foreach (PlannedSwap swap in plan.Swaps)
        {
            foreach (SmallGrid now in Now(swap, outcome, problems))
            {
                groupOf[now.ReferenceId] = swap.GroupId;
                focus.Add(now.ReferenceId);
                models.Add(PieceShapes.Live(now));
            }
        }

        // Every piece counted under its swap's group: links among one group's new pieces are no links.
        List<long> unreadable = new List<long>();
        HashSet<Link> after = Connectivity.Grouped(
            LinkSurvey.GameLinks(LinkSurvey.Neighbourhood(models), focus, unreadable), groupOf);
        LinkDiff diff = Connectivity.Compare(survey.Expected, after);
        AddLinkProblems(problems, survey, diff, unreadable);
        CheckMounted(problems, plan, outcome);
        CheckDevices(problems, plan);
        List<object> networks = new List<object>();
        foreach (NetworkRecord record in plan.Networks)
        {
            record.Verify(problems, outcome.Replacements);
            object? report = record.ReportNow();
            if (report != null)
            {
                networks.Add(report);
            }
        }

        return new UpgradeVerificationView(problems, new UpgradeLinkCounts(survey.Expected.Count, after.Count),
            survey.ViewsOf(diff.Added), survey.ViewsOf(diff.Lost), networks);
    }

    // What stands where the swap's old pieces stood: its replacements (none for a removal) when it was carried out,
    // else the old pieces.
    private static List<SmallGrid> Now(PlannedSwap swap, SwapOutcome outcome, List<UpgradeProblemView> problems)
    {
        List<SmallGrid> now = new List<SmallGrid>(swap.Parts.Count);
        if (!outcome.Replacements.TryGetValue(swap.GroupId, out List<SmallGrid> replacements))
        {
            foreach (OldPiece old in swap.Olds)
            {
                if (old.Piece != null && !old.Piece.IsBeingDestroyed)
                {
                    now.Add(old.Piece);
                }
            }

            return now;
        }

        foreach (SmallGrid replacement in replacements)
        {
            if (replacement == null || replacement.IsBeingDestroyed)
            {
                problems.Add(new UpgradeProblemView("replacement_gone",
                    $"A replacement of {swap.GroupId} no longer exists.", new ThingId(swap.GroupId)));
                continue;
            }

            now.Add(replacement);
        }

        foreach (OldPiece old in swap.Olds)
        {
            if (old.Piece != null && !old.Piece.IsBeingDestroyed)
            {
                problems.Add(new UpgradeProblemView("old_piece_remains",
                    $"{old.Piece.PrefabName} {old.Piece.ReferenceId} is still there after the swap.",
                    new ThingId(old.Piece.ReferenceId)));
                now.Add(old.Piece);
            }
        }

        return now;
    }

    private static void AddLinkProblems(List<UpgradeProblemView> problems, LinkSurvey survey, LinkDiff diff,
        List<long> unreadable)
    {
        foreach (long id in unreadable)
        {
            problems.Add(new UpgradeProblemView("links_unreadable",
                $"The game's connections of {id} could not be read after the swap.", new ThingId(id)));
        }

        foreach (Link link in diff.Added)
        {
            problems.Add(new UpgradeProblemView("link_added",
                $"{survey.NameOf(link.From)} now connects to {survey.NameOf(link.To)}, which it did not before.",
                new ThingId(link.From)));
        }

        foreach (Link link in diff.Lost)
        {
            problems.Add(new UpgradeProblemView("link_lost",
                $"{survey.NameOf(link.From)} no longer connects to {survey.NameOf(link.To)}.", new ThingId(link.From)));
        }
    }

    private static void CheckMounted(List<UpgradeProblemView> problems, UpgradePlan plan, SwapOutcome outcome)
    {
        foreach (MountedRecord record in plan.Links!.Mounted)
        {
            SmallGrid piece = outcome.Replacements.TryGetValue(record.Swap.GroupId,
                out List<SmallGrid> replacements) && record.Part >= 0 && record.Part < replacements.Count
                ? replacements[record.Part]
                : record.Piece;
            if (record.Device == null || piece == null)
            {
                continue;
            }

            bool now = plan.Request.Family.MountedNow(record.Device, piece);
            if (now != record.Before)
            {
                problems.Add(new UpgradeProblemView("mounted_changed",
                    $"{record.Device.PrefabName} {record.Device.ReferenceId} was {(record.Before ? "" : "not ")}" +
                    $"attached and now is{(now ? "" : " not")}.", new ThingId(record.Device.ReferenceId)));
            }
        }
    }

    private static void CheckDevices(List<UpgradeProblemView> problems, UpgradePlan plan)
    {
        foreach (DeviceRecord record in plan.Links!.Devices)
        {
            Device device = record.Device;
            if (device == null || device.IsBeingDestroyed)
            {
                continue;
            }

            device.InitializeDataConnection();
            List<long> now = plan.Request.Family.DeviceNetworks(device);
            if (!SameIds(now, record.Networks))
            {
                problems.Add(new UpgradeProblemView("device_networks_changed",
                    $"{device.PrefabName} {device.ReferenceId} was on networks " +
                    $"[{string.Join(", ", record.Networks)}] " +
                    $"and is on [{string.Join(", ", now)}].", new ThingId(device.ReferenceId)));
            }
        }
    }

    private static bool SameIds(List<long> a, List<long> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (int index = 0; index < a.Count; index++)
        {
            if (a[index] != b[index])
            {
                return false;
            }
        }

        return true;
    }
}
