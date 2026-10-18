using System.Text;
using System.Text.Json.Nodes;

namespace StationGodMCP.Server;

/// <summary>
/// A run's route (hub.md, Routes and Stop): a report run reports every write; a confirmed run acts on seen targets and
/// stops at the first unseen one; after a stop only held restores and logic writes to targets the run already wrote
/// act (CLEANUP), and after a world change nothing acts. And what each reply tells the run: the write log, report and
/// refused rows, builds, warnings, contested values and jobs.
/// </summary>
internal sealed partial class Run
{
    private const string Player = "player";
    private const string LogicKey = "write.logic";

    /// <summary>The gate's decision for one write call of this run (RunGate asks it).</summary>
    internal WriteDecision Decide(VerbCall call, WriteTargets targets, RunCallContext context)
    {
        WhatEntry? what = _pipeline.Catalogue.Verb(call.Verb)?.What(call.What);
        string mark = what?.Write?.UndoMark ?? "[cannot undo]";
        string label = what?.Label ?? call.Verb;
        WriteEntry? entry = null;
        WriteDecision.Stop? stop = null;
        lock (Record.Sync)
        {
            _wrote = true;
            if (_host.Phase == SessionPhase.Ended)
            {
                return RefuseRow(call, targets, context, "the session ended: nothing is sent after session_end");
            }

            if (_stopCode != null)
            {
                if (_stopWorld || !Record.Confirm)
                {
                    return RefuseRow(call, targets, context, _stopWorld
                        ? "the world changed: no cleanup acts, so this write was not sent"
                        : "this run was stopped; this write was not sent");
                }

                if (context.HoldExit ? !HeldOnly(call, targets) : !(what?.Key == LogicKey && AllWritten(call, targets)))
                {
                    return RefuseRow(call, targets, context, context.HoldExit
                        ? "this run was stopped; a held restore acts only for a hold this run entered, with its value"
                        : "this run was stopped; only held restores and writes to targets it already wrote act now");
                }

                entry = Open(call, what, targets, context, WriteGroup.Cleanup);
            }
            else if (!Record.Confirm)
            {
                return new WriteDecision.Report();
            }
            else if (Untargeted(what) is { } fix)
            {
                // A write that names no targets cannot be checked against the seen set, so a run never acts with it.
                stop = RecordRuleStop(StopRows(call, targets, mark), fix, "untargeted_write",
                    $"{label} names no targets a run can check; nothing acted.", context);
            }
            else if (Unseen(what, call, targets))
            {
                stop = RecordRuleStop(StopRows(call, targets, mark), Fix(), "unseen_target",
                    $"{label} names targets not seen; nothing acted.", context);
            }
            else
            {
                entry = Open(call, what, targets, context, context.Group);
            }
        }

        if (stop != null)
        {
            _stopSignal.Cancel();
            _ = KillLaterAsync();
            return stop;
        }

        _journal.Before(Record, entry!);
        return new WriteDecision.Act();
    }

    // Under Sync: a write-rule stop is the run's stop from the moment it is decided, so a write on another thread of the
    // run that the gate decides next meets the stop rule, not the route.
    private WriteDecision.Stop RecordRuleStop(JsonArray rows, string fix, string code, string message, RunCallContext context)
    {
        _stops++;
        _stopCode = code;
        _stopMessage = $"line {context.Line?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}: {message}";
        _stopAt = DateTime.UtcNow;
        _stopDelivered = true;
        Record.StopCode = _stopCode;
        Record.StopMessage = _stopMessage;
        Record.StopRows = (JsonArray)rows.DeepClone();
        Record.StopFix = fix;
        context.RuleStop = true;
        return new WriteDecision.Stop(rows, fix, code, message);
    }

    // The fix for a write that declares no targets (x-target []), or null when it declares some. build blueprint is
    // checked by its at instead (a selector written in the code).
    private static string? Untargeted(WhatEntry? what)
    {
        if (what?.Write is not { } write || write.Targets.Count > 0 || what.Key == "build.blueprint")
        {
            return null;
        }

        return what.Key == "server.command"
            ? "server {what: \"command\"} directly: it reports until confirm (ask the user first: a cheat)"
            : $"run {what.Label} directly; it reports until confirm";
    }

    // Under Sync: after a stop, a hold exit acts only for (id, logic) pairs the run registered, with their registered
    // value; a forged exit, or one for a hold never entered, gets an error row.
    private bool HeldOnly(VerbCall call, WriteTargets targets)
    {
        List<(string Id, string Logic, JsonNode? New)> pairs = LogicPairs(call.Args, targets);
        if (pairs.Count == 0)
        {
            return false;
        }

        foreach ((string id, string logic, JsonNode? next) in pairs)
        {
            int found = _holds.FindIndex(hold => hold.Id == id && hold.Logic == logic);
            if (found < 0 || !SameValue(_holds[found].After, next))
            {
                return false;
            }
        }

        return true;
    }

    // Under Sync.
    private WriteDecision RefuseRow(VerbCall call, WriteTargets targets, RunCallContext context, string message)
    {
        string? first = null;
        foreach (WriteTarget target in targets.Targets)
        {
            if (target.Ids.Count > 0)
            {
                first = target.Ids[0];
                break;
            }
        }

        JsonObject row = new()
        {
            ["index"] = 0,
            ["ok"] = false,
            ["reference_id"] = first,
            ["error"] = new JsonObject { ["code"] = _stopWorld ? "world_changed" : "stopped", ["message"] = message }
        };
        context.Refused = row;
        return new WriteDecision.Refuse(row);
    }

    // ---- seen -------------------------------------------------------------------------------------------------------

    // Under Sync: true when any target is not seen (hub.md, The seen set).
    private bool Unseen(WhatEntry? what, VerbCall call, WriteTargets targets)
    {
        if (what?.Key == "build.blueprint" && call.Args["at"] is { } at && !SelectorWritten(at))
        {
            return true;
        }

        foreach (WriteTarget target in targets.Targets)
        {
            if (!Passes(target))
            {
                return true;
            }
        }

        return false;
    }

    private bool Passes(WriteTarget target)
    {
        if (target.Source == TargetSource.Ref)
        {
            // A ref passes when the code names it (a literal) and the agent was shown it (it is in the store).
            return target.From is { } reference && RefWritten(reference) && _pipeline.Refs.Get(reference) != null;
        }

        foreach (string id in target.Ids)
        {
            if (!IsSeen(id))
            {
                return false;
            }
        }

        return target.Selector == null || SelectorWritten(target.Selector);
    }

    // Under Sync.
    private bool IsSeen(string id) =>
        id == Player || _literals.Contains(id) || _builds.Contains(id) || _reads.Contains(id) || _seen.Contains(id);

    // Under Sync: "r7" written in the code, or a path of it ("r7.things.2").
    private bool RefWritten(string reference)
    {
        foreach (string literal in _literals)
        {
            if (literal == reference || literal.StartsWith(reference + ".", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Seen ids for a hold's targets, which do not pass through the pipeline's gate before they register. An unseen one
    /// is the run's stop from here (recorded under the lock, as the gate does), with these rows and the fix.
    /// </summary>
    private VerbReply.Failed? HoldSeen(List<string> ids, Func<JsonArray> rows, int? line)
    {
        WriteDecision.Stop stop;
        lock (Record.Sync)
        {
            bool all = true;
            foreach (string id in ids)
            {
                all &= IsSeen(id);
            }

            if (all)
            {
                return null;
            }

            stop = RecordRuleStop(rows(), Fix(), "unseen_target", "hold names targets not seen; nothing acted.",
                new RunCallContext(line));
        }

        _stopSignal.Cancel();
        _ = KillLaterAsync();
        return new VerbReply.Failed(stop.Code, stop.Message!, stop.Fix, new JsonObject { ["rows"] = stop.Rows.DeepClone() },
            stopped: true);
    }

    /// <summary>
    /// A selector passes when it is written in the code: every string and number in it equals a literal by value (floats
    /// included), or is an id one of the run's own reads answered. Booleans and nulls are switches, not selectors, and
    /// are not asked about.
    /// </summary>
    private bool SelectorWritten(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject entries:
                foreach ((string _, JsonNode? value) in entries)
                {
                    if (!SelectorWritten(value))
                    {
                        return false;
                    }
                }

                return true;
            case JsonArray list:
                foreach (JsonNode? value in list)
                {
                    if (!SelectorWritten(value))
                    {
                        return false;
                    }
                }

                return true;
            case JsonValue value when JsonNodes.Text(value) is { } text:
                return _literals.Contains(text) || _reads.Contains(text);
            case JsonValue value when JsonNodes.Number(value) is { } number:
                return _literalNumbers.Contains(number);
            default:
                return true;
        }
    }

    // The refused call's targets as report rows: [id, ?, ? -> new] with the undo mark, a selector by its value.
    private static JsonArray StopRows(VerbCall call, WriteTargets targets, string mark)
    {
        JsonArray rows = [];
        foreach (WriteTarget target in targets.Targets)
        {
            foreach (string id in target.Ids)
            {
                rows.Add(VerbPipeline.Row(id, null, null, null, NewValue(call.Args, id), mark));
            }

            if (target.Selector != null && target.Ids.Count == 0)
            {
                JsonObject row = VerbPipeline.Row(null, null, null, null, null, mark);
                row["selector"] = new JsonObject { [target.Path] = target.Selector.DeepClone() };
                row["text"] = $"[{target.Path}: {target.Selector.ToJsonString()}] {mark}";
                rows.Add(row);
            }
        }

        return rows;
    }

    // Under Sync: SAFE's fix texts.
    private string Fix()
    {
        List<string> acted = [];
        foreach (WriteEntry write in Record.Writes)
        {
            if (!write.Acted)
            {
                continue;
            }

            foreach (WrittenTarget target in write.Targets)
            {
                if (target.Acted)
                {
                    acted.Add(target.Id + " " + (write.What ?? write.Verb) + (target.Field != null ? " " + target.Field : ""));
                }
            }
        }

        return acted.Count == 0
            ? "nothing acted: send it again with confirm: true"
            : $"these acted: [{string.Join(", ", acted)}]; undo {Record.Id} first, then send it again";
    }

    // ---- the write log ----------------------------------------------------------------------------------------------

    // Under Sync: logic writes act after a stop only when every (id, logic) they name was written by the run already.
    private bool AllWritten(VerbCall call, WriteTargets targets)
    {
        List<(string Id, string Logic, JsonNode? New)> pairs = LogicPairs(call.Args, targets);
        if (pairs.Count == 0)
        {
            return false;
        }

        foreach ((string id, string logic, JsonNode? _) in pairs)
        {
            if (!_lastWritten.ContainsKey(Key(id, logic)))
            {
                return false;
            }
        }

        return true;
    }

    // (id, logic, value) per target of a logic write, in the order the handler takes them.
    private static List<(string Id, string Logic, JsonNode? New)> LogicPairs(JsonObject args, WriteTargets targets)
    {
        List<(string, string, JsonNode?)> pairs = [];
        if (args["writes"] is JsonArray writes)
        {
            foreach (JsonNode? entry in writes)
            {
                if (IdText(entry?["id"]) is { } id && LogicName(entry!["logic"]) is { } logic)
                {
                    pairs.Add((id, logic, entry["value"]?.DeepClone()));
                }
            }

            return pairs;
        }

        string? named = LogicName(args["logic"]);
        if (named == null)
        {
            return pairs;
        }

        foreach (WriteTarget target in targets.Targets)
        {
            if (target.Role != "acts-on")
            {
                continue;
            }

            foreach (string id in target.Ids)
            {
                pairs.Add((id, named, args["value"]?.DeepClone()));
            }
        }

        return pairs;
    }

    private static string? LogicName(JsonNode? logic) => JsonNodes.Text(logic) ?? IdText(logic);

    // Under Sync: the entry for a write about to be sent, added to the log in the order sent.
    private WriteEntry Open(VerbCall call, WhatEntry? what, WriteTargets targets, RunCallContext context, WriteGroup group)
    {
        List<WrittenTarget> written = [];
        if (what?.Key == LogicKey)
        {
            foreach ((string id, string logic, JsonNode? next) in LogicPairs(call.Args, targets))
            {
                WrittenTarget target = new(id, logic, next);
                if (context.KnownBefore != null && context.KnownBefore.TryGetValue(Key(id, logic), out JsonNode? before))
                {
                    target.Before = before?.DeepClone();
                }

                written.Add(target);
            }
        }
        else
        {
            string? field = FieldOf(what);
            foreach (WriteTarget target in targets.Targets)
            {
                foreach (string id in target.Ids)
                {
                    written.Add(new WrittenTarget(id, field, NewValue(call.Args, id)));
                }

                if (target.Selector != null && target.Ids.Count == 0)
                {
                    written.Add(new WrittenTarget(target.Path + " " + target.Selector.ToJsonString(), null, null));
                }
            }
        }

        _sequence++;
        WriteEntry entry = new(_sequence, call.Verb, call.What, written, what?.Write?.Undo ?? "cannot",
            what?.Write?.UndoMark ?? "[cannot undo]", group, context.Line);
        Record.Writes.Add(entry);
        context.Pending = entry;
        return entry;
    }

    private static string? FieldOf(WhatEntry? what) => what?.Key switch
    {
        "write.label" => "label",
        "write.paint" => "color",
        _ => null
    };

    // The new value a call gives an id: a writes entry's value, else the call's value, label or color.
    private static JsonNode? NewValue(JsonObject args, string id)
    {
        foreach (string list in new[] { "writes", "labels", "items" })
        {
            if (args[list] is JsonArray entries)
            {
                foreach (JsonNode? entry in entries)
                {
                    if (IdText(entry?["id"]) == id)
                    {
                        return (entry!["value"] ?? entry["label"] ?? entry["color"])?.DeepClone();
                    }
                }
            }
        }

        return (args["value"] ?? args["label"] ?? args["color"])?.DeepClone();
    }

    // ---- what a reply tells the run ---------------------------------------------------------------------------------

    private void Observe(string verb, string? what, RunCallContext context, VerbReply reply)
    {
        string label = what == null ? verb : verb + " " + what;
        if (reply is VerbReply.Failed failed)
        {
            if (failed.Stopped && !context.RuleStop && context.Pending == null && context.Refused == null)
            {
                NoteRuleStop(failed, context.Line);
            }

            if (context.Pending is { } unsent)
            {
                lock (Record.Sync)
                {
                    unsent.MaybeRan = failed.MaybeRan;
                    unsent.Acted = failed.MaybeRan;
                    if (!unsent.Acted)
                    {
                        Record.Writes.Remove(unsent);
                        NoteRefused(unsent, failed.ToJson());
                    }
                }

                if (!failed.MaybeRan && (failed.Code is "cancelled" or "session_ended" || _host.Phase == SessionPhase.Ended))
                {
                    _journal.NotSent(Record, unsent);
                }
                else
                {
                    _journal.After(Record, unsent);
                }
            }

            return;
        }

        JsonNode? answer = ((VerbReply.Done)reply).Reply;
        if (context.Refused is { } refused)
        {
            lock (Record.Sync)
            {
                Record.Refused.Add(new ListedRow(label, "", (JsonObject)refused.DeepClone()));
            }

            return;
        }

        WriteEntry? refusedEntry = null;
        lock (Record.Sync)
        {
            Gather(answer);
            if (context.Pending is { } refusedAct && answer is JsonObject refusal && JsonNodes.IsTrue(refusal["report_only"]))
            {
                refusedEntry = refusedAct;
                // The gate let it act, but the game's checks refused it and answered the report: nothing acted, and
                // the script hears it as an error, never as a quiet report row.
                context.ActRefused = ActRefusal(label, refusal);
                refusedAct.Acted = false;
                Record.Writes.Remove(refusedAct);
                NoteRefused(refusedAct, context.ActRefused.ToJson());
                context.Pending = null;
            }
            else if (answer is JsonObject report && JsonNodes.IsTrue(report["report_only"]))
            {
                string mark = _pipeline.Catalogue.Verb(verb)?.What(what)?.Write?.UndoMark ?? "";
                foreach (JsonNode? row in report["targets"] as JsonArray ?? [])
                {
                    if (row is JsonObject listed)
                    {
                        Record.Reported.Add(new ListedRow(label, mark, (JsonObject)listed.DeepClone()));
                    }
                }

                return;
            }

            if (verb is "build" or "swap")
            {
                CollectBuilds(answer, context.Pending);
            }

            if (_pipeline.Catalogue.Verb(verb)?.What(what) is { Write: null } && answer != null)
            {
                // A read the run made itself: the ids it answered are seen by this run's later writes.
                foreach (string id in SeenSet.IdsInJson(answer.ToJsonString()))
                {
                    _reads.Add(id);
                }
            }

            if (verb == "read" && what == "logic")
            {
                NoteContested(answer);
            }
        }

        if (refusedEntry != null)
        {
            _journal.After(Record, refusedEntry);
            return;
        }

        if (context.Pending is { } entry)
        {
            lock (Record.Sync)
            {
                Complete(entry, answer);
                if (entry.Acted)
                {
                    _lastWrite = entry.Label;
                }
                else
                {
                    Record.Writes.Remove(entry);
                }
            }

            _journal.After(Record, entry);
        }
    }

    // A reply that arrived after this call's caller gave up (a kill, a closing session): what its rows say fills the
    // write log's targets, so an undo can still place what the write changed. A write whose reply never comes keeps
    // the before value unknown, and its undo says so. Called on the link's reader, before the call settles.
    private void LateFill(RunCallContext context, JsonNode? answer)
    {
        if (context.Pending is not { } entry)
        {
            return;
        }

        List<JsonObject> rows = BulkWrites.Rows(answer);
        lock (Record.Sync)
        {
            for (int index = 0; index < entry.Targets.Count; index++)
            {
                WrittenTarget target = entry.Targets[index];
                if (RowFor(rows, index, target.Id) is not { } row)
                {
                    continue;
                }

                if (row["previous_value"] is { } previous)
                {
                    target.Before = previous.DeepClone();
                }

                if (row["current_value"] is { } current)
                {
                    target.After = current.DeepClone();
                }
            }
        }
    }

    // Under Sync: each target's before (previous_value) and after (current_value), and whether its row acted.
    private void Complete(WriteEntry entry, JsonNode? answer)
    {
        List<JsonObject> rows = BulkWrites.Rows(answer);
        bool any = false;
        for (int index = 0; index < entry.Targets.Count; index++)
        {
            WrittenTarget target = entry.Targets[index];
            JsonObject? row = RowFor(rows, index, target.Id);
            if (row != null && row["ok"] is { } ok && !JsonNodes.IsTrue(ok))
            {
                target.Acted = false;
                Record.Refused.Add(new ListedRow(entry.Label, entry.UndoMark, new JsonObject
                {
                    ["reference_id"] = target.Id,
                    ["error"] = row["error"]?.DeepClone() ?? new JsonObject { ["message"] = "the game refused it" }
                }));
                continue;
            }

            if (row?["previous_value"] is { } previous)
            {
                target.Before = previous.DeepClone();
            }

            if (row?["current_value"] is { } current)
            {
                target.After = current.DeepClone();
            }

            any = true;
            if (target.Field != null && entry.What == "logic")
            {
                string key = Key(target.Id, target.Field);
                if (!_lastWritten.ContainsKey(key))
                {
                    _writtenOrder.Add(key);
                }

                _lastWritten[key] = target;
            }
        }

        entry.Acted = any || entry.Targets.Count == 0;
    }

    // Under Sync: a write the game refused, one row per target, for the footer's refused group.
    private void NoteRefused(WriteEntry write, JsonObject error)
    {
        foreach (WrittenTarget target in write.Targets)
        {
            Record.Refused.Add(new ListedRow(write.Label, write.UndoMark,
                new JsonObject { ["reference_id"] = target.Id, ["error"] = error.DeepClone() }));
        }
    }

    // The reply row of a target: the one at its index when that names it, else the first that names it.
    private static JsonObject? RowFor(List<JsonObject> rows, int index, string id)
    {
        if (index < rows.Count && (rows[index]["reference_id"] == null || IdText(rows[index]["reference_id"]) == id))
        {
            return rows[index];
        }

        foreach (JsonObject row in rows)
        {
            if (IdText(row["reference_id"]) == id)
            {
                return row;
            }
        }

        return null;
    }

    // The error a refused act answers: the report's first problem (its code and message), with the whole report as data.
    private static VerbReply.Failed ActRefusal(string label, JsonObject report)
    {
        JsonObject? problem = (report["problems"] as JsonArray)?.Count > 0 ? report["problems"]![0] as JsonObject : null;
        string code = JsonNodes.Text(problem?["code"]) ?? "not_acted";
        string why = JsonNodes.Text(problem?["message"]) ?? "the game answered its report instead of acting";
        return new VerbReply.Failed(code, $"{label} did not act: {why}", "fix what the problem names and run it again",
            report.DeepClone());
    }

    // Under Sync: what a build or swap made is the run's own and seen by it.
    private void CollectBuilds(JsonNode? answer, WriteEntry? entry)
    {
        if (answer is not JsonObject reply)
        {
            return;
        }

        foreach (string key in new[] { "placed", "created_ids", "created" })
        {
            if (reply[key] is not JsonArray made)
            {
                continue;
            }

            foreach (JsonNode? id in made)
            {
                if (IdText(id) is { } text)
                {
                    _builds.Add(text);
                    entry?.Targets.Add(new WrittenTarget(text, key, null));
                }
            }
        }
    }

    // Under Sync: a value the run wrote that reads back otherwise now.
    private void NoteContested(JsonNode? answer)
    {
        foreach (JsonObject row in BulkWrites.Rows(answer))
        {
            if (IdText(row["reference_id"]) is not { } id || row["value"] is not { } value)
            {
                continue;
            }

            string? logic = LogicName(row["logic_type"]?["name"]) ?? LogicName(row["logic_type"]);
            if (logic == null || !_lastWritten.TryGetValue(Key(id, logic), out WrittenTarget? wrote))
            {
                continue;
            }

            JsonNode? expected = wrote.After ?? wrote.New;
            if (!SameValue(expected, value) && _contested.Add(Key(id, logic)))
            {
                Record.Contested.Add($"{id} {logic}: wrote {Shown(expected)}, read {Shown(value)}");
            }
        }
    }

    // Under Sync: warnings (driven_by, lint), lifted guards (passed) and the jobs the run started: still running
    // (_pendingJobs) and every one for its life (_startedJobs), from a reply and its rows.
    private void Gather(JsonNode? answer)
    {
        if (answer is not JsonObject reply)
        {
            return;
        }

        GatherOne(reply);
        foreach (JsonNode? row in reply["results"] as JsonArray ?? [])
        {
            if (row is JsonObject entry)
            {
                GatherOne(entry);
                if (entry["payload"] is JsonObject payload)
                {
                    GatherOne(payload);
                }
            }
        }
    }

    private void GatherOne(JsonObject reply)
    {
        foreach (JsonNode? warning in reply["warnings"] as JsonArray ?? [])
        {
            string text = JsonNodes.Text(warning) ?? JsonNodes.Text(warning?["message"]) ?? warning?.ToJsonString() ?? "";
            AddOnce(Record.Warnings, text);
        }

        if (reply["driven_by"] is { } driven)
        {
            AddOnce(Record.Warnings, "driven_by " + (JsonNodes.Text(driven) ?? driven.ToJsonString()));
        }

        foreach (JsonNode? passed in reply["passed"] as JsonArray ?? [])
        {
            AddOnce(Record.Passed, JsonNodes.Text(passed) ?? passed?.ToJsonString() ?? "");
        }

        if (JsonNodes.Text(reply["job_id"]) is { } job && JsonNodes.IsTrue(reply["running"]))
        {
            _startedJobs.Add(job);
            if (!_pendingJobs.Contains(job))
            {
                _pendingJobs.Add(job);
            }
        }
    }

    private static void AddOnce(List<string> list, string text)
    {
        if (text.Length > 0 && !list.Contains(text))
        {
            list.Add(text);
        }
    }

    /// <summary>The compact item a footer shows for a target: "122643 Open=1", or the id alone.</summary>
    internal static string Item(WrittenTarget target)
    {
        StringBuilder item = new(target.Id);
        if (target.Field != null && target.New != null)
        {
            item.Append(' ').Append(target.Field).Append('=').Append(Shown(target.New));
        }

        return item.ToString();
    }
}
