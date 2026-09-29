#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api;

/// <summary>
/// undo_job: the inverse of a finished place or remove job (UndoPlanner): remove_structure every thing it built, then
/// build again every thing it removed, each as it stood (the snapshot taken when the job started: JobSnapshots): cable,
/// pipe and chute pieces through place_cables, place_pipes and place_chutes (the pieces form, one call per tool and
/// grade), so their would_bridge, burst and gas guards apply; everything else through place_structure. The pieces the
/// job built of a tool that also builds pieces again are removed by that tool's first piece run (remove_ids,
/// UndoRemovals), so a network the job changed in its middle is taken apart and put back in one job; only the rest goes
/// through remove_structure first. Refused when the world diverged from what the job left. Dry run by default: the plan, every tool's arguments and dry run (the
/// piece runs checked as if the removal were done, assume_removed; place_structure only when nothing is removed
/// first); ready only when every one of them is. A real run makes the same checks, then starts the removal job and
/// queues the placement jobs behind it (wait), each checked again against the world the jobs before it left. Every
/// call takes the job's own from_id (UndoSource), or the caller's; removing what a free placement built gives nothing
/// back.
/// </summary>
internal static class UndoJobApi
{
    internal static UndoJobView Handle(Args args)
    {
        string jobId = args.String("job_id").Trim();
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

        long? fromId = args.OptionalThingId("from_id")?.Value;
        string? refundTo = RefundTo(args);
        JObject job = JobSnapshots.Wire(HeldTickJobs.Status(jobId));
        RecordedJob? recorded = JobSnapshots.Of(jobId);
        string tool = job.Value<string>("tool") ?? recorded?.Tool ?? "unknown";
        JobFacts facts = new JobFacts(jobId, tool, job.Value<string>("status") ?? "unknown", Created(job),
            Removed(job), recorded?.Removed ?? new Dictionary<long, ThingSnapshot>());
        UndoPlan plan = UndoPlanner.Plan(facts, Standing);
        UndoSource source = UndoSource.Of(recorded?.Source ?? JobSource.Unknown, fromId, refundTo);
        plan.Notes.AddRange(source.Notes);
        List<PieceRestore> groups = plan.RestorePieces;
        UndoRemovals removals = UndoRemovals.Of(plan.Remove, groups, JobSnapshots.PieceToolOf);
        plan.Notes.AddRange(RemovalNotes(removals, groups, source));
        JObject? removeArguments =
            removals.Structures.Count > 0 ? RemoveArguments(removals.Structures, source) : null;
        List<ThingSnapshot> structures = plan.RestoreStructures;
        JObject? placeArguments = structures.Count > 0 ? PlaceArguments(structures, source) : null;
        List<UndoPieceRunView> pieceRuns = new List<UndoPieceRunView>(groups.Count);
        for (int index = 0; index < groups.Count; index++)
        {
            pieceRuns.Add(new UndoPieceRunView(groups[index].Tool,
                PieceArguments(groups[index], removals.ByRun[index], plan, args, source), null));
        }

        if (!plan.Ready)
        {
            return new UndoJobView(jobId, tool, dryRun ? "dry_run" : "refused", ViewOf(plan, false), removeArguments,
                placeArguments, null, null, pieceRuns);
        }

        object? removal = removeArguments != null ? RemoveStructureApi.Handle(DryRun(removeArguments)) : null;
        object? placement = placeArguments != null && plan.Remove.Count == 0
            ? PlaceStructureApi.Handle(DryRun(placeArguments))
            : null;
        List<UndoPieceRunView> checkedRuns = pieceRuns.ConvertAll(run =>
            new UndoPieceRunView(run.Tool, run.Arguments, PlaceTool(run.Tool, DryRun(run.Arguments))));
        bool ready = IsReady(removal) && IsReady(placement) && checkedRuns.TrueForAll(run => IsReady(run.Reply));
        if (dryRun || !ready)
        {
            return new UndoJobView(jobId, tool, dryRun ? "dry_run" : "refused", ViewOf(plan, ready), removeArguments,
                placeArguments, removal, placement, checkedRuns);
        }

        return Start(jobId, tool, plan, removeArguments, placeArguments, pieceRuns);
    }

    // The removal job first; the placements queued behind it (wait), each checked again when it starts.
    private static UndoJobView Start(string jobId, string tool, UndoPlan plan, JObject? removeArguments,
        JObject? placeArguments, List<UndoPieceRunView> pieceRuns)
    {
        object? removeJob = null;
        if (removeArguments != null)
        {
            removeJob = RemoveStructureApi.Handle(RealRun(removeArguments));
            if (JobSnapshots.Wire(removeJob)["job_id"] == null)
            {
                return new UndoJobView(jobId, tool, "refused", ViewOf(plan, false), removeArguments, placeArguments,
                    removeJob, null, pieceRuns);
            }
        }

        object? placeJob = placeArguments != null ? PlaceStructureApi.Handle(RealRun(placeArguments)) : null;
        List<UndoPieceRunView> started = pieceRuns.ConvertAll(run =>
            new UndoPieceRunView(run.Tool, run.Arguments, PlaceTool(run.Tool, RealRun(run.Arguments))));
        return new UndoJobView(jobId, tool, "scheduled", ViewOf(plan, true), removeArguments, placeArguments,
            removeJob, placeJob, started);
    }

    private static object PlaceTool(string tool, Args args) => tool switch
    {
        "place_cables" => PlaceCablesApi.Handle(args),
        "place_pipes" => PlacePipesApi.Handle(args),
        "place_chutes" => PlaceChutesApi.Handle(args),
        _ => throw new System.ArgumentException($"No place tool {tool}.", nameof(tool))
    };

    private static Args DryRun(JObject arguments) => new Args((JObject)arguments.DeepClone());

    private static Args RealRun(JObject arguments)
    {
        JObject run = (JObject)arguments.DeepClone();
        run["dry_run"] = false;
        run["confirm"] = true;
        run["wait"] = true;
        return new Args(run);
    }

    // A tool's reply is ready when it says so; no reply (nothing to do there) holds nothing up.
    private static bool IsReady(object? reply) =>
        reply == null || JobSnapshots.Wire(reply).Value<bool?>("ready") == true;

    private static string? Standing(long id) =>
        GameLookup.TryFindThing(new ThingId(id), out Thing thing) && !thing.IsBeingDestroyed ? thing.PrefabName : null;

    // Built things: a run's created_ids (new and changed pieces) with the prefab from placed or changed; a structure
    // job's placed.
    private static List<(long, string?)> Created(JObject job)
    {
        Dictionary<long, string?> prefabs = new Dictionary<long, string?>();
        JToken? log = job["log"];
        JToken? result = job["result"];
        foreach (JToken placed in Array(log?["placed"]))
        {
            prefabs[Id(placed["reference_id"])] = placed.Value<string>("prefab_name");
        }

        foreach (JToken changed in Array(log?["changed"]))
        {
            prefabs[Id(changed["new_reference_id"])] = changed.Value<string>("target_prefab_name");
        }

        List<(long, string?)> created = new List<(long, string?)>();
        foreach (JToken id in Array(log?["created_ids"]))
        {
            long value = Id(id);
            created.Add((value, prefabs.TryGetValue(value, out string? prefab) ? prefab : null));
        }

        foreach (JToken placed in Array(result?["placed"]))
        {
            created.Add((Id(placed["reference_id"]), placed.Value<string>("prefab_name")));
        }

        return created;
    }

    // Removed things: a run's removed ids and its changed pieces' old ids; a structure job's removed.
    private static List<long> Removed(JObject job)
    {
        List<long> removed = new List<long>();
        JToken? log = job["log"];
        foreach (JToken id in Array(log?["removed"]))
        {
            removed.Add(Id(id));
        }

        foreach (JToken changed in Array(log?["changed"]))
        {
            removed.Add(Id(changed["old_reference_id"]));
        }

        foreach (JToken piece in Array(job["result"]?["removed"]))
        {
            removed.Add(Id(piece["reference_id"]));
        }

        return removed;
    }

    private static string? RefundTo(Args args)
    {
        string? refundTo = args.OptionalString("refund_to")?.Trim().ToLowerInvariant();
        return refundTo == null || refundTo == "source" || refundTo == "ground" || refundTo == "none"
            ? refundTo
            : throw ApiErrors.InvalidArgument("refund_to must be source, ground or none.");
    }

    private static string Text(long id) => id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // from_id on every call that has a source; refund_to on the removal.
    private static JObject WithSource(JObject arguments, UndoSource source, bool removal)
    {
        if (source.FromId.HasValue)
        {
            arguments["from_id"] = Text(source.FromId.Value);
        }

        if (removal && source.RefundTo != null)
        {
            arguments["refund_to"] = source.RefundTo;
        }

        return arguments;
    }

    private static JObject RemoveArguments(List<long> remove, UndoSource source) =>
        WithSource(new JObject { ["reference_ids"] = Ids(remove) }, source, true);

    private static JArray Ids(IEnumerable<long> ids)
    {
        JArray array = new JArray();
        foreach (long id in ids)
        {
            array.Add(Text(id));
        }

        return array;
    }

    // Which run removes the job's pieces, and what refund_to means for them.
    private static List<string> RemovalNotes(UndoRemovals removals, List<PieceRestore> groups, UndoSource source)
    {
        List<string> notes = new List<string>();
        for (int index = 0; index < groups.Count; index++)
        {
            int count = removals.ByRun[index].Count;
            if (count > 0)
            {
                notes.Add($"{count} piece(s) the job built are removed by {groups[index].Tool} itself (remove_ids), " +
                          "in the job that builds the old pieces again: the network is never left cut between two " +
                          "jobs, and that run's guards see it as it ends up.");
            }
        }

        if (source.RefundTo == "ground" && removals.InRuns > 0)
        {
            notes.Add("refund_to ground applies to remove_structure only; the pieces a piece run removes give their " +
                      "refund to the source, as that tool does.");
        }

        return notes;
    }

    private static JObject PlaceArguments(List<ThingSnapshot> restore, UndoSource source)
    {
        JArray placements = new JArray();
        foreach (ThingSnapshot snapshot in restore)
        {
            (int x, int y, int z) = snapshot.Turn!.EulerTurns();
            JObject placement = new JObject
            {
                ["prefab"] = snapshot.Prefab,
                ["at"] = new JArray(snapshot.Position.X, snapshot.Position.Y, snapshot.Position.Z),
                ["rotation"] = new JArray(x * 90, y * 90, z * 90),
                ["build_state"] = snapshot.BuildState
            };
            if (snapshot.Label != null)
            {
                placement["label"] = snapshot.Label;
            }

            placements.Add(placement);
        }

        return WithSource(new JObject { ["placements"] = placements }, source, false);
    }

    // The pieces form of one tool and grade: each piece's cells with the ends it had there (a long straight comes back
    // as singles), removing in the same job the pieces it takes down itself (remove_ids; refund_to none: refund false),
    // checked as if what the other steps remove were gone already (assume_removed: the small-grid things of the rest of
    // the removal), with the caller's allow_bridge and the undo's source.
    private static JObject PieceArguments(PieceRestore group, List<long> own, UndoPlan plan, Args args,
        UndoSource source)
    {
        JArray pieces = new JArray();
        foreach (ThingSnapshot snapshot in group.Pieces)
        {
            foreach (PieceCell cell in snapshot.Piece!.Cells)
            {
                JArray ends = new JArray();
                foreach (GridStep step in cell.Ends.Steps())
                {
                    ends.Add(step.Name);
                }

                pieces.Add(new JObject
                {
                    ["at"] = new JArray(cell.Cell.X / 10.0, cell.Cell.Y / 10.0, cell.Cell.Z / 10.0),
                    ["ends"] = ends
                });
            }
        }

        JObject arguments = WithSource(new JObject { ["pieces"] = pieces, ["grade"] = group.Grade }, source, false);
        if (own.Count > 0)
        {
            arguments["remove_ids"] = Ids(own);
            if (source.RefundTo == "none")
            {
                arguments["refund"] = false;
            }
        }

        JArray assumed = new JArray();
        foreach (long id in plan.Remove)
        {
            if (!own.Contains(id) && GameLookup.TryFindThing(new ThingId(id), out Thing thing) && thing is SmallGrid)
            {
                assumed.Add(Text(id));
            }
        }

        if (assumed.Count > 0)
        {
            arguments["assume_removed"] = assumed;
        }

        JToken? allow = args.Optional("allow_bridge");
        if (allow != null)
        {
            arguments["allow_bridge"] = allow.DeepClone();
        }

        return arguments;
    }

    private static UndoPlanView ViewOf(UndoPlan plan, bool ready) =>
        new UndoPlanView(plan.Remove.ConvertAll(id => new ThingId(id)),
            plan.Restore.ConvertAll(snapshot => new ThingId(snapshot.Id)), plan.Diverged, plan.Notes, ready);

    private static IEnumerable<JToken> Array(JToken? token) =>
        token is JArray array ? array : (IEnumerable<JToken>)new JArray();

    private static long Id(JToken? token) =>
        token != null && ThingId.TryRead(token, out ThingId id) ? id.Value : 0L;
}
