#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Jobs;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Verbs;
using StationGodMCP.Verbs;

namespace StationGodMCP.Api;

/// <summary>
/// undo_job: the inverse of a finished place or remove job (UndoPlanner): remove_structure every thing it built, then
/// build again every thing it removed, each as it stood (the snapshot taken when the job started: JobSnapshots): cable,
/// pipe and chute pieces through the build service (the pieces form, one call per kind and grade), so their
/// would_bridge, burst and gas guards apply; everything else through place_structure. The pieces the job built of a
/// kind that a piece run also builds are removed by that kind's first run (remove_ids, UndoRemovals), so a network the
/// job changed in its middle is taken apart and put back in one job; only the rest goes through remove_structure
/// first. Refused when the world diverged from what the job left. Dry run by default: the plan, every tool's arguments and dry run (the
/// piece runs checked as if the removal were done, assume_removed; place_structure only when nothing is removed
/// first); ready only when every one of them is. A real run makes the same checks, then starts the removal job and
/// queues the placement jobs behind it (wait), each checked again against the world the jobs before it left. Every
/// call takes the job's own from_id (UndoSource), or the caller's; removing what a free placement built gives nothing
/// back. A real run is recorded (JobSnapshots.RecordUndo): undoing the same job again is refused as already undone, with
/// the undo's own jobs named, unless every one of them was refused. acknowledge_gas_lost is passed on to every call, so a
/// held pipe job is judged by the tool that runs it; a real run the hold would refuse in any of its calls is refused
/// before the first job starts (GasHoldReply.Refusal). What the job built and removed comes from its final view while
/// HeldTickJobs keeps it, else from the request's created_ids, removed, changed and placed while its snapshots are kept
/// (UndoFactsSource); both are kept bounded by memory, a job pinned to its caller's stream outlasting the rest
/// (KeptJobs). A job whose snapshots were evicted answers undo_expired naming it (data: {job_id}).
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
            throw ApiErrors.InvalidArgument("confirm: true needs dry_run: false; nothing " +
                "was changed.");
        }

        if (!dryRun && !confirm)
        {
            throw ApiErrors.Refused("confirm_required",
                "A real run needs dry_run: false and confirm: true; nothing was changed.");
        }

        long? fromId = args.OptionalThingId("from_id")?.Value;
        RefundRoute? refundTo = RefundArgs.RouteOf(args.Optional(RefundArgs.Argument));
        string? acknowledge = GasHoldArgs.Acknowledgement(args);
        JObject? given = JobEndFacts.FromRequest(args.Optional(JobEndFacts.CreatedIdsKey),
            args.Optional(JobEndFacts.RemovedKey), args.Optional(JobEndFacts.ChangedKey),
            args.Optional(JobEndFacts.PlacedKey), out string? problem);
        if (problem != null)
        {
            throw ApiErrors.InvalidArgument(problem + " Nothing was changed.");
        }

        RecordedJob? recorded = JobSnapshots.Of(jobId);
        JObject? kept = KeptJob(jobId, out ApiException? notFound);
        UndoFactsSource from = UndoFactsSource.Choose(kept, recorded != null, given, JobSnapshots.Evicted(jobId),
            HeldTickJobs.Evicted(jobId), jobId);
        JObject job = from switch
        {
            UndoFactsSource.Kept own => own.Facts,
            UndoFactsSource.Given request => request.Facts,
            UndoFactsSource.Expired expired => throw new ApiException(UndoFactsSource.Expired.Code, expired.Message,
                new Dictionary<string, string> { ["job_id"] = jobId }),
            UndoFactsSource.NotFound { FactsGiven: true } => throw ApiErrors.Refused(notFound!.Code,
                notFound.Message + " Its snapshots are not kept either, " +
                "so the created_ids, removed, changed and placed passed are not enough to undo it."),
            _ => throw notFound!
        };
        string tool = job.Value<string>("tool") ?? recorded?.Tool ?? "unknown";
        string status = from is UndoFactsSource.Given
            ? UndoFactsSource.Given.Status
            : job.Value<string>("status") ?? "unknown";
        JobFacts facts = new JobFacts(jobId, tool, status, JobEndFacts.Created(job), JobEndFacts.Removed(job),
            recorded?.Removed ?? new Dictionary<long, ThingSnapshot>(), JobSnapshots.UndoOf(jobId).ConvertAll(StepOf));
        UndoPlan plan = UndoPlanner.Plan(facts, Standing);
        if (from is UndoFactsSource.Given)
        {
            plan.Notes.Insert(0, UndoFactsSource.Given.Note(jobId));
        }
        UndoSource source = UndoSource.Of(recorded?.Source ?? JobSource.Unknown, fromId, refundTo);
        plan.Notes.AddRange(source.Notes);
        List<PieceRestore> groups = plan.RestorePieces;
        UndoRemovals removals = UndoRemovals.Of(plan.Remove, groups, JobSnapshots.PieceToolOf);
        plan.Notes.AddRange(RemovalNotes(removals, groups, source));
        JObject? removeArguments = removals.Structures.Count > 0
            ? Acknowledging(RemoveArguments(removals.Structures, source), acknowledge)
            : null;
        List<ThingSnapshot> structures = plan.RestoreStructures;
        JObject? placeArguments = structures.Count > 0
            ? Acknowledging(PlaceArguments(structures, source), acknowledge)
            : null;
        List<UndoPieceRunView> pieceRuns = new List<UndoPieceRunView>(groups.Count);
        List<RunBuildOrder> orders = new List<RunBuildOrder>(groups.Count);
        for (int index = 0; index < groups.Count; index++)
        {
            PieceRun run = new PieceRun(groups[index], removals.ByRun[index], plan);
            pieceRuns.Add(new UndoPieceRunView(run.Kind,
                Acknowledging(PieceArguments(run, args, source), acknowledge), null));
            orders.Add(PieceOrder(run, args, source, acknowledge, acts: false, queue: false));
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
        List<UndoPieceRunView> checkedRuns = new List<UndoPieceRunView>(groups.Count);
        for (int index = 0; index < groups.Count; index++)
        {
            checkedRuns.Add(new UndoPieceRunView(pieceRuns[index].Tool, pieceRuns[index].Arguments,
                BuildRuns.Reply(RunKind.Named(groups[index].Kind), orders[index])));
        }

        bool ready = IsReady(removal) && IsReady(placement) && checkedRuns.TrueForAll(run => IsReady(run.Reply));
        if (dryRun || !ready)
        {
            return new UndoJobView(jobId, tool, dryRun ? "dry_run" : "refused", ViewOf(plan, ready), removeArguments,
                placeArguments, removal, placement, checkedRuns);
        }

        // The checks above judged the hold for every call; one it refuses would otherwise stop the undo half way.
        if (GasHoldReply.Refusal is { } held)
        {
            throw GasHold.Refusal(held);
        }

        return Start(jobId, tool, plan, removeArguments, placeArguments, pieceRuns, groups, removals, args, source,
            acknowledge);
    }

    // The removal job first; the placements queued behind it (wait), each checked again when it starts.
    private static UndoJobView Start(string jobId, string tool, UndoPlan plan, JObject? removeArguments,
        JObject? placeArguments, List<UndoPieceRunView> pieceRuns, List<PieceRestore> groups, UndoRemovals removals,
        Args args, UndoSource source, string? acknowledge)
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
        List<UndoPieceRunView> started = new List<UndoPieceRunView>(groups.Count);
        for (int index = 0; index < groups.Count; index++)
        {
            PieceRun run = new PieceRun(groups[index], removals.ByRun[index], plan);
            RunBuildOrder order = PieceOrder(run, args, source, acknowledge, acts: true, queue: true);
            started.Add(new UndoPieceRunView(pieceRuns[index].Tool, pieceRuns[index].Arguments,
                BuildRuns.Reply(RunKind.Named(run.Kind), order)));
        }

        JobSnapshots.RecordUndo(jobId, StartedJobs(removeJob, placeJob, started));
        return new UndoJobView(jobId, tool, "scheduled", ViewOf(plan, true), removeArguments, placeArguments,
            removeJob, placeJob, started);
    }

    // The ids of the jobs an undo started: its removal, its placement and its piece runs.
    private static List<string> StartedJobs(object? removeJob, object? placeJob, List<UndoPieceRunView> pieceRuns)
    {
        List<object?> replies = new List<object?> { removeJob, placeJob };
        replies.AddRange(pieceRuns.ConvertAll(run => run.Reply));
        List<string> ids = new List<string>();
        foreach (object? reply in replies)
        {
            string? id = JobSnapshots.JobIdOf(reply);
            if (id != null)
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    // The caller's acknowledge_gas_lost on one of the calls, so the tool that runs a held pipe job judges it.
    private static JObject Acknowledging(JObject arguments, string? acknowledge)
    {
        if (acknowledge != null)
        {
            arguments[GasHoldVerdict.AcknowledgeArgument] = acknowledge;
        }

        return arguments;
    }

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

    // The job as its polls answer it (running, queued or among the finished ones kept); null with the refusal when the
    // mod no longer keeps it.
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

    private static string? Standing(long id) =>
        GameLookup.TryFindThing(new ThingId(id), out Thing thing) && !thing.IsBeingDestroyed ? thing.PrefabName : null;

    private static string Text(long id) => id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // from_id on every call that has a source; refund_to on the removal (a piece run's is set in PieceArguments).
    private static JObject WithSource(JObject arguments, UndoSource source, bool removal)
    {
        if (source.FromId.HasValue)
        {
            arguments["from_id"] = Text(source.FromId.Value);
        }

        if (removal && source.RefundTo != null)
        {
            arguments[RefundArgs.Argument] = RefundArgs.Wire(source.RefundTo);
        }

        return arguments;
    }

    private static JObject RemoveArguments(List<long> remove, UndoSource source) =>
        WithSource(new JObject { ["reference_ids"] = Ids(remove) }, source, true);

    private static JArray Ids(IEnumerable<long> ids)
    {
        JArray array = new JArray();
        foreach (string id in Texts(ids))
        {
            array.Add(id);
        }

        return array;
    }

    private static List<string> Texts(IEnumerable<long> ids)
    {
        List<string> texts = new List<string>();
        foreach (long id in ids)
        {
            texts.Add(Text(id));
        }

        return texts;
    }

    private static JArray Texts(IReadOnlyList<string> ids)
    {
        JArray array = new JArray();
        foreach (string id in ids)
        {
            array.Add(id);
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
                notes.Add($"{count} piece(s) the job built are removed by the {groups[index].Kind} run " +
                    "itself (remove_ids), in the job that builds the old pieces again: the network is never left " +
                    "cut between two jobs, and that run's guards see it as it ends up.");
            }
        }

        if (source.RefundTo == RefundRoute.WherePieceStood && removals.InRuns > 0)
        {
            notes.Add("refund_to ground: removing a structure puts each refund where its piece stood; the pieces " +
                "a build takes down give theirs on the ground in front of the source, as it does.");
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

    /// <summary>
    /// One piece run of an undo: the kind and grade it builds again, the pieces it lays (each cell with the ends it
    /// had there) and the pieces it takes down itself (remove_ids). Its reply's arguments and the typed call both come
    /// from it, so the two cannot drift apart.
    /// </summary>
    private sealed class PieceRun
    {
        internal PieceRun(PieceRestore group, List<long> own, UndoPlan plan)
        {
            Kind = group.Kind;
            Grade = group.Grade;
            Own = own;
            Pieces = PieceCells(group);
            Assumed = AssumedIds(plan, own);
        }

        internal string Kind { get; }

        internal string Grade { get; }

        /// <summary>remove_ids: the pieces the same run takes down.</summary>
        internal List<long> Own { get; }

        /// <summary>The pieces form: each piece's cells with the ends it had there (a long straight comes back as
        /// singles).</summary>
        internal JArray Pieces { get; }

        /// <summary>assume_removed: the small-grid things of the rest of the removal, checked as gone.</summary>
        internal List<string> Assumed { get; }
    }

    // The pieces form of one kind and grade: each piece's cells with the ends it had there (a long straight comes back
    // as singles).
    private static JArray PieceCells(PieceRestore group)
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

        return pieces;
    }

    // The small-grid things of the rest of the removal the run checks as gone.
    private static List<string> AssumedIds(UndoPlan plan, List<long> own)
    {
        List<string> assumed = new List<string>();
        foreach (long id in plan.Remove)
        {
            if (!own.Contains(id) && GameLookup.TryFindThing(new ThingId(id), out Thing thing) && thing is SmallGrid)
            {
                assumed.Add(Text(id));
            }
        }

        return assumed;
    }

    // Where the run's refunds go, as its call gives them: nothing back when the undo's source gives nothing back and
    // the run takes pieces down; left out (null) is the default chain.
    private static RefundRoute? Refunds(UndoSource source, PieceRun run) =>
        source.RefundTo is { GivesBack: false } && run.Own.Count > 0 ? RefundRoute.Nothing : source.RefundTo;

    // Who pays: the undo's from_id when it has one, else the player.
    private static List<Payer> Payers(UndoSource source)
    {
        List<Payer> payers = new List<Payer>(1);
        payers.Add(source.FromId.HasValue ? new Payer.Holder(Text(source.FromId.Value)) : new Payer.Player());
        return payers;
    }

    // The typed call one piece run makes, for BuildRuns: the pieces and grade it lays again, the pieces it takes down
    // itself, the rest of the removal checked as gone, the caller's allow_bridge and the undo's source. A call that
    // cannot be read refuses the undo (the same refusal the old arguments gave).
    private static RunBuildOrder PieceOrder(PieceRun run, Args args, UndoSource source, string? acknowledge, bool acts,
        bool queue)
    {
        RefundRoute? refunds = Refunds(source, run);
        MaterialsSource materials = new MaterialsSource(Payers(source), refunds ?? RefundRoute.Default);
        JobSource job = new JobSource(source.FromId, false, refunds is { GivesBack: true } ? refunds : null,
            refunds is { GivesBack: false } && run.Own.Count > 0 ? false : (bool?)null);
        List<OptionProblem> problems = new List<OptionProblem>();
        RunBuildOrder? order = RunBuildOrder.Replay(run.Kind, run.Grade, run.Pieces, Texts(run.Own), run.Assumed,
            args.Optional("allow_bridge"), materials, job, acts, queue, acknowledge, problems);
        return order ?? throw ApiErrors.InvalidArgument(
            OptionProblem.Refusal(RunBuildOrder.SpecOf(run.Kind).Label, problems).Message);
    }

    // The pieces form of one call as undo_job's reply shows it (piece_runs[].arguments), and what it takes down
    // itself: refund_to none is refund false, any other refund_to is passed on, and the caller's allow_bridge.
    private static JObject PieceArguments(PieceRun run, Args args, UndoSource source)
    {
        JObject arguments = WithSource(new JObject { ["pieces"] = run.Pieces, ["grade"] = run.Grade }, source, false);
        if (run.Own.Count > 0)
        {
            arguments["remove_ids"] = Ids(run.Own);
            if (source.RefundTo is { GivesBack: false })
            {
                arguments[RefundArgs.Flag] = false;
            }
        }

        if (source.RefundTo is { GivesBack: true })
        {
            arguments[RefundArgs.Argument] = RefundArgs.Wire(source.RefundTo);
        }

        if (run.Assumed.Count > 0)
        {
            arguments["assume_removed"] = Texts(run.Assumed);
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
            plan.Restore.ConvertAll(snapshot => new ThingId(snapshot.Id)), plan.Diverged, plan.Notes, ready,
            plan.UndoneBy);
}
