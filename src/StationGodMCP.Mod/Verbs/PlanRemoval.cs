#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// plan removal (catalogue plan.json): what removing cable, pipe or chute pieces would do, priced over RunPlanner and
/// never done: the pieces by id, along waypoints, in cells, or every piece of a network. The answer is the run's
/// planned view, rows cells listed, without the keys a job has (JobReports.Body). Read only, with no player needed:
/// the refund is an estimate (SourceRule.PlanRemoval).
/// </summary>
[ReplyFact(FactRule.Live, "structures, networks and their contents")]
internal static class PlanRemoval
{
    internal static VerbOutcome Run(PlanRemovalOptions options)
    {
        string label = PlanRemovalOptions.Spec.Label;
        RunKind kind = RunKind.Named(options.Kind ?? "cable");
        Selector selector = options.Selector;
        List<OptionProblem> problems = new List<OptionProblem>();
        string? form = RemoveRunOrders.Form(selector.Ids != null, options.Waypoints, options.Cells,
            selector.Network != null, label, kind.Noun, problems, out VerbError? missing);
        if (problems.Count > 0)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(label, problems));
        }

        if (missing != null)
        {
            return new VerbOutcome.Refused(missing);
        }

        RunRemoval? removal = Removal(form!, options, kind, label, out OptionProblem? unreadable);
        if (removal == null)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(label, new[] { unreadable! }));
        }

        List<ThingId> more = new List<ThingId>();
        ThingId? from = JobTargets.Payers(options.Materials, label, more);
        ThingId? root = options.Root != null ? TargetIds.Of(options.Root, label) : null;
        Fields fields = options.Fields;
        RunOptions run = new RunOptions(new EditAllowance(new HashSet<long>(), Allows(options.Allow, "split")), from,
            options.Materials.RefundTo, (int)(options.Rows ?? RunArgs.DefaultListLimit), true,
            new RunTargets(root, null, false), false,
            new RunDetail(fields.Includes("links"), fields.Includes("notes"), fields.Includes("network_devices")),
            more);
        RunRequest request = new RunRequest(kind, SourceRule.PlanRemoval, null, removal, run);
        return WorkResult<RunPlan>.Of(answer => RunPlanner.Planning(request, true, answer), plan =>
        {
            RunReportView view = RunReports.Of(plan, RunReports.DryRun, null);
            view.NoteTruncations(string.Empty);
            return new VerbOutcome.Answered(JobReports.Body(view, view.Ready));
        });
    }

    // The pieces the call names: by id, every piece of the network, or the piece in each cell along waypoints or
    // listed; null with the problem when the cells make no run.
    private static RunRemoval? Removal(string form, PlanRemovalOptions options, RunKind kind, string label,
        out OptionProblem? unreadable)
    {
        unreadable = null;
        switch (form)
        {
            case RemoveRunOrders.ById:
                List<ThingId> ids = new List<ThingId>(options.Selector.Ids!.Ids.Count);
                foreach (string id in options.Selector.Ids.Ids)
                {
                    ids.Add(TargetIds.Of(id, label));
                }

                return new RunRemoval(ids, new List<GridCell>());
            case RemoveRunOrders.ByNetwork:
                return new RunRemoval(NetworkPieces(options.Selector.Network!, kind), new List<GridCell>());
            default:
                List<GridCell>? cells = RemoveRuns.CellsOf(form,
                    form == RemoveRunOrders.ByWaypoints ? options.Waypoints! : options.Cells!, out string? error);
                if (cells == null)
                {
                    unreadable = new OptionProblem(form, error ?? "is not a run");
                    return null;
                }

                return new RunRemoval(new List<ThingId>(), cells);
        }
    }

    // Every piece of the kind on the network named; network_empty when it has none.
    private static List<ThingId> NetworkPieces(NetworkTarget target, RunKind kind)
    {
        ThingId network = NetworkHandles.Resolve(target, RemoveRunOrders.ByNetwork, kind.Family);
        List<ThingId> ids = new List<ThingId>();
        foreach (SmallGrid member in kind.Family.NetworkMembers(network))
        {
            if (kind.Family.IsPiece(member))
            {
                ids.Add(new ThingId(member.ReferenceId));
            }
        }

        return ids.Count > 0
            ? ids
            : throw ApiErrors.Refused("network_empty", $"network: {network} has no {kind.Noun} piece.");
    }

    private static bool Allows(IReadOnlyList<string>? allow, string guard)
    {
        foreach (string word in allow ?? System.Array.Empty<string>())
        {
            if (word == guard)
            {
                return true;
            }
        }

        return false;
    }
}
