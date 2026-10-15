#nullable enable

using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// One id's undo outcome: the plan it would carry out (and, in a confirmed call, the jobs it started), or the refusal
/// that stops that id alone.
/// </summary>
internal abstract class UndoResult
{
    private UndoResult()
    {
    }

    /// <summary>
    /// Planned and checked, and (in a confirmed call) started: the id's plan, the things it would remove and build
    /// again, and the jobs it started in started order.
    /// </summary>
    internal sealed class Undone : UndoResult
    {
        internal Undone(string id, JObject body, List<ReportTarget> targets, List<object> jobs)
        {
            Id = id;
            Body = body;
            Targets = targets;
            Jobs = jobs;
        }

        /// <summary>The job, as its row and its plan name it.</summary>
        internal string Id { get; }

        /// <summary>The plan body: {id, tool, remove, build, piece_runs, notes}.</summary>
        internal JObject Body { get; }

        /// <summary>Every thing the undo would remove and every thing it would build again.</summary>
        internal List<ReportTarget> Targets { get; }

        /// <summary>The removal, placement and piece-run job replies it started, in started order.</summary>
        internal List<object> Jobs { get; }

        /// <summary>The id's plan plus its jobs: an ok row's payload.</summary>
        internal ReplyObject Payload() => new ReplyObject(1).Spread(Body).Add("jobs", Jobs);
    }

    /// <summary>Refused: the id as the call gave it, its place in that list, and the error the acting call answered.</summary>
    internal sealed class Failed : UndoResult
    {
        internal Failed(int index, string id, VerbError error)
        {
            Index = index;
            Id = id;
            Error = error;
        }

        internal int Index { get; }

        internal string Id { get; }

        internal VerbError Error { get; }
    }
}

/// <summary>
/// undo's replies (catalogue undo.json; T-A5-2, T-A5-7, T-A5-8): confirmed, one row per id through the shared Rows
/// writer in the order the ids ran, each ok row's payload that id's plan plus the jobs it started and a refused or
/// diverged id its error - one id answers the single shape, a list the rows shape. Unconfirmed, the one report
/// (Report.Of): every thing the undo would remove and build again a target [id, name, current -> new] with the what's
/// undo mark, the body one id's plan (a list's as {plans: [...]}), and every refused or diverged id a problem about
/// its place in the ids given. Both read the same per-id results, so a confirmed reply and its report cannot drift.
/// </summary>
internal static class UndoReplies
{
    /// <summary>
    /// What a diverged or not-ready id is refused with (T-A5-7): the planner's lines joined, saying which it is.
    /// </summary>
    internal const string RefusedCode = "undo_refused";

    /// <summary>What a start the full job queue refused is answered with (T-A5-9).</summary>
    internal const string QueueFullCode = "queue_full";

    /// <summary>The call's act: the rows, or one id's own plan and jobs as the what's single shape.</summary>
    internal static VerbOutcome Acted(PerIdList ids, IReadOnlyList<UndoResult> results)
    {
        Rows rows = new Rows();
        foreach (UndoResult result in results)
        {
            switch (result)
            {
                case UndoResult.Undone undone:
                    rows.Succeeded(undone.Id, undone.Payload());
                    break;
                case UndoResult.Failed failed:
                    rows.Failed(failed.Id, failed.Error);
                    break;
            }
        }

        return rows.For(ids);
    }

    /// <summary>
    /// The report: every target, the plan as the body, and every problem. asList is true when the call gave a list of
    /// ids: the body is then every plan in plans, one id's own plan otherwise.
    /// </summary>
    internal static ReportReply Reported(IReadOnlyList<UndoResult> results, bool asList)
    {
        List<ReportTarget> targets = new List<ReportTarget>();
        List<ReportProblem> problems = new List<ReportProblem>();
        List<JObject> plans = new List<JObject>();
        JObject? single = null;
        foreach (UndoResult result in results)
        {
            switch (result)
            {
                case UndoResult.Undone undone:
                    targets.AddRange(undone.Targets);
                    if (asList)
                    {
                        plans.Add(undone.Body);
                    }
                    else
                    {
                        single = undone.Body;
                    }

                    break;
                case UndoResult.Failed failed:
                    problems.Add(new ReportProblem(failed.Index, failed.Id, failed.Error));
                    break;
            }
        }

        object? body = asList ? new ReplyObject(1).Add("plans", plans) : single;
        return Report.Of(targets, body, problems);
    }

    /// <summary>One id's refusal: the planner's diverged lines joined into one message (T-A5-7).</summary>
    internal static VerbError Refused(UndoPlan plan) => new VerbError(RefusedCode, string.Join(" ", plan.Diverged));

    /// <summary>
    /// A start the full queue refused (T-A5-9): the job queue holds the most waiting jobs it takes, so this call was
    /// not queued. What that id had already started stays running.
    /// </summary>
    internal static VerbError QueueFull(JObject busy, string id, int started)
    {
        string tool = busy.Value<string>("tool") ?? "the job";
        int waiting = busy.Value<int?>("waiting") ?? 0;
        string stayed = started == 0
            ? $" Nothing of {id} ran."
            : $" The {started} job(s) {id} started before it stay running.";
        return new VerbError(QueueFullCode,
            $"{tool} did not start: {waiting} jobs are already waiting, the most the queue takes; nothing more was " +
            $"queued.{stayed} Wait for one to start, then try again.");
    }

    /// <summary>
    /// The first problem a typed call's report lists: what the acting call would be refused with. Null when the report
    /// lists none, which is the call's readiness (T-A5-2, no ready key).
    /// </summary>
    internal static VerbError? Problem(object reply) =>
        reply is ReportReply { Problems: { Count: > 0 } problems } ? problems[0].Error : null;

    /// <summary>
    /// One id's plan body: the id, the job's tool in verb words, the typed removal call (null when the undo removes
    /// nothing), the typed placement call (null when it builds no structure again), its piece runs and its notes.
    /// </summary>
    internal static JObject Plan(string id, string tool, JObject? remove, JObject? build,
        IReadOnlyList<PieceRun> runs, List<string> notes)
    {
        JArray pieces = new JArray();
        foreach (PieceRun run in runs)
        {
            pieces.Add(run.ToJson());
        }

        return new JObject
        {
            ["id"] = id,
            ["tool"] = tool,
            ["remove"] = remove == null ? JValue.CreateNull() : remove,
            ["build"] = build == null ? JValue.CreateNull() : build,
            ["piece_runs"] = pieces,
            ["notes"] = new JArray(notes)
        };
    }

    /// <summary>
    /// The typed removal call in remove structure's own words: the ids, refund_to (left out when the undo has none),
    /// the one holder every call pays from, and the caller's acknowledge_gas_lost.
    /// </summary>
    internal static JObject RemoveCall(IReadOnlyList<long> ids, RefundRoute? refundTo, long? materials,
        string? acknowledge)
    {
        JArray list = new JArray();
        foreach (long id in ids)
        {
            list.Add(Text(id));
        }

        JObject call = new JObject { ["id"] = list };
        if (refundTo != null)
        {
            call["refund_to"] = RefundArgs.Wire(refundTo);
        }

        if (materials.HasValue)
        {
            call["materials"] = Text(materials.Value);
        }

        if (acknowledge != null)
        {
            call["acknowledge_gas_lost"] = acknowledge;
        }

        return call;
    }

    /// <summary>
    /// The typed placement call in build structure's own words: the placements the job put back as they stood, the one
    /// holder every call pays from, and the caller's acknowledge_gas_lost.
    /// </summary>
    internal static JObject BuildCall(JArray placements, long? materials, string? acknowledge)
    {
        JObject call = new JObject { ["placements"] = placements.DeepClone() };
        if (materials.HasValue)
        {
            call["materials"] = Text(materials.Value);
        }

        if (acknowledge != null)
        {
            call["acknowledge_gas_lost"] = acknowledge;
        }

        return call;
    }

    // The pieces form of one kind and grade: each piece's cells with the ends it had there (a long straight comes back
    // as singles).
    internal static JArray Cells(PieceRestore group)
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

    internal static string Text(long id) => id.ToString(CultureInfo.InvariantCulture);

    /// <summary>The ids as decimal text, in the order given.</summary>
    internal static List<string> Texts(IReadOnlyList<long> ids)
    {
        List<string> texts = new List<string>(ids.Count);
        foreach (long id in ids)
        {
            texts.Add(Text(id));
        }

        return texts;
    }

    /// <summary>
    /// The pieces form of one placement call: each thing the job removed as it stood, where it stood, its turn and
    /// build state, and its label when it had one (the same form the old undo showed as place_arguments).
    /// </summary>
    internal static JArray Placements(List<ThingSnapshot> restore)
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

        return placements;
    }

    /// <summary>
    /// The paste's plan plus BlueprintMod's own answer ({message}): what a confirmed paste answers. The paste has no
    /// jobs of its own: BlueprintMod undoes it at once.
    /// </summary>
    internal static ReplyObject Pasted(JObject body, object reply) => new ReplyObject(1).Spread(body).Add("reply", reply);

    /// <summary>
    /// The removal's own notes: which run takes down the pieces of the job's own kinds (so a network the job changed
    /// in its middle comes back in one job), and refund_to ground where the pieces a build takes down give theirs.
    /// </summary>
    internal static List<string> RemovalNotes(UndoRemovals removals, List<PieceRestore> groups, UndoSource source)
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

    /// <summary>
    /// Where a piece run's refunds go, as its call gives them: nothing back when the undo's source gives nothing back
    /// and the run takes pieces down; left out (null) is the default chain.
    /// </summary>
    internal static RefundRoute? Refunds(UndoSource source, PieceRun run) =>
        source.RefundTo is { GivesBack: false } && run.Own.Count > 0 ? RefundRoute.Nothing : source.RefundTo;

    /// <summary>Who pays a piece run: the undo's holder when it has one, else the player.</summary>
    internal static List<Payer> Payers(UndoSource source)
    {
        List<Payer> payers = new List<Payer>(1);
        payers.Add(source.FromId.HasValue ? new Payer.Holder(Text(source.FromId.Value)) : (Payer)new Payer.Player());
        return payers;
    }

    /// <summary>Who pays a piece run and where its refunds go, for the run's typed call.</summary>
    internal static MaterialsSource Materials(UndoSource source, PieceRun run) =>
        new MaterialsSource(Payers(source), Refunds(source, run) ?? RefundRoute.Default);

    /// <summary>
    /// The source a piece run's own job records: the undo's holder, the route its refunds go when they go somewhere,
    /// and refund false when it gives nothing back for what it takes down.
    /// </summary>
    internal static JobSource Job(UndoSource source, PieceRun run)
    {
        RefundRoute? refunds = Refunds(source, run);
        return new JobSource(source.FromId, false, refunds is { GivesBack: true } ? refunds : null,
            refunds is { GivesBack: false } && run.Own.Count > 0 ? false : (bool?)null);
    }
}

/// <summary>
/// One piece run of an undo: the kind and grade it builds again (what: cable, pipe or chute), the pieces it lays (each
/// cell with the ends it had there) and the pieces it takes down itself (remove_ids). Its reply's plan body and the
/// typed call both come from it, so the two cannot drift apart.
/// </summary>
internal sealed class PieceRun
{
    internal PieceRun(PieceRestore group, List<long> own, List<string> assumed)
    {
        What = group.Kind;
        Grade = group.Grade;
        Own = own;
        Assumed = assumed;
        Pieces = UndoReplies.Cells(group);
    }

    /// <summary>cable, pipe or chute: the kind of piece this run builds again.</summary>
    internal string What { get; }

    internal string Grade { get; }

    /// <summary>remove_ids: the pieces of the same kind and grade the run takes down itself.</summary>
    internal List<long> Own { get; }

    /// <summary>assume_removed: the small-grid things of the rest of the removal, checked as gone.</summary>
    internal List<string> Assumed { get; }

    /// <summary>The pieces form: each piece's cells with the ends it had there.</summary>
    internal JArray Pieces { get; }

    /// <summary>The run as the plan body's piece_runs entry.</summary>
    internal JObject ToJson()
    {
        JArray removeIds = new JArray();
        foreach (long id in Own)
        {
            removeIds.Add(UndoReplies.Text(id));
        }

        JArray assumed = new JArray();
        foreach (string id in Assumed)
        {
            assumed.Add(id);
        }

        return new JObject
        {
            ["what"] = What,
            ["pieces"] = Pieces.DeepClone(),
            ["grade"] = Grade,
            ["remove_ids"] = removeIds,
            ["assume_removed"] = assumed
        };
    }
}
