#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.Objects;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Build;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Jobs;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// undo (catalogue undo.json; D-A4-15, T-A5-2, T-A5-7, T-A5-8, T-A5-9): the inverse of a finished place or remove job
/// (UndoPlanner): remove every thing it built, then build again every thing it removed, each as it stood (the snapshot
/// taken when the job started: JobSnapshots). Cable, pipe and chute pieces come back through the build runs (the
/// pieces form, one run per kind and grade), so their would_bridge, burst and gas guards apply; everything else through
/// build structure. The pieces the job built of a kind that a piece run also builds are removed by that kind's first
/// run (remove_ids, UndoRemovals), so a network the job changed in its middle is taken apart and put back in one job;
/// only the rest goes through remove structure first. A job the world diverged from (not finished, already undone, a
/// thing it built gone or changed, a removed thing with no snapshot, a piece off the grid's axes) answers undo_refused
/// and nothing of that id runs (T-A5-7); a job past the memory bound answers undo_expired, an id the mod never had
/// job_not_found. Unconfirmed, the one report (T-A5-2): every thing the undo would remove and build again a target
/// with the what's mark, one plan per id, and every refused or diverged id a problem about its place in the ids
/// given. Confirmed, one row per id through the shared writer, in the order the ids ran: the id's plan plus the jobs
/// it started (remove structure's, the placements' and the piece runs', the removal first and the rest queued behind
/// it), or that id's error - the others still act. Every typed call is checked as a report before anything starts; a
/// start the full queue refuses is that id's queue_full (T-A5-9), what that id started first staying running. A real
/// run is recorded (JobSnapshots.RecordUndo): undoing the same job again answers undo_refused, already undone. Job ids
/// run newest first (their numeric suffix, HeldTickJobs' one counter); "paste" is undone on its own (T-A5-8:
/// BlueprintMod's own undo of the newest paste) and never beside job ids. allow and from_step are a bulk write's
/// words, undone by the hub, and are refused naming that undo (T-A5-2(b)); a write-N or script-14 id refuses the whole
/// call through HubJobs, every such id at once.
/// </summary>
[ReplyFact(FactRule.Live, "what it changes, read and checked when it acts")]
internal static class Undo
{
    /// <summary>The newest blueprint paste, which BlueprintMod itself undoes (T-A5-8).</summary>
    private const string Paste = "paste";

    /// <summary>The paste's tool, as its plan names it: the what that made it.</summary>
    private const string PasteTool = "build blueprint";

    private const string Removed = "removed";

    internal static VerbOutcome Run(UndoOrders order)
    {
        List<OptionProblem> problems = order.Problems;
        PerIdList? ids = order.Ids;
        if (ids != null && HubJobs.Refusal(UndoOptions.Spec, ids) is { } hub)
        {
            return new VerbOutcome.Refused(hub);
        }

        if (ids == null)
        {
            return new VerbOutcome.Refused(UndoOrders.Missing());
        }

        BulkWords(order, problems);
        PasteAlone(ids, problems);
        if (problems.Count > 0)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(UndoOptions.Spec.Label, problems));
        }

        if (ids.Ids.Count == 1 && IsPaste(ids.Ids[0]))
        {
            StationGodMod.Log("undo paste: BlueprintMod's own undo of the newest paste.");
            return PasteUndo(order);
        }

        long? holder = Holder(order);
        List<UndoResult> results = new List<UndoResult>(ids.Ids.Count);
        foreach ((string id, int given) in UndoOrders.NewestFirst(ids.Ids))
        {
            UndoResult result = One(order, id, given, holder);
            Note(result);
            results.Add(result);
        }

        return order.Acts
            ? UndoReplies.Acted(ids, results)
            : new VerbOutcome.Answered(UndoReplies.Reported(results, ids.AnswersRows));
    }

    // One line on the mod's log per id: what the undo planned and did, or why it did not.
    private static void Note(UndoResult result)
    {
        switch (result)
        {
            case UndoResult.Undone undone:
                StationGodMod.Log($"undo {undone.Id}: {undone.Body.Value<string>("tool")}, " +
                    $"{undone.Targets.Count} target(s), {undone.Jobs.Count} job(s) started.");
                break;
            case UndoResult.Failed failed:
                StationGodMod.Log($"undo {failed.Id}: {failed.Error.Code}: {failed.Error.Message}");
                break;
        }
    }

    // One id's undo: its facts, its plan and the typed calls it makes, checked as a report before anything starts; and,
    // in a confirmed call, the jobs it starts. Returns the refusal that stops that id alone for a job the world
    // diverged from, a check the acting call would be refused on, or a job the mod no longer keeps.
    private static UndoResult One(UndoOrders call, string id, int given, long? holder)
    {
        try
        {
            RecordedJob? recorded = JobSnapshots.Of(id);
            JobFacts facts = Facts(id, recorded);
            UndoPlan plan = UndoPlanner.Plan(facts, Standing);
            UndoSource source = UndoSource.Of(recorded?.Source ?? JobSource.Unknown, holder, call.HolderGiven,
                call.RefundTo);
            plan.Notes.AddRange(source.Notes);
            Work work = new Work(id, facts.Tool, plan, source);
            JObject body = UndoReplies.Plan(id, facts.Tool,
                work.Removals.Structures.Count > 0
                    ? UndoReplies.RemoveCall(work.Removals.Structures, source.RefundTo, source.FromId,
                        call.AcknowledgeGasLost)
                    : null,
                work.Restore.Count > 0
                    ? UndoReplies.BuildCall(work.Placements, source.FromId, call.AcknowledgeGasLost)
                    : null,
                work.Runs, plan.Notes);
            if (plan.Diverged.Count > 0)
            {
                return new UndoResult.Failed(given, id, UndoReplies.Refused(plan));
            }

            if (Check(call, work) is { } refused)
            {
                return new UndoResult.Failed(given, id, refused);
            }

            return new UndoResult.Undone(id, body, Targets(plan, call.Mark),
                call.Acts ? Start(call, work) : new List<object>());
        }
        catch (ApiException refused)
        {
            return new UndoResult.Failed(given, id, VerbError.Of(refused));
        }
    }

    // The pre-check the old undo made before it started any of its jobs: every typed call as a report, in the order
    // the undo runs them, and the first problem one lists is the id's refusal. A report carries its problems, not a
    // ready key, so no problem is readiness. The placement call is checked only when nothing is removed first: behind
    // the removal it is judged when its job starts, on the world the removal leaves.
    private static VerbError? Check(UndoOrders call, Work work)
    {
        if (work.Removals.Structures.Count > 0 && UndoReplies.Problem(Removal(call, work, acts: false)) is { } removal)
        {
            return removal;
        }

        if (work.Removals.Structures.Count == 0 && work.Restore.Count > 0 &&
            UndoReplies.Problem(Build(call, work, acts: false)) is { } build)
        {
            return build;
        }

        for (int index = 0; index < work.Runs.Count; index++)
        {
            if (UndoReplies.Problem(Piece(call, work, index, acts: false)) is { } run)
            {
                return run;
            }
        }

        return null;
    }

    // The act: the removal first, then the placements and the piece runs queued behind it (wait), each job's reply
    // collected in started order and the undo recorded (JobSnapshots.RecordUndo), so undoing the same job again is
    // refused as already undone.
    private static List<object> Start(UndoOrders call, Work work)
    {
        List<object> jobs = new List<object>();
        List<string> started = new List<string>();
        if (work.Removals.Structures.Count > 0)
        {
            Started(jobs, started, work.Id, Removal(call, work, acts: true));
        }

        if (work.Restore.Count > 0)
        {
            Started(jobs, started, work.Id, Build(call, work, acts: true));
        }

        for (int index = 0; index < work.Runs.Count; index++)
        {
            Started(jobs, started, work.Id, Piece(call, work, index, acts: true));
        }

        JobSnapshots.RecordUndo(work.Id, started);
        return jobs;
    }

    // One started call's reply: the queued or started job, or queue_full when the queue takes no more waiting jobs
    // (T-A5-9). Nothing of that id is started after a refusal; what it started before stays running, as the old undo
    // left it.
    private static void Started(List<object> jobs, List<string> started, string id, JObject reply)
    {
        if (JobSnapshots.JobIdOf(reply) is not { } jobId)
        {
            VerbError full = UndoReplies.QueueFull(reply, id, started.Count);
            throw ApiErrors.Refused(full.Code, full.Message);
        }

        jobs.Add(reply);
        started.Add(jobId);
    }

    // The typed removal call, for RemoveStructureRun: the same call remove structure itself makes. A report neither
    // acts nor queues; a real run queues behind the removal before it.
    private static JObject Removal(UndoOrders call, Work work, bool acts) =>
        (JObject)RemoveStructureRun.Reply(RemoveStructureOrder.Replay(work.Removals.Structures, work.Source.FromId,
            work.Source.RefundTo, call.AcknowledgeGasLost, acts, acts));

    // The typed placement call, for StructureBuild: the same call build structure itself makes.
    private static JObject Build(UndoOrders call, Work work, bool acts)
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        StructureBuildOrder? order = StructureBuildOrder.Replay(work.Placements,
            work.Source.FromId.HasValue ? UndoReplies.Text(work.Source.FromId.Value) : null, acts, acts,
            call.AcknowledgeGasLost, problems);
        return order != null
            ? (JObject)StructureBuild.Reply(order)
            : throw ApiErrors.InvalidArgument(
                OptionProblem.Refusal(BuildStructureOptions.Spec.Label, problems).Message);
    }

    // The typed call one piece run makes, for BuildRuns: the pieces and grade it lays again, the pieces it takes down
    // itself (remove_ids), the rest of the removal checked as gone (assume_removed), the caller's allow_bridge and the
    // undo's source. A call that cannot be read refuses the id (the same refusal the old arguments gave).
    private static JObject Piece(UndoOrders call, Work work, int index, bool acts)
    {
        PieceRun run = work.Runs[index];
        List<OptionProblem> problems = new List<OptionProblem>();
        RunBuildOrder? order = RunBuildOrder.Replay(run.What, run.Grade, run.Pieces, UndoReplies.Texts(run.Own),
            run.Assumed, call.AllowBridge, UndoReplies.Materials(work.Source, run), UndoReplies.Job(work.Source, run),
            acts, acts, call.AcknowledgeGasLost, problems);
        return order != null
            ? (JObject)BuildRuns.Reply(RunKind.Named(run.What), order)
            : throw ApiErrors.InvalidArgument(OptionProblem.Refusal(RunBuildOrder.SpecOf(run.What).Label, problems).Message);
    }

    // undo "paste" (T-A5-8): BlueprintMod's own undo of the newest paste. Unconfirmed, the one report over an empty
    // target list, its body the paste's plan; confirmed, the plan plus BlueprintPastes.Undo()'s reply ({message}),
    // whose refusal (mod_missing, blueprint_failed, not_host) is the row's error.
    private static VerbOutcome PasteUndo(UndoOrders call)
    {
        JObject body = UndoReplies.Plan(Paste, PasteTool, null, null, Array.Empty<PieceRun>(), PasteNotes());
        PerIdList ids = call.Ids!;
        if (!call.Acts)
        {
            List<UndoResult> reported = new List<UndoResult>(1);
            reported.Add(new UndoResult.Undone(Paste, body, new List<ReportTarget>(), new List<object>()));
            return new VerbOutcome.Answered(UndoReplies.Reported(reported, ids.AnswersRows));
        }

        Rows rows = new Rows();
        rows.Add(Paste, () => UndoReplies.Pasted(body, BlueprintPastes.Undo()));
        return rows.For(ids);
    }

    // The paste's plan note (T-A5-8): BlueprintMod does the work, so there is nothing to report of it beforehand.
    private static List<string> PasteNotes()
    {
        List<string> notes = new List<string>(1);
        notes.Add("BlueprintMod undoes the newest paste itself; confirm: true runs it.");
        return notes;
    }

    // Every thing the id's undo would remove and every thing it would build again, with the what's declared mark
    // (T-A5-2): what it built stands as the job left it (prefab -> "removed"), and what it removed stands nowhere
    // (-> the prefab the snapshot carries).
    private static List<ReportTarget> Targets(UndoPlan plan, string mark)
    {
        List<ReportTarget> targets = new List<ReportTarget>(plan.Remove.Count + plan.Restore.Count);
        foreach (long id in plan.Remove)
        {
            GameLookup.TryFindThing(new ThingId(id), out Thing thing);
            targets.Add(new ReportTarget(UndoReplies.Text(id), null, thing != null ? Names.Of(thing) : null,
                thing != null ? new JValue(thing.PrefabName) : JValue.CreateNull(), new JValue(Removed), mark));
        }

        foreach (ThingSnapshot snapshot in plan.Restore)
        {
            targets.Add(new ReportTarget(UndoReplies.Text(snapshot.Id), null, snapshot.Prefab, JValue.CreateNull(),
                new JValue(snapshot.Prefab), mark));
        }

        return targets;
    }

    // The job's facts, as the undo reads them: its final view while the mod keeps it (JobEndFacts: the lists its
    // job_ended event carries), with its snapshots for what it removed (JobSnapshots). A job whose snapshots were
    // evicted answers undo_expired; an id the mod never had, job_not_found.
    private static JobFacts Facts(string id, RecordedJob? recorded)
    {
        JObject? kept = KeptJob(id, out ApiException? notFound);
        UndoFactsSource from = UndoFactsSource.Choose(kept, JobSnapshots.Evicted(id), HeldTickJobs.Evicted(id), id);
        switch (from)
        {
            case UndoFactsSource.Kept own:
                JObject job = own.Facts;
                return new JobFacts(id, job.Value<string>("tool") ?? recorded?.Tool ?? "unknown",
                    job.Value<string>("status") ?? "unknown", JobEndFacts.Created(job), JobEndFacts.Removed(job),
                    recorded?.Removed ?? new Dictionary<long, ThingSnapshot>(),
                    JobSnapshots.UndoOf(id).ConvertAll(StepOf));
            case UndoFactsSource.Expired expired:
                throw new ApiException(UndoFactsSource.Expired.Code, expired.Message,
                    new Dictionary<string, string> { ["job_id"] = id });
            default:
                throw notFound!;
        }
    }

    // The job as its polls answer it (running, queued or among the finished ones kept); null with the refusal when
    // the mod no longer keeps it.
    private static JObject? KeptJob(string jobId, out ApiException? notFound)
    {
        notFound = null;
        try
        {
            return JobSnapshots.Wire(HeldTickJobs.Status(jobId));
        }
        catch (ApiException refused) when (refused.Code == HeldTickJobs.JobNotFoundCode)
        {
            notFound = refused;
            return null;
        }
    }

    // A job an earlier undo started, with its status now; unknown once it is no longer among the jobs kept.
    private static UndoStep StepOf(string undoJobId)
    {
        try
        {
            return new UndoStep(undoJobId,
                JobSnapshots.Wire(HeldTickJobs.Status(undoJobId)).Value<string>("status") ?? UndoStep.Unknown);
        }
        catch (ApiException)
        {
            return new UndoStep(undoJobId, UndoStep.Unknown);
        }
    }

    private static string? Standing(long id) =>
        GameLookup.TryFindThing(new ThingId(id), out Thing thing) && !thing.IsBeingDestroyed ? thing.PrefabName : null;

    // The small-grid things of the rest of the removal the run checks as gone.
    private static List<string> AssumedIds(UndoPlan plan, List<long> own)
    {
        List<string> assumed = new List<string>();
        foreach (long id in plan.Remove)
        {
            if (!own.Contains(id) && GameLookup.TryFindThing(new ThingId(id), out Thing thing) && thing is SmallGrid)
            {
                assumed.Add(UndoReplies.Text(id));
            }
        }

        return assumed;
    }

    // The holder the call named (materials): the thing by id, through the reader every verb's materials takes (a
    // name that reads as no thing refuses the call), or null for the player.
    private static long? Holder(UndoOrders order) =>
        order.Holder is Payer.Holder held ? TargetIds.Of(held.Id, UndoOptions.Spec.Label).Value : (long?)null;

    // T-A5-2(b): allow and from_step are a bulk write's words. The mod's own job ids do not take them, and they are
    // never silent: they go with the hub's write-N undo, which is the undo's own caller.
    private static void BulkWords(UndoOrders call, List<OptionProblem> problems)
    {
        if (call.Allow != null)
        {
            problems.Add(new OptionProblem("allow", "goes with a bulk write's undo: the hub's write-N undo takes " +
                "allow: [\"overwrite\"]"));
        }

        if (call.FromStep.HasValue)
        {
            problems.Add(new OptionProblem("from_step", "goes with a bulk write's undo: the hub's write-N undo goes " +
                "on from a step with it"));
        }
    }

    // T-A5-8(c): paste goes alone. BlueprintMod gives the paste no time, so listing it beside job ids would invent an
    // order between it and the jobs.
    private static void PasteAlone(PerIdList ids, List<OptionProblem> problems)
    {
        if (ids.Ids.Count < 2)
        {
            return;
        }

        foreach (string id in ids.Ids)
        {
            if (IsPaste(id))
            {
                problems.Add(new OptionProblem("id", "undo paste on its own: BlueprintMod gives the paste no time, " +
                    "so listing it beside job ids would invent an order between it and them"));
                return;
            }
        }
    }

    private static bool IsPaste(string id) =>
        string.Equals(id.Trim(), Paste, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One id's undo as planned: the facts and the plan, the typed calls it makes (the removal of what the job built,
    /// the placements and the piece runs that build back what it removed), and the plan's own piece runs.
    /// </summary>
    private sealed class Work
    {
        internal Work(string id, string tool, UndoPlan plan, UndoSource source)
        {
            Id = id;
            Tool = tool;
            Plan = plan;
            Source = source;
            Groups = plan.RestorePieces;
            Removals = UndoRemovals.Of(plan.Remove, Groups, JobSnapshots.PieceToolOf);
            Restore = plan.RestoreStructures;
            Placements = UndoReplies.Placements(Restore);
            Runs = new List<PieceRun>(Groups.Count);
            for (int index = 0; index < Groups.Count; index++)
            {
                Runs.Add(new PieceRun(Groups[index], Removals.ByRun[index],
                    AssumedIds(plan, Removals.ByRun[index])));
            }

            plan.Notes.AddRange(UndoReplies.RemovalNotes(Removals, Groups, source));
        }

        internal string Id { get; }

        internal string Tool { get; }

        internal UndoPlan Plan { get; }

        internal UndoSource Source { get; }

        /// <summary>The network pieces restored, one group per place tool and grade.</summary>
        internal List<PieceRestore> Groups { get; }

        /// <summary>Who removes what the undo removes: the rest through remove structure, a kind's own pieces in its run.</summary>
        internal UndoRemovals Removals { get; }

        /// <summary>What build structure builds again: everything restored that is not a network piece.</summary>
        internal List<ThingSnapshot> Restore { get; }

        /// <summary>The placements the build call puts back, as each stood.</summary>
        internal JArray Placements { get; }

        internal List<PieceRun> Runs { get; }
    }
}
