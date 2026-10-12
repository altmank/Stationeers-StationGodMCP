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
        WriteEntry entry;
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

                if (!context.HoldExit && !(what?.Key == LogicKey && AllWritten(call, targets)))
                {
                    return RefuseRow(call, targets, context,
                        "this run was stopped; only held restores and writes to targets it already wrote act now");
                }

                entry = Open(call, what, targets, context, WriteGroup.Cleanup);
            }
            else if (!Record.Confirm)
            {
                return new WriteDecision.Report();
            }
            else if (Unseen(targets))
            {
                return new WriteDecision.Stop(StopRows(call, targets, mark), Fix());
            }
            else
            {
                entry = Open(call, what, targets, context, context.Group);
            }
        }

        _journal.Before(Record, entry);
        return new WriteDecision.Act();
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
    private bool Unseen(WriteTargets targets)
    {
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
            // A ref names the ids its reply showed: the reply the agent saw.
            return target.From != null && _pipeline.Refs.Get(target.From) != null;
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
        id == Player || _literals.Contains(id) || _builds.Contains(id) || _seen.Contains(id);

    /// <summary>Seen ids for a hold's targets, which do not pass through the pipeline's gate before they register.</summary>
    private bool AllSeen(List<string> ids)
    {
        lock (Record.Sync)
        {
            foreach (string id in ids)
            {
                if (!IsSeen(id))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// A selector passes when it is written in the code: a string or integer equal to a literal; an object or list when
    /// each of its string and integer values is one (numbers with a fraction and booleans are not literals and do not
    /// name things, so they are not asked about).
    /// </summary>
    private bool SelectorWritten(JsonNode selector)
    {
        int asked = 0;
        return LeavesWritten(selector, ref asked) && asked > 0;
    }

    private bool LeavesWritten(JsonNode? node, ref int asked)
    {
        switch (node)
        {
            case JsonObject entries:
                foreach ((string _, JsonNode? value) in entries)
                {
                    if (!LeavesWritten(value, ref asked))
                    {
                        return false;
                    }
                }

                return true;
            case JsonArray list:
                foreach (JsonNode? value in list)
                {
                    if (!LeavesWritten(value, ref asked))
                    {
                        return false;
                    }
                }

                return true;
            case JsonValue value when IdText(value) is { } text:
                asked++;
                return _literals.Contains(text);
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
                if (context.KnownBefore != null && context.KnownBefore.TryGetValue(id, out JsonNode? before))
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
            if (failed.Stopped && context.Pending == null && context.Refused == null)
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

        lock (Record.Sync)
        {
            Gather(answer);
            if (answer is JsonObject report && JsonNodes.IsTrue(report["report_only"]))
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

            if (verb == "read" && what == "logic")
            {
                NoteContested(answer);
            }
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

    // Under Sync: warnings (driven_by, lint), lifted guards (passed) and jobs still running, from a reply and its rows.
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

        if (JsonNodes.Text(reply["job_id"]) is { } job && JsonNodes.IsTrue(reply["running"]) && !_pendingJobs.Contains(job))
        {
            _pendingJobs.Add(job);
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
