#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects.Electrical;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api;

/// <summary>
/// upgrade_cables: replace a cable network's pieces, or listed pieces, with heavy or super heavy cable in place.
/// The game itself refuses to place heavy cable over normal (Cable.CanReplace: CannotMergeIMergeableOfDifferentType),
/// so this rebuilds each piece the way the coil's merge does. Dry run by default; a real run needs dry_run false and
/// confirm true, and is a job polled with job_id (UpgradeJobs). Host only.
/// </summary>
internal static class UpgradeCablesApi
{
    internal static object Handle(Args args) => UpgradeApi.Handle(args, new CableFamily(), Goal(args));

    private static SwapGoal Goal(Args args)
    {
        string target = (args.OptionalString("to") ?? "heavy").Trim().ToLowerInvariant();
        return target switch
        {
            "heavy" => new CableUpgrade(Cable.Type.heavy),
            "super_heavy" => new CableUpgrade(Cable.Type.superHeavy),
            _ => throw ApiErrors.InvalidArgument("Argument 'to' must be heavy or super_heavy.")
        };
    }
}

/// <summary>
/// upgrade_pipes: replace a pipe network's normal pieces, or listed pieces, with insulated pipe of the same content
/// (gas to insulated gas, liquid to insulated liquid), in place, keeping the network's contents. Otherwise as
/// upgrade_cables.
/// </summary>
internal static class UpgradePipesApi
{
    internal static object Handle(Args args) => UpgradeApi.Handle(args, new PipeFamily(), Goal(args));

    private static SwapGoal Goal(Args args)
    {
        string target = (args.OptionalString("to") ?? "insulated").Trim().ToLowerInvariant();
        return target == "insulated"
            ? new PipeUpgrade()
            : throw ApiErrors.InvalidArgument("Argument 'to' must be insulated.");
    }
}

/// <summary>
/// clean_cables: tidy a cable network in place. simplify_junctions replaces pieces with open ends by the piece of the
/// same coil with only the connected ends (a 3-way junction joining two neighbours becomes a straight or a corner, a
/// 4-way joining three a T, and so on); split_long_straights replaces each 3-, 5- or 10-long straight by one single
/// straight per cell. Pieces with one connected end or none are reported, never changed. The same dry run, confirm,
/// job and checks as upgrade_cables (EndCleanupGoal). Host only.
/// </summary>
internal static class CleanCablesApi
{
    internal static object Handle(Args args) => CleanApi.Handle(args, "clean_cables", new CableFamily());
}

/// <summary>clean_pipes: clean_cables for pipe networks, keeping each network's contents (PipeFamily).</summary>
internal static class CleanPipesApi
{
    internal static object Handle(Args args) => CleanApi.Handle(args, "clean_pipes", new PipeFamily());
}

/// <summary>The clean tools' own argument: operations, default simplify_junctions only (CleanOperationSet).</summary>
internal static class CleanApi
{
    internal static object Handle(Args args, string tool, UpgradeFamily family)
    {
        args.Reject(tool, "to");
        return UpgradeApi.Handle(args, family, new EndCleanupGoal(tool, Operations(args)));
    }

    private static List<ICleanOperation> Operations(Args args)
    {
        List<string> names = new List<string>();
        if (args.Has("operations"))
        {
            foreach (JToken entry in args.Array("operations", CleanOperationSet.Order.Length))
            {
                names.Add(entry.Type == JTokenType.String ? (string)entry! : string.Empty);
            }
        }
        else
        {
            names.AddRange(CleanOperationSet.Default);
        }

        HashSet<long> keep = new HashSet<long>();
        bool loops = names.Exists(name => name.Trim().ToLowerInvariant() == CleanOperationSet.RemoveLoops);
        bool redundant = names.Exists(name => name.Trim().ToLowerInvariant() == CleanOperationSet.RemoveRedundant);
        if (args.Has("keep_ids"))
        {
            if (!loops && !redundant)
            {
                throw ApiErrors.InvalidArgument(
                    "keep_ids spares pieces from remove_loops and remove_redundant; ask for one of them too.");
            }

            foreach (ThingId id in args.ThingIds("keep_ids", UpgradePlanner.MaximumPieces))
            {
                keep.Add(id.Value);
            }
        }

        foreach (string name in new[] { "only_ids", "older_than_id", "root" })
        {
            if (args.Has(name) && !redundant)
            {
                throw ApiErrors.InvalidArgument($"{name} goes with remove_redundant; ask for it too.");
            }
        }

        HashSet<long>? only = null;
        if (args.Has("only_ids"))
        {
            only = new HashSet<long>();
            foreach (ThingId id in args.ThingIds("only_ids", UpgradePlanner.MaximumPieces))
            {
                only.Add(id.Value);
            }
        }

        RedundancyOptions redundancy = new RedundancyOptions(only, args.OptionalThingId("older_than_id")?.Value,
            args.OptionalThingId("root")?.Value);
        return CleanOperationCatalogue.Parse(names, new CleanOptions(keep, redundancy));
    }
}

/// <summary>The request forms the swap tools share: a dry run, a confirmed run, or a job's status.</summary>
internal static class UpgradeApi
{
    private const int DefaultListLimit = 200;

    internal static object Handle(Args args, UpgradeFamily family, SwapGoal goal)
    {
        if (args.Has("job_id"))
        {
            args.Reject("job_id", "network_id", "reference_ids", "to", "operations", "keep_ids", "only_ids",
                "older_than_id", "root", "wait", "dry_run",
                "confirm", "from_id", "skip_unmatched", "refund", "refund_to", "limit",
                GasHoldVerdict.AcknowledgeArgument);
            return HeldTickJobs.Status(args.String("job_id").Trim());
        }

        bool dryRun = args.OptionalBool("dry_run") ?? true;
        bool confirm = args.OptionalBool("confirm") ?? false;
        if (dryRun && confirm)
        {
            throw ApiErrors.InvalidArgument("confirm: true needs dry_run: false; nothing was changed.");
        }

        if (!dryRun && !confirm)
        {
            throw ApiErrors.Refused("confirm_required",
                "A real run needs dry_run: false and confirm: true; nothing was changed.");
        }

        UpgradeRequest request = Parse(args, family, goal);
        UpgradePlan plan = UpgradePlanner.Plan(request);
        string? acknowledge = GasHoldArgs.Acknowledgement(args);
        if (dryRun)
        {
            GasHold.Preview(request.Family is PipeFamily, acknowledge);
            return UpgradeReports.Of(plan, UpgradeReports.DryRun, null);
        }

        return plan.Ready
            ? UpgradeJobs.Start(request, plan, args.OptionalBool("wait") ?? false, acknowledge)
            : UpgradeReports.Of(plan, UpgradeReports.Refused, null);
    }

    private static UpgradeRequest Parse(Args args, UpgradeFamily family, SwapGoal goal)
    {
        bool network = args.Has("network_id");
        if (network == args.Has("reference_ids"))
        {
            throw ApiErrors.InvalidArgument(network
                ? "Pass network_id (from connections) or reference_ids, not both."
                : "Pass network_id (from connections) or reference_ids.");
        }

        PieceSelection selection = network
            ? new PieceSelection.Network(NetworkHandles.Resolve(args, "network_id", family))
            : new PieceSelection.Pieces(args.ThingIds("reference_ids", UpgradePlanner.MaximumPieces));
        UpgradeOptions options = new UpgradeOptions(
            args.OptionalBool("skip_unmatched") ?? false,
            RefundArgs.RouteWithFlag(args),
            args.OptionalInt("limit", 1, UpgradePlanner.MaximumPieces) ?? DefaultListLimit);
        return new UpgradeRequest(family, goal, selection, args.OptionalThingId("from_id"), options);
    }
}
