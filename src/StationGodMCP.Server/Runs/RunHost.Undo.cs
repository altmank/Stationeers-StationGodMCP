using System.Text.Json.Nodes;

namespace StationGodMCP.Server;

/// <summary>
/// undo script-N (and script-&lt;pid&gt;.N from an adopted journal): the run's logic writes reverted newest first, each
/// (id, logic) back to its value before the run. Each target is read first: one that changed since the run is
/// reported and left unless allow: ["overwrite"] (its row carries code target_drifted). Writes that are not logic
/// writes are named, not reverted. Like
/// every undo on the direct face it reports until confirm: true (the pipeline's gate decides). Every call of an undo
/// goes on a stream of its own, pinned to the run's world: a save loaded mid-undo refuses the rest unsent, and the
/// undo answers undo_expired. While a write of the run may still reach the game (unsettled), undo waits for it.
/// </summary>
public sealed partial class RunHost
{
    private const string UndoMark = "[undo]";

    /// <summary>
    /// How long an undo waits for a run's in-flight write to settle before answering run_unsettled: a reply that is
    /// already on its way arrives in microseconds, and one the game never answers still answers the caller's "try
    /// again". The wait is bounded, so a write held up in the game cannot hold the undo.
    /// </summary>
    private const int UnsettleGraceMs = 500;

    /// <summary>undo for a run; null when the id is not a run's (the pipeline's undo answers it).</summary>
    internal async Task<VerbReply?> UndoAsync(JsonObject options)
    {
        string? id = JsonNodes.Text(options["id"])?.Trim();
        if (id == null || !id.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        VerbCall call = new("undo", null, options, Face.Direct);
        if (OptionCheck.Check(_pipeline.Catalogue, call, out _, out _) is { } refused)
        {
            return refused;
        }

        await AdoptedAsync().ConfigureAwait(false);
        if (NotesOf(id) is not { } notes)
        {
            return new VerbReply.Failed("job_not_found", $"No run {id} in this session.");
        }

        RunRecord record = notes.Record;
        if (!record.Adopted && _pipeline.Unsettled(record.Stream))
        {
            await SettleWaitAsync(record.Stream).ConfigureAwait(false);
        }

        if (Unrevertable(record) is { } why)
        {
            return why;
        }

        lock (record.Sync)
        {
            if (record.UndoRunning)
            {
                return new VerbReply.Failed("undo_running", $"an undo of {record.Id} is going now; send it again when it ends.");
            }

            record.UndoRunning = true;
        }

        long stream = _link.NewStream();
        _pipeline.PinWorld(stream, record.World);
        try
        {
            return await UndoOnAsync(record, call, options, stream).ConfigureAwait(false);
        }
        finally
        {
            lock (record.Sync)
            {
                record.UndoRunning = false;
            }

            _pipeline.EndStream(stream);
            await _link.EndStreamAsync(stream).ConfigureAwait(false);
        }
    }

    private async Task<VerbReply> UndoOnAsync(RunRecord record, VerbCall call, JsonObject options, long stream)
    {
        (List<UndoStep> steps, List<string> notUndone) = Plan(record);
        lock (record.Sync)
        {
            steps.RemoveAll(step => record.UndoneTargets.Contains(step.Key));
        }

        bool overwrite = JsonNodes.Holds(options["allow"], "overwrite");
        UndoWorld world = new();
        if (_pipeline.DecideWrite(call) is not WriteDecision.Act)
        {
            VerbReply report = await UndoReportAsync(record, steps, notUndone, overwrite, stream, world).ConfigureAwait(false);
            return world.Gone ? WorldChangedDuringUndo(record) : report;
        }

        JsonArray results = [];
        int reverted = 0;
        int left = 0;
        foreach (UndoStep step in steps)
        {
            (JsonObject row, bool dealt) = await RevertAsync(step, overwrite, stream, world).ConfigureAwait(false);
            if (world.Gone)
            {
                return WorldChangedDuringUndo(record);
            }

            reverted += JsonNodes.IsTrue(row["ok"]) ? 1 : 0;
            results.Add(row);
            lock (record.Sync)
            {
                if (dealt)
                {
                    record.UndoneTargets.Add(step.Key);
                }
                else
                {
                    left++;
                }
            }
        }

        lock (record.Sync)
        {
            // Targets left (changed since the run) keep the run undoable for them, with allow: ["overwrite"].
            record.Undone = left == 0;
        }

        MarkSeen(record.Id);
        return new VerbReply.Done(new JsonObject
        {
            ["undone"] = record.Id,
            ["results"] = results,
            ["count"] = steps.Count,
            ["reverted"] = reverted,
            ["not_undone"] = Strings(notUndone)
        });
    }

    private static VerbReply.Failed WorldChangedDuringUndo(RunRecord record) =>
        new("undo_expired", $"world changed: another world was loaded during the undo of {record.Id}; what was not " +
                            "reverted by then was not sent, because its ids may name other things now.");

    private VerbReply.Failed? Unrevertable(RunRecord record)
    {
        if (record.IsLive)
        {
            return new VerbReply.Failed("run_live", $"{record.Id} is still running; undo reverts a run that has ended.",
                $"stop it first: server {{what: \"stop\", id: \"{record.Id}\"}}");
        }

        if (!record.Adopted && _pipeline.Unsettled(record.Stream))
        {
            return new VerbReply.Failed("run_unsettled",
                $"a write may still land: try again when {record.Id} settles (a write it sent has not been answered).");
        }

        lock (record.Sync)
        {
            if (record.Undone)
            {
                return new VerbReply.Failed("already_undone", $"{record.Id} was undone already.");
            }

            if (record.World == null)
            {
                return new VerbReply.Failed("undo_expired",
                    $"{record.Id}'s world is not known, so its ids cannot be trusted to name the same things now.");
            }

            if (record.World != _link.WorldId)
            {
                return new VerbReply.Failed("undo_expired",
                    $"world changed: {record.Id} ran in another world, so the ids it wrote may name other things now.");
            }

            if (record.Forgotten != null)
            {
                return new VerbReply.Failed("undo_expired",
                    $"{record.Id}'s write log was let go to keep the hub within its memory; it can no longer revert: " +
                    record.Forgotten);
            }
        }

        return null;
    }

    // A run that ended with a write in flight leaves that call unsettled until its reply comes (late: the run gave up,
    // the mod still answered). The reply settles the call and fills the write log's before/after values (Run.LateFill),
    // so the undo waits briefly for it; one that never comes still answers run_unsettled and the caller's "try again"
    // stands.
    private async Task SettleWaitAsync(long stream)
    {
        DateTime start = DateTime.UtcNow;
        while (_pipeline.Unsettled(stream) &&
               DateTime.UtcNow - start < TimeSpan.FromMilliseconds(UnsettleGraceMs))
        {
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    // Newest first: each logic target once, at its newest write, back to its value before the run's first write to it.
    // A newest write that may or may not have run also accepts the value before it (the last one known to have landed).
    private static (List<UndoStep> Steps, List<string> NotUndone) Plan(RunRecord record)
    {
        List<UndoStep> steps = [];
        List<string> notUndone = [];
        HashSet<string> planned = new(StringComparer.Ordinal);
        lock (record.Sync)
        {
            for (int index = record.Writes.Count - 1; index >= 0; index--)
            {
                WriteEntry write = record.Writes[index];
                if (!write.Acted)
                {
                    continue;
                }

                if (write.Verb != "write" || write.What != "logic")
                {
                    notUndone.Add($"{write.Label} x{write.Targets.Count} {write.UndoMark}: not reverted by undo of a run");
                    continue;
                }

                for (int target = write.Targets.Count - 1; target >= 0; target--)
                {
                    WrittenTarget written = write.Targets[target];
                    if (!written.Acted || written.Field == null || !planned.Add(written.Id + "\n" + written.Field))
                    {
                        continue;
                    }

                    JsonNode? before = BeforeRun(record, written.Id, written.Field);
                    JsonNode? alternative = write.MaybeRan ? LandedBefore(record, index, written.Id, written.Field) ?? before : null;
                    steps.Add(new UndoStep(written.Id, written.Field, before, (written.After ?? written.New)?.DeepClone(),
                        alternative?.DeepClone()));
                }
            }
        }

        return (steps, notUndone);
    }

    // Under the record's lock: the target's value before the run's first acted write to it.
    private static JsonNode? BeforeRun(RunRecord record, string id, string logic)
    {
        foreach (WriteEntry write in record.Writes)
        {
            if (!write.Acted || write.What != "logic")
            {
                continue;
            }

            foreach (WrittenTarget target in write.Targets)
            {
                if (target.Acted && target.Id == id && target.Field == logic)
                {
                    return target.Before?.DeepClone();
                }
            }
        }

        return null;
    }

    // Under the record's lock: the value the newest write before index known to have landed left on the target.
    private static JsonNode? LandedBefore(RunRecord record, int index, string id, string logic)
    {
        for (int earlier = index - 1; earlier >= 0; earlier--)
        {
            WriteEntry write = record.Writes[earlier];
            if (!write.Acted || write.MaybeRan || write.What != "logic")
            {
                continue;
            }

            foreach (WrittenTarget target in write.Targets)
            {
                if (target.Acted && target.Id == id && target.Field == logic)
                {
                    return (target.After ?? target.New)?.DeepClone();
                }
            }
        }

        return null;
    }

    private async Task<VerbReply> UndoReportAsync(RunRecord record, List<UndoStep> steps, List<string> notUndone,
        bool overwrite, long stream, UndoWorld world)
    {
        List<JsonNode?> current = await CurrentAsync(steps, stream, world).ConfigureAwait(false);
        JsonArray rows = [];
        for (int index = 0; index < steps.Count; index++)
        {
            UndoStep step = steps[index];
            JsonNode? now = current[index];
            JsonObject row = VerbPipeline.Row(step.Id, null, null, now, step.Before, UndoMark);
            row["logic"] = step.Logic;
            Verdict verdict = Judge(step, now, overwrite);
            if (verdict == Verdict.Changed)
            {
                row["code"] = VerbPipeline.DriftedCode;
            }

            string? note = verdict switch
            {
                Verdict.Unknown => "its value before the run is unknown, so it cannot be reverted",
                Verdict.Nothing => "nothing to revert: it holds its value from before the run",
                Verdict.Changed => $"changed since the run: it holds {Run.Shown(now)}, the run left {Run.Shown(step.Last)}; " +
                                   "allow: [\"overwrite\"] writes it back anyway",
                _ => null
            };
            if (note != null)
            {
                row["note"] = note;
                row["text"] = JsonNodes.Text(row["text"]) + " (" + note + ")";
            }

            rows.Add(row);
        }

        return new VerbReply.Done(new JsonObject
        {
            ["report_only"] = true,
            ["undo"] = record.Id,
            ["targets"] = rows,
            ["not_undone"] = Strings(notUndone)
        });
    }

    // One target: read it now, judge it, and write its value from before the run when that is right. Dealt: it needs
    // no further undo (reverted, already back, or with no value to go back to).
    private async Task<(JsonObject Row, bool Dealt)> RevertAsync(UndoStep step, bool overwrite, long stream, UndoWorld world)
    {
        List<JsonNode?> current = await CurrentAsync([step], stream, world).ConfigureAwait(false);
        JsonNode? now = current[0];
        JsonObject row = new() { ["id"] = step.Id, ["logic"] = step.Logic, ["value"] = step.Before?.DeepClone() };
        Verdict verdict = Judge(step, now, overwrite);
        if (world.Gone || verdict != Verdict.Write)
        {
            row["ok"] = verdict == Verdict.Nothing;
            row["now"] = now?.DeepClone();
            if (verdict == Verdict.Changed)
            {
                row["code"] = VerbPipeline.DriftedCode;
            }

            row["note"] = verdict switch
            {
                Verdict.Nothing => "nothing to revert",
                Verdict.Unknown => "its value before the run is unknown",
                _ => $"changed since the run (it holds {Run.Shown(now)}, the run left {Run.Shown(step.Last)}); not " +
                     "written without allow: [\"overwrite\"]"
            };
            return (row, verdict != Verdict.Changed && !world.Gone);
        }

        VerbReply wrote = await _pipeline.CallAsync(new VerbCall("write", "logic", new JsonObject
        {
            ["writes"] = new JsonArray(new JsonObject
            {
                ["id"] = step.Id,
                ["logic"] = step.Logic,
                ["value"] = step.Before?.DeepClone()
            })
        }, Face.Script, stream) { Gate = PassThroughGate.Instance }, CancellationToken.None).ConfigureAwait(false);
        if (wrote is VerbReply.Failed failed)
        {
            world.Gone |= failed.Code == "world_changed";
            row["ok"] = false;
            row["error"] = failed.ToJson();
            return (row, false);
        }

        List<JsonObject> results = BulkWrites.Rows(((VerbReply.Done)wrote).Reply);
        JsonObject? result = results.Count > 0 ? results[0] : null;
        row["ok"] = result?["ok"] is not { } ok || JsonNodes.IsTrue(ok);
        row["previous"] = now?.DeepClone();
        if (result?["error"] is { } error)
        {
            row["error"] = error.DeepClone();
        }

        return (row, JsonNodes.IsTrue(row["ok"]));
    }

    private enum Verdict
    {
        Write,
        Nothing,
        Changed,
        Unknown
    }

    // A maybe-ran write is settled by what the target holds: its before value means it never landed; the value it
    // wrote, or the last one known to have landed, is the run's and is put back.
    private static Verdict Judge(UndoStep step, JsonNode? now, bool overwrite)
    {
        if (step.Before == null)
        {
            return Verdict.Unknown;
        }

        if (Run.SameValue(now, step.Before))
        {
            return Verdict.Nothing;
        }

        bool runs = Run.SameValue(now, step.Last) || (step.Alternative != null && Run.SameValue(now, step.Alternative));
        return runs || overwrite ? Verdict.Write : Verdict.Changed;
    }

    // What each target holds now, in order (null where the read failed); a world change marks the undo gone.
    private async Task<List<JsonNode?>> CurrentAsync(List<UndoStep> steps, long stream, UndoWorld world)
    {
        List<JsonNode?> values = [];
        if (steps.Count == 0)
        {
            return values;
        }

        JsonArray reads = [];
        foreach (UndoStep step in steps)
        {
            reads.Add(new JsonObject { ["id"] = step.Id, ["logic"] = step.Logic });
        }

        VerbReply read = await _pipeline.CallAsync(new VerbCall("read", "logic", new JsonObject { ["reads"] = reads },
            Face.Script, stream), CancellationToken.None).ConfigureAwait(false);
        if (read is VerbReply.Failed { Code: "world_changed" })
        {
            world.Gone = true;
        }

        List<JsonObject> rows = read is VerbReply.Done done ? BulkWrites.Rows(done.Reply) : [];
        for (int index = 0; index < steps.Count; index++)
        {
            values.Add(index < rows.Count && (rows[index]["ok"] == null || JsonNodes.IsTrue(rows[index]["ok"]))
                ? rows[index]["value"]?.DeepClone()
                : null);
        }

        return values;
    }

    private static JsonArray Strings(List<string> items)
    {
        JsonArray list = [];
        foreach (string item in items)
        {
            list.Add(item);
        }

        return list;
    }

    /// <summary>Set when the undo's world went away under it (a call answered world_changed).</summary>
    private sealed class UndoWorld
    {
        internal bool Gone { get; set; }
    }

    /// <summary>
    /// One target to revert: its id and logic type, its value before the run, the value the run left, and for a write
    /// that may or may not have landed the value before it.
    /// </summary>
    private sealed class UndoStep(string id, string logic, JsonNode? before, JsonNode? last, JsonNode? alternative)
    {
        internal string Id { get; } = id;

        internal string Logic { get; } = logic;

        internal JsonNode? Before { get; } = before;

        internal JsonNode? Last { get; } = last;

        internal JsonNode? Alternative { get; } = alternative;

        internal string Key => Id + "\n" + Logic;
    }
}
