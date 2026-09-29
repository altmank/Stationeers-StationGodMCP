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
/// place_structure every thing it removed, each as it stood (the snapshot taken when the job started: JobSnapshots).
/// Refused when the world diverged from what the job left. Dry run by default: the plan, both tools' arguments and the
/// removal's own dry run (the placements are checked when their job starts, after the removal). A real run starts the
/// removal job and queues the placement job behind it (wait), so the placements are checked against the world the
/// removal left; every guard of both tools applies (gas in a pipe network refuses the removal, as it would by hand).
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

        JObject job = JobSnapshots.Wire(HeldTickJobs.Status(jobId));
        (string Tool, Dictionary<long, ThingSnapshot> Removed)? recorded = JobSnapshots.Of(jobId);
        string tool = job.Value<string>("tool") ?? recorded?.Tool ?? "unknown";
        JobFacts facts = new JobFacts(jobId, tool, job.Value<string>("status") ?? "unknown", Created(job),
            Removed(job), recorded?.Removed ?? new Dictionary<long, ThingSnapshot>());
        UndoPlan plan = UndoPlanner.Plan(facts, Standing);
        JObject? removeArguments = plan.Remove.Count > 0 ? RemoveArguments(plan) : null;
        JObject? placeArguments = plan.Restore.Count > 0 ? PlaceArguments(plan) : null;
        if (dryRun || !plan.Ready)
        {
            object? removal = plan.Ready && removeArguments != null
                ? RemoveStructureApi.Handle(new Args((JObject)removeArguments.DeepClone()))
                : null;
            return new UndoJobView(jobId, tool, dryRun ? "dry_run" : "refused", ViewOf(plan), removeArguments,
                placeArguments, removal, null);
        }

        object? removeJob = null;
        if (removeArguments != null)
        {
            JObject run = (JObject)removeArguments.DeepClone();
            run["dry_run"] = false;
            run["confirm"] = true;
            removeJob = RemoveStructureApi.Handle(new Args(run));
            if (JobSnapshots.Wire(removeJob)["job_id"] == null)
            {
                return new UndoJobView(jobId, tool, "refused", ViewOf(plan), removeArguments, placeArguments,
                    removeJob, null);
            }
        }

        object? placeJob = null;
        if (placeArguments != null)
        {
            JObject run = (JObject)placeArguments.DeepClone();
            run["dry_run"] = false;
            run["confirm"] = true;
            run["wait"] = true;
            placeJob = PlaceStructureApi.Handle(new Args(run));
        }

        return new UndoJobView(jobId, tool, "scheduled", ViewOf(plan), removeArguments, placeArguments, removeJob,
            placeJob);
    }

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

    private static JObject RemoveArguments(UndoPlan plan)
    {
        JArray ids = new JArray();
        foreach (long id in plan.Remove)
        {
            ids.Add(id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return new JObject { ["reference_ids"] = ids };
    }

    private static JObject PlaceArguments(UndoPlan plan)
    {
        JArray placements = new JArray();
        foreach (ThingSnapshot snapshot in plan.Restore)
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

        return new JObject { ["placements"] = placements };
    }

    private static UndoPlanView ViewOf(UndoPlan plan) =>
        new UndoPlanView(plan.Remove.ConvertAll(id => new ThingId(id)),
            plan.Restore.ConvertAll(snapshot => new ThingId(snapshot.Id)), plan.Diverged, plan.Notes, plan.Ready);

    private static IEnumerable<JToken> Array(JToken? token) =>
        token is JArray array ? array : (IEnumerable<JToken>)new JArray();

    private static long Id(JToken? token) =>
        token != null && ThingId.TryRead(token, out ThingId id) ? id.Value : 0L;
}
