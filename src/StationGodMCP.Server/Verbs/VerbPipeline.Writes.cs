using System.Text;
using System.Text.Json.Nodes;
using StationGodMCP.Client;

namespace StationGodMCP.Server;

/// <summary>
/// Writes: the one place the gate is asked (with the targets the x-target paths, refs and spreads resolve to), the act
/// and report sends (confirm as the gate decided: true acts, false reports; a what with no confirm option is sent
/// without one), and the bulk writes (write-N).
/// </summary>
public sealed partial class VerbPipeline
{
    private const string ReplayKey = "__replay";
    private const string ReplayNoteKey = "__replay_note";

    private static readonly string[] BulkLists = ["writes", "labels", "items", "moves"];

    /// <summary>The gate's decision on a write the hub answers itself (undo of a run), by the same path as any write.</summary>
    internal WriteDecision DecideWrite(VerbCall call)
    {
        WhatEntry what = _catalogue.Verb(call.Verb)!.What(call.What)!;
        return GateFor(call).Decide(call, WriteTargetPaths.Resolve(what, call.Args));
    }

    private IWriteGate GateFor(VerbCall call) =>
        call.Gate ?? _gate ?? (call.Face == Face.Script ? ReportingGate.Instance : _directGate);

    private async Task<VerbReply> WriteAsync(VerbCall call, WhatEntry what, JsonObject options, List<RefUse> refs,
        CancellationToken cancellation)
    {
        if (RunsWrite(what, options) is { } runs)
        {
            return runs;
        }

        WriteTargets targets = WriteTargetPaths.Resolve(what, options, refs);
        switch (GateFor(call).Decide(call, targets))
        {
            case WriteDecision.Act:
                return await ActAsync(call, what, options, refs.Count > 0, cancellation).ConfigureAwait(false);
            case WriteDecision.Report:
                VerbReply report = await ReportAsync(call, what, options, targets, cancellation).ConfigureAwait(false);
                Replayable(report, what, options, targets);
                return report;
            case WriteDecision.Refuse refuse:
                return Refused(refuse, targets);
            case WriteDecision.Stop stop:
                return new VerbReply.Failed(stop.Code, stop.Message ?? $"{what.Label} names targets not seen; nothing acted.",
                    stop.Fix, new JsonObject { ["rows"] = stop.Rows.DeepClone() }, stopped: true);
            default:
                throw new InvalidOperationException("A write decision is act, report, refuse or stop.");
        }
    }

    // server stop and undo of a script run are the runs': refused below when no run host is here.
    private static VerbReply? RunsWrite(WhatEntry what, JsonObject options)
    {
        if (what.Key == "server.stop")
        {
            return new VerbReply.Failed(VerbFailure.NotAvailable, $"server stop {RunlessHub}.");
        }

        if (what.Key == "undo" && JsonNodes.Text(options["id"])?.Trim() is { } id &&
            id.StartsWith("script-", StringComparison.OrdinalIgnoreCase))
        {
            return new VerbReply.Failed(VerbFailure.NotAvailable, $"undo of a script run {RunlessHub}.");
        }

        return null;
    }

    // A refused write answers a row per target and does not raise: the rest of a run's finally goes on.
    private static VerbReply Refused(WriteDecision.Refuse refuse, WriteTargets targets)
    {
        List<string> ids = ActsOnIds(targets);
        JsonArray rows = [];
        if (ids.Count == 0)
        {
            rows.Add(refuse.Row.DeepClone());
        }

        for (int index = 0; index < ids.Count; index++)
        {
            JsonObject row = refuse.Row.DeepClone() as JsonObject ?? new JsonObject();
            row["index"] = index;
            row["ok"] = false;
            row["reference_id"] = ids[index];
            rows.Add(row);
        }

        return new VerbReply.Done(new JsonObject
        {
            ["results"] = rows,
            ["count"] = rows.Count,
            ["success_count"] = 0,
            ["error_count"] = rows.Count
        });
    }

    // ---- act -------------------------------------------------------------------------------------------------------

    private async Task<VerbReply> ActAsync(VerbCall call, WhatEntry what, JsonObject options, bool fromRef,
        CancellationToken cancellation)
    {
        VerbReply reply = await SentWriteAsync(call, what, options, true, cancellation).ConfigureAwait(false);
        if (reply is not VerbReply.Done done)
        {
            return reply;
        }

        if (call.Face != Face.Script && IsBulk(what, options, fromRef) && done.Reply is JsonObject acted)
        {
            BulkWrite write = _bulk.Mint(what, acted);
            acted["job_id"] = write.Id;
        }

        return done;
    }

    // A write sent to the mod (acting: confirm true; else a report), or the hub's own for undo of a write-N.
    private async Task<VerbReply> SentWriteAsync(VerbCall call, WhatEntry what, JsonObject options, bool acting,
        CancellationToken cancellation)
    {
        if (what.Key == "undo")
        {
            if (options["id"] is JsonArray ids && HoldsBulk(ids))
            {
                return await RowsAsync(call, what, options, ids, acting, cancellation).ConfigureAwait(false);
            }

            if (JsonNodes.Text(options["id"])?.Trim() is { } id && BulkWrites.IsBulkId(id))
            {
                return await OwnUndoAsync(call, id, options, acting, cancellation).ConfigureAwait(false);
            }
        }

        return await ModAsync(call, what, Confirmed(what, options, acting), cancellation).ConfigureAwait(false);
    }

    // A bulk write: a write or move that acted on a list, a ref or a list of entries (bulk jobs are other verbs' jobs).
    private static bool IsBulk(WhatEntry what, JsonObject options, bool fromRef)
    {
        if (what.Verb is not ("write" or "move"))
        {
            return false;
        }

        if (fromRef || options["id"] is JsonArray)
        {
            return true;
        }

        foreach (string list in BulkLists)
        {
            if (options[list] is JsonArray)
            {
                return true;
            }
        }

        return false;
    }

    // ---- reports ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The report a write gives instead of acting: the mod's own (the call sent with confirm: false), or, for a what
    /// with no confirm (nothing it could be sent with reports), a row per target from the hub, nothing sent. Either way
    /// it is report_only, and a ref's targets say where they came from (targets_from).
    /// </summary>
    private async Task<VerbReply> ReportAsync(VerbCall call, WhatEntry what, JsonObject options, WriteTargets targets,
        CancellationToken cancellation)
    {
        JsonObject report;
        if (what.Options.ContainsKey("confirm"))
        {
            VerbReply sent = await SentWriteAsync(call, what, options, false, cancellation).ConfigureAwait(false);
            if (sent is not VerbReply.Done done)
            {
                return sent;
            }

            report = done.Reply as JsonObject ?? new JsonObject { ["reply"] = done.Reply?.DeepClone() };
        }
        else
        {
            report = new JsonObject { ["targets"] = TargetRows(what, options, targets) };
        }

        report["report_only"] = true;
        foreach (WriteTarget target in targets.Targets)
        {
            if (target.Source == TargetSource.Ref && _refs.Get(target.From!) is { } stored)
            {
                int whole = stored.MainList != null && stored.Whole?[stored.MainList] is JsonArray all ? all.Count : 0;
                report["targets_from"] = new JsonObject
                {
                    ["from"] = target.From,
                    ["ids"] = Strings(target.Ids),
                    ["more_in_source"] = whole > target.Ids.Count
                };
            }
        }

        return new VerbReply.Done(report);
    }

    /// <summary>
    /// What a repeat with confirm of this report acts on, kept in the report until its ref is minted: the options with
    /// refs as their ids; a spread is sent again, and the repeat says the selection may differ.
    /// </summary>
    private static void Replayable(VerbReply report, WhatEntry what, JsonObject options, WriteTargets targets)
    {
        if (report is not VerbReply.Done { Reply: JsonObject shown })
        {
            return;
        }

        JsonObject replay = (JsonObject)options.DeepClone();
        replay.Remove("confirm");
        shown[ReplayKey] = replay;
        foreach (WriteTarget target in targets.Targets)
        {
            if (target.Source == TargetSource.Selector)
            {
                shown[ReplayNoteKey] = $"{what.Label} was sent again with confirm: its selection may differ from the report's.";
                return;
            }
        }
    }

    // The hub's report rows: one per acts-on id, and each spread.
    private static JsonArray TargetRows(WhatEntry what, JsonObject options, WriteTargets targets)
    {
        JsonArray rows = [];
        string mark = RowMark(what, options, null);
        foreach (string id in ActsOnIds(targets))
        {
            rows.Add(Row(id, null, null, null, null, mark));
        }

        foreach (WriteTarget target in targets.Targets)
        {
            if (target.Source == TargetSource.Selector && target.Ids.Count == 0)
            {
                JsonObject spread = Row(null, null, null, null, null, mark);
                spread["selector"] = new JsonObject { [target.Path] = target.Selector?.DeepClone() };
                spread["text"] = $"[{target.Path}: {target.Selector?.ToJsonString()}] {mark}";
                rows.Add(spread);
            }
        }

        return rows;
    }

    /// <summary>
    /// A row's own undo mark (not the what's whole legend): a logic write holds a setting ([undo]) unless its type fires
    /// an action; a chip's source and run controls cannot be undone, its pins can; a move by force cannot.
    /// </summary>
    internal static string RowMark(WhatEntry what, JsonObject options, JsonNode? logic)
    {
        switch (what.Key)
        {
            case "write.logic":
                return BulkWrites.IsAction(logic) ? "[cannot undo: action]" : "[undo]";
            case "write.chip":
            {
                List<string> marks = [];
                if (options["code"] != null || options["code_file"] != null)
                {
                    marks.Add("[cannot undo: chip source]");
                }

                if (options["pins"] != null)
                {
                    marks.Add("[undo]");
                }

                if (options["run"] != null)
                {
                    marks.Add("[cannot undo: run control]");
                }

                return marks.Count == 0 ? "[undo]" : string.Join(" ", marks);
            }
            case "move.item":
                return options["source"] != null || JsonNodes.IsTrue(options["force"]) ? "[cannot undo: store move]" : "[undo]";
            default:
                string mark = what.Write!.UndoMark;
                return mark.Contains(';') ? (what.Write.Undo == "undo" ? "[undo]" : "[cannot undo]") : mark;
        }
    }

    /// <summary>A report row: [id or cell, name, current -> new] with the undo mark, and that line as text.</summary>
    internal static JsonObject Row(string? id, JsonNode? cell, string? name, JsonNode? current, JsonNode? next, string mark)
    {
        JsonObject row = new();
        if (id != null)
        {
            row["id"] = id;
        }

        if (cell != null)
        {
            row["cell"] = cell.DeepClone();
        }

        row["name"] = name;
        row["current"] = current?.DeepClone();
        row["new"] = next?.DeepClone();
        row["undo"] = mark;
        StringBuilder text = new();
        text.Append('[').Append(id ?? cell?.ToJsonString() ?? "?").Append(", ").Append(name ?? "?").Append(", ")
            .Append(Shown(current)).Append(" -> ").Append(Shown(next)).Append("] ").Append(mark);
        row["text"] = text.ToString();
        return row;
    }

    private static string Shown(JsonNode? value) =>
        value == null ? "?" : JsonNodes.Text(value) ?? value.ToJsonString();

    private static List<string> ActsOnIds(WriteTargets targets)
    {
        List<string> ids = [];
        foreach (WriteTarget target in targets.Targets)
        {
            if (target.Role != "acts-on" || target.Source == TargetSource.Selector)
            {
                continue;
            }

            foreach (string id in target.Ids)
            {
                if (!ids.Contains(id))
                {
                    ids.Add(id);
                }
            }
        }

        return ids;
    }

    private static JsonArray Strings(IReadOnlyList<string> values)
    {
        JsonArray list = [];
        foreach (string value in values)
        {
            list.Add(value);
        }

        return list;
    }

    // ---- the hub's own undo: write-N --------------------------------------------------------------------------------

    private async Task<VerbReply> OwnUndoAsync(VerbCall call, string id, JsonObject options, bool acting,
        CancellationToken cancellation) =>
        _bulk.Get(id) is { } write
            ? await UndoBulkAsync(call, write, options, acting, cancellation).ConfigureAwait(false)
            : NoBulk(id);

    private static VerbReply.Failed NoBulk(string id) =>
        new("job_not_found", $"No bulk write {id} in this session (bulk writes last until the MCP server stops).");

    /// <summary>
    /// undo write-N. A report lists every step with its own mark (cannot-undo ones too). Acting writes the steps back
    /// newest first, from from_step, skipping steps already done; before each it reads the target again and leaves one
    /// that changed since the write (the user, a chip, a run; its row carries code target_drifted) unless
    /// allow: ["overwrite"]; a refused step stops the rest. One undo runs at a time; a write with nothing left to undo
    /// says so.
    /// </summary>
    private async Task<VerbReply> UndoBulkAsync(VerbCall call, BulkWrite write, JsonObject options, bool acting,
        CancellationToken cancellation)
    {
        if (!acting)
        {
            JsonArray listed = [];
            foreach (BulkStep step in write.Steps)
            {
                listed.Add(step.RowFor(write.Done.Contains(step.Number)));
            }

            JsonObject report = new()
            {
                ["report_only"] = true,
                ["job_id"] = write.Id,
                ["undoable"] = write.Undoable,
                ["targets"] = listed
            };
            if (write.Undoable == 0)
            {
                report["note"] = $"{write.Id} cannot be undone: none of its steps can.";
            }

            return new VerbReply.Done(report);
        }

        if (write.Undoable == 0)
        {
            return new VerbReply.Failed("cannot_undo", $"{write.Id} cannot be undone: none of its steps can.");
        }

        if (write.Undone || !write.BeginUndo())
        {
            return new VerbReply.Failed("already_undone",
                write.Undone ? $"{write.Id} is undone already." : $"{write.Id} is being undone by another call.");
        }

        try
        {
            return await UndoStepsAsync(call, write, options, cancellation).ConfigureAwait(false);
        }
        finally
        {
            write.EndUndo();
        }
    }

    private async Task<VerbReply> UndoStepsAsync(VerbCall call, BulkWrite write, JsonObject options, CancellationToken cancellation)
    {
        int from = (int)(JsonNodes.Number(options["from_step"]) ?? 1);
        bool overwrite = JsonNodes.Holds(options["allow"], "overwrite");
        JsonArray rows = [];
        JsonArray notRun = [];
        JsonArray drifted = [];
        int done = 0;
        bool stopped = false;
        foreach (BulkStep step in write.Steps)
        {
            if (step.Back == null || step.Number < from || write.Done.Contains(step.Number))
            {
                continue;
            }

            if (stopped)
            {
                notRun.Add(step.Number);
                continue;
            }

            JsonObject row = step.RowFor(false);
            if (!overwrite && step.Check != null &&
                await DriftAsync(call, step.Check, cancellation).ConfigureAwait(false) is { } now)
            {
                row["ok"] = false;
                row["drifted"] = true;
                row["code"] = DriftedCode;
                row["current"] = now.DeepClone();
                row["note"] = "changed since the write; left as it is (allow: [\"overwrite\"] writes it back anyway)";
                drifted.Add(step.Number);
                rows.Add(row);
                continue;
            }

            GameReply reply = await SendAsync(write.What, Confirmed(write.What, step.Back, true), call.Stream, null, cancellation)
                .ConfigureAwait(false);
            row["ok"] = reply is GameReply.Ok;
            if (reply is GameReply.Ok)
            {
                done++;
                write.Done.Add(step.Number);
            }
            else
            {
                row["error"] = Failure(reply).ToJson();
                stopped = true;
            }

            rows.Add(row);
        }

        JsonObject answer = new()
        {
            ["job_id"] = write.Id,
            ["undone"] = done,
            ["results"] = rows,
            ["count"] = rows.Count,
            ["status"] = write.Undone ? "undone" : "partly_undone"
        };
        if (notRun.Count > 0)
        {
            answer["not_run"] = notRun;
        }

        if (drifted.Count > 0)
        {
            answer["drifted"] = drifted;
        }

        return new VerbReply.Done(answer);
    }

    /// <summary>
    /// What the target holds now when it is not what the write left; null when it is the same (or cannot be read): a
    /// logic value by read logic, a custom name by inspect health.
    /// </summary>
    private async Task<JsonNode?> DriftAsync(VerbCall call, BulkCheck check, CancellationToken cancellation)
    {
        JsonNode? now;
        if (check.Logic != null)
        {
            WhatEntry read = _catalogue.Verb("read")!.What("logic")!;
            JsonObject reads = new()
            {
                ["reads"] = new JsonArray(new JsonObject { ["id"] = check.Id, ["logic"] = check.Logic.DeepClone() })
            };
            if (await SendAsync(read, reads, call.Stream, null, cancellation).ConfigureAwait(false) is not GameReply.Ok ok)
            {
                return null;
            }

            now = JsonNodes.FromElement(ok.Result)?["results"]?[0]?["value"];
        }
        else
        {
            WhatEntry health = _catalogue.Verb("inspect")!.What("health")!;
            JsonObject asked = new() { ["id"] = new JsonArray(JsonValue.Create(check.Id)) };
            if (await SendAsync(health, asked, call.Stream, null, cancellation).ConfigureAwait(false) is not GameReply.Ok ok ||
                CustomName(JsonNodes.FromElement(ok.Result), check.Id) is not { } name)
            {
                return null;
            }

            now = JsonValue.Create(name);
        }

        bool same = JsonNodes.Number(now) is { } number && JsonNodes.Number(check.Left) is { } left
            ? Math.Abs(number - left) < 1e-6
            : JsonNode.DeepEquals(now, check.Left) ||
              (JsonNodes.Text(now) ?? string.Empty) == (JsonNodes.Text(check.Left) ?? string.Empty);
        return same ? null : now ?? JsonValue.Create("?");
    }

    // The custom name inspect health gives the id ("" for none); null when the reply names no such thing.
    private static string? CustomName(JsonNode? reply, string id)
    {
        foreach (JsonObject row in BulkWrites.Rows(reply))
        {
            JsonNode? thing = row["payload"] ?? row;
            if (JsonNodes.Text(thing?["reference_id"] ?? row["reference_id"]) == id)
            {
                return JsonNodes.Text(thing?["custom_name"]) ?? string.Empty;
            }
        }

        return null;
    }
}
