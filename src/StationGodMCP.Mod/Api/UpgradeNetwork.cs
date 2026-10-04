#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects.Electrical;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Runs;
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

/// <summary>
/// clean_chutes: removes every selected chute piece no item can pass through to a consumer, and turns each junction,
/// overflow or splitter that loses a branch into the plain piece its remaining ends need (RemoveDeadChutes,
/// ChuteCleanup). Selected by network, by piece or by box; the rest as clean_cables. Host only.
/// </summary>
internal static class CleanChutesApi
{
    internal const string Tool = "clean_chutes";

    internal static object Handle(Args args)
    {
        if (args.Has("job_id"))
        {
            args.Reject("job_id", "keep_ids", "riding");
        }

        HashSet<long> keep = new HashSet<long>();
        if (args.Has("keep_ids"))
        {
            foreach (ThingId id in args.ThingIds("keep_ids", UpgradePlanner.MaximumPieces))
            {
                keep.Add(id.Value);
            }
        }

        List<ICleanOperation> operations = new List<ICleanOperation> { new RemoveDeadChutes(keep, Riding(args)) };
        return UpgradeApi.Handle(args, new ChuteFamily(), new EndCleanupGoal(Tool, operations));
    }

    private static RidingPolicy Riding(Args args) =>
        (args.OptionalString("riding") ?? "skip").Trim().ToLowerInvariant() switch
        {
            "skip" => RidingPolicy.Skip,
            "refuse" => RidingPolicy.Refuse,
            _ => throw ApiErrors.InvalidArgument("Argument 'riding' must be skip or refuse.")
        };
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
    private const int DefaultListLimit = ReplyDefaults.UpgradeListed;

    internal static object Handle(Args args, UpgradeFamily family, SwapGoal goal)
    {
        if (args.Has("job_id"))
        {
            args.Reject("job_id", "network_id", "reference_ids", "min", "max", "to", "operations", "keep_ids",
                "only_ids", "older_than_id", "root", "wait", "dry_run",
                "confirm", "from_id", "skip_unmatched", "refund", "refund_to", "limit", "include_notes",
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
        PieceSelection selection = Selection(args, family);
        UpgradeOptions options = new UpgradeOptions(
            args.OptionalBool("skip_unmatched") ?? false,
            RefundArgs.RouteWithFlag(args),
            args.OptionalInt("limit", 1, UpgradePlanner.MaximumPieces) ?? DefaultListLimit,
            args.OptionalBool("include_notes") ?? false);
        return new UpgradeRequest(family, goal, selection, args.OptionalThingId("from_id"), options);
    }

    // Exactly one of network_id, reference_ids, or a box (min and max: only clean_chutes lists them).
    private static PieceSelection Selection(Args args, UpgradeFamily family)
    {
        bool network = args.Has("network_id");
        bool pieces = args.Has("reference_ids");
        bool box = args.Has("min") || args.Has("max");
        int forms = (network ? 1 : 0) + (pieces ? 1 : 0) + (box ? 1 : 0);
        if (forms != 1)
        {
            string choices = family is ChuteFamily
                ? "network_id (from connections), reference_ids, or min and max (a box)"
                : "network_id (from connections) or reference_ids";
            throw ApiErrors.InvalidArgument(forms == 0 ? $"Pass {choices}." : $"Pass one of {choices}, not several.");
        }

        if (network)
        {
            return new PieceSelection.Network(NetworkHandles.Resolve(args, "network_id", family));
        }

        return pieces
            ? new PieceSelection.Pieces(args.ThingIds("reference_ids", UpgradePlanner.MaximumPieces))
            : BoxOf(args);
    }

    private static PieceSelection.Box BoxOf(Args args)
    {
        GridCell a = RunArgs.CellOf(RunArgs.PositionOf(args.Optional("min") ??
                                                       throw ApiErrors.InvalidArgument("Pass min too."), "min"));
        GridCell b = RunArgs.CellOf(RunArgs.PositionOf(args.Optional("max") ??
                                                       throw ApiErrors.InvalidArgument("Pass max too."), "max"));
        GridCell min = new GridCell(System.Math.Min(a.X, b.X), System.Math.Min(a.Y, b.Y), System.Math.Min(a.Z, b.Z));
        GridCell max = new GridCell(System.Math.Max(a.X, b.X), System.Math.Max(a.Y, b.Y), System.Math.Max(a.Z, b.Z));
        long cells = SmallCellCode.SmallCountIn(min, max);
        return cells <= MaximumBoxCells
            ? new PieceSelection.Box(min, max)
            : throw ApiErrors.InvalidArgument(
                $"The box holds {cells} half-metre cells; at most {MaximumBoxCells} (a 32 m cube).");
    }

    internal const long MaximumBoxCells = 262144;
}
