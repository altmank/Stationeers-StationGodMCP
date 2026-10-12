using System.Text.Json.Nodes;

namespace StationGodMCP.Server;

/// <summary>
/// undo script-N (and script-&lt;pid&gt;.N from an adopted journal): the run's logic writes reverted newest first, each
/// (id, logic) back to its value before the run. Each target is read first: one that changed since the run is
/// reported and left unless allow: ["overwrite"]. Writes that are not logic writes are named, not reverted. Like
/// every undo on the direct face it reports until confirm: true (the pipeline's gate decides).
/// </summary>
public sealed partial class RunHost
{
    private const string UndoMark = "[undo]";

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
        if (Unrevertable(record) is { } why)
        {
            return why;
        }

        (List<UndoStep> steps, List<string> notUndone) = Plan(record);
        lock (record.Sync)
        {
            steps.RemoveAll(step => record.UndoneTargets.Contains(step.Key));
        }

        bool overwrite = JsonNodes.Holds(options["allow"], "overwrite");
        if (_pipeline.DecideWrite(call) is not WriteDecision.Act)
        {
            return await UndoReportAsync(record, steps, notUndone, overwrite).ConfigureAwait(false);
        }

        JsonArray results = [];
        int reverted = 0;
        int left = 0;
        foreach (UndoStep step in steps)
        {
            (JsonObject row, bool dealt) = await RevertAsync(step, overwrite).ConfigureAwait(false);
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

    private VerbReply.Failed? Unrevertable(RunRecord record)
    {
        if (record.IsLive)
        {
            return new VerbReply.Failed("run_live", $"{record.Id} is still running; undo reverts a run that has ended.",
                $"stop it first: server {{what: \"stop\", id: \"{record.Id}\"}}");
        }

        lock (record.Sync)
        {
            if (record.Undone)
            {
                return new VerbReply.Failed("already_undone", $"{record.Id} was undone already.");
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

    // Newest first: each logic target once, at its newest write, back to its value before the run's first write to it.
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

                    steps.Add(new UndoStep(written.Id, written.Field, BeforeRun(record, written.Id, written.Field),
                        (written.After ?? written.New)?.DeepClone(), write.MaybeRan));
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

    private async Task<VerbReply> UndoReportAsync(RunRecord record, List<UndoStep> steps, List<string> notUndone,
        bool overwrite)
    {
        List<JsonNode?> current = await CurrentAsync(steps).ConfigureAwait(false);
        JsonArray rows = [];
        for (int index = 0; index < steps.Count; index++)
        {
            UndoStep step = steps[index];
            JsonNode? now = current[index];
            JsonObject row = VerbPipeline.Row(step.Id, null, null, now, step.Before, UndoMark);
            row["logic"] = step.Logic;
            string? note = Judge(step, now, overwrite) switch
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
    private async Task<(JsonObject Row, bool Dealt)> RevertAsync(UndoStep step, bool overwrite)
    {
        List<JsonNode?> current = await CurrentAsync([step]).ConfigureAwait(false);
        JsonNode? now = current[0];
        JsonObject row = new() { ["id"] = step.Id, ["logic"] = step.Logic, ["value"] = step.Before?.DeepClone() };
        Verdict verdict = Judge(step, now, overwrite);
        if (verdict != Verdict.Write)
        {
            row["ok"] = verdict == Verdict.Nothing;
            row["now"] = now?.DeepClone();
            row["note"] = verdict switch
            {
                Verdict.Nothing => "nothing to revert",
                Verdict.Unknown => "its value before the run is unknown",
                _ => $"changed since the run (it holds {Run.Shown(now)}, the run left {Run.Shown(step.Last)}); not " +
                     "written without allow: [\"overwrite\"]"
            };
            return (row, verdict != Verdict.Changed);
        }

        VerbReply wrote = await _pipeline.CallAsync(new VerbCall("write", "logic", new JsonObject
        {
            ["writes"] = new JsonArray(new JsonObject
            {
                ["id"] = step.Id,
                ["logic"] = step.Logic,
                ["value"] = step.Before?.DeepClone()
            })
        }, Face.Script) { Gate = PassThroughGate.Instance }, CancellationToken.None).ConfigureAwait(false);
        if (wrote is VerbReply.Failed failed)
        {
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

    // A maybe-ran write is settled by what the target holds: its before value means it never landed.
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

        return Run.SameValue(now, step.Last) || overwrite ? Verdict.Write : Verdict.Changed;
    }

    // What each target holds now, in order (null where the read failed).
    private async Task<List<JsonNode?>> CurrentAsync(List<UndoStep> steps)
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
            Face.Script), CancellationToken.None).ConfigureAwait(false);
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

    /// <summary>One target to revert: its id and logic type, its value before the run, the value the run left.</summary>
    private sealed class UndoStep(string id, string logic, JsonNode? before, JsonNode? last, bool maybeRan)
    {
        internal string Id { get; } = id;

        internal string Logic { get; } = logic;

        internal JsonNode? Before { get; } = before;

        internal JsonNode? Last { get; } = last;

        internal bool MaybeRan { get; } = maybeRan;

        internal string Key => Id + "\n" + Logic;
    }
}
