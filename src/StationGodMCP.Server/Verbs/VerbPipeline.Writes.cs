using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using StationGodMCP.Client;

namespace StationGodMCP.Server;

/// <summary>
/// Writes: the one place the gate is asked (with the targets the x-target paths and spreads resolve to), the report
/// forms by report class, the act path with move item's names and undo marks, and the bulk writes (write-N).
/// </summary>
public sealed partial class VerbPipeline
{
    private static readonly string[] BulkLists = ["writes", "labels", "items", "moves"];

    private IWriteGate GateFor(VerbCall call) =>
        call.Gate ?? _gate ?? (call.Face == Face.Script ? ReportingGate.Instance : _directGate);

    private async Task<VerbReply> WriteAsync(VerbCall call, WhatEntry what, JsonObject options, JsonArray? ids,
        Dictionary<string, string> refs, CancellationToken cancellation)
    {
        // A call the handlers cannot take is refused before the gate: a report of it would promise what cannot act.
        JsonObject first = (JsonObject)options.DeepClone();
        if (ids != null && what.ListForm == "split")
        {
            first["id"] = ids.Count > 0 ? ids[0]?.DeepClone() : null;
        }

        if (_map.Translate(what, first) is Translation.Answered { Reply: VerbReply.Failed cannot })
        {
            return cannot;
        }

        WriteTargets targets = WriteTargetPaths.Resolve(what, options, refs);
        switch (GateFor(call).Decide(call, targets))
        {
            case WriteDecision.Act:
                return await ActAsync(call, what, options, ids, targets, refs.Count > 0, cancellation).ConfigureAwait(false);
            case WriteDecision.Report:
                return await ReportAsync(call, what, options, ids, targets, cancellation).ConfigureAwait(false);
            case WriteDecision.Refuse refuse:
                // A refused write answers its row and does not raise: the rest of a run's finally goes on.
                return new VerbReply.Done(new JsonObject
                {
                    ["results"] = new JsonArray(refuse.Row.DeepClone()),
                    ["count"] = 1,
                    ["success_count"] = 0,
                    ["error_count"] = 1
                });
            case WriteDecision.Stop stop:
                return new VerbReply.Failed("unseen_target", $"{what.Label} names targets not seen; nothing acted.",
                    stop.Fix, new JsonObject { ["rows"] = stop.Rows.DeepClone() }, stopped: true);
            default:
                throw new InvalidOperationException("A write decision is act, report, refuse or stop.");
        }
    }

    // ---- act -------------------------------------------------------------------------------------------------------

    private async Task<VerbReply> ActAsync(VerbCall call, WhatEntry what, JsonObject options, JsonArray? ids,
        WriteTargets targets, bool fromRef, CancellationToken cancellation)
    {
        bool slotMove = what.Key == "move.item" && options["source"] == null;
        Dictionary<string, (string? Display, string? Custom)>? names = slotMove
            ? await NamesAsync(what, ActsOnIds(targets), call.Stream, cancellation).ConfigureAwait(false)
            : null;
        VerbReply reply = ids != null && what.ListForm == "split"
            ? await SplitAsync(call, what, options, ids, true, cancellation).ConfigureAwait(false)
            : await ExecuteAsync(_map.Translate(what, options), call, what, options, true, cancellation).ConfigureAwait(false);
        if (reply is not VerbReply.Done done)
        {
            return reply;
        }

        if (names != null)
        {
            MarkMoves(done.Reply, names, JsonNodes.IsTrue(options["force"]));
        }

        if (call.Face != Face.Script && IsBulk(what, options, ids, fromRef) && done.Reply is JsonObject acted)
        {
            BulkWrite write = _bulk.Mint(what, acted);
            acted["job_id"] = write.Id;
        }

        return done;
    }

    // A bulk write: a write or move that acted on a list, a ref or a list of entries (bulk jobs are other verbs' jobs).
    private static bool IsBulk(WhatEntry what, JsonObject options, JsonArray? ids, bool fromRef)
    {
        if (what.Verb is not ("write" or "move") || what.ListForm == "one")
        {
            return false;
        }

        if (ids != null || fromRef)
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

    // Slot moves act on typed ids: each row says what moved (display_name) and whether it can be undone.
    private static void MarkMoves(JsonNode? reply, Dictionary<string, (string? Display, string? Custom)> names, bool force)
    {
        foreach (JsonObject row in BulkWrites.Rows(reply))
        {
            if (JsonNodes.Text(row["reference_id"]) is { } id && names.TryGetValue(id, out (string? Display, string? Custom) name))
            {
                row["display_name"] = name.Display;
            }

            if (row["ok"] is { } ok && !JsonNodes.IsTrue(ok))
            {
                continue;
            }

            row["undo"] = force ? "[cannot undo: store move]"
                : JsonNodes.Text(row["merged_into"]) is { } into ? $"[cannot undo: merged into {into}]"
                : "[undo]";
        }
    }

    // ---- reports ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The report a write gives instead of acting, one shape on every face: {report_only: true, targets: [{id or cell,
    /// name, current, new, undo, text}], placed: [] for a build}. Report class dry_run and mod send the handler's own
    /// report (dry_run: true) and keep its keys; hub reads the current values itself; none sends nothing.
    /// </summary>
    private async Task<VerbReply> ReportAsync(VerbCall call, WhatEntry what, JsonObject options, JsonArray? ids,
        WriteTargets targets, CancellationToken cancellation)
    {
        JsonObject report;
        JsonArray rows;
        if (what.Key == "undo" && string.Equals(JsonNodes.Text(options["id"])?.Trim(), "paste", StringComparison.OrdinalIgnoreCase))
        {
            return await PasteUndoReportAsync(call, what, cancellation).ConfigureAwait(false);
        }

        switch (what.Write!.Report)
        {
            case "hub":
                report = new JsonObject();
                rows = await HubRowsAsync(call, what, options, targets, cancellation).ConfigureAwait(false);
                break;
            case "none":
                report = new JsonObject();
                rows = await TargetRowsAsync(call, what, options, targets, cancellation).ConfigureAwait(false);
                break;
            default:
            {
                VerbReply sent = ids != null && what.ListForm == "split"
                    ? await SplitAsync(call, what, options, ids, false, cancellation).ConfigureAwait(false)
                    : await ExecuteAsync(_map.Translate(what, options), call, what, options, false, cancellation)
                        .ConfigureAwait(false);
                if (sent is not VerbReply.Done done)
                {
                    return sent;
                }

                report = done.Reply is JsonObject own ? own : new JsonObject { ["reply"] = done.Reply?.DeepClone() };
                rows = await TargetRowsAsync(call, what, options, targets, cancellation).ConfigureAwait(false);
                break;
            }
        }

        report["report_only"] = true;
        report["targets"] = rows;
        if (what.Verb == "build")
        {
            report["placed"] = new JsonArray();
        }

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

    // The mod's paste undo acts at once and has no report: the report is the last paste's status.
    private async Task<VerbReply> PasteUndoReportAsync(VerbCall call, WhatEntry what, CancellationToken cancellation)
    {
        HandlerCall status = new("paste_blueprint", new JsonObject { ["status"] = true },
            _map.Rename.For(what, "paste_blueprint"));
        GameReply reply = await SendAsync(status, call.Stream, null, cancellation).ConfigureAwait(false);
        if (reply is not GameReply.Ok ok)
        {
            return Failure(reply, status, what);
        }

        JsonNode? paste = JsonNodes.FromElement(ok.Result);
        string file = JsonNodes.Text(paste?["file"]) ?? "?";
        string entries = paste?["entries"] is { } count ? count.ToJsonString() : "?";
        JsonObject row = Row("paste", null, file, null, null, what.Write!.UndoMark);
        row["text"] = $"would undo the newest paste ({file}, {entries} entries)";
        return new VerbReply.Done(new JsonObject
        {
            ["report_only"] = true,
            ["targets"] = new JsonArray(row),
            ["paste"] = paste?.DeepClone()
        });
    }

    // The rows of dry_run, mod and none reports: one per acts-on id (named), a build's placements by cell, each spread.
    private async Task<JsonArray> TargetRowsAsync(VerbCall call, WhatEntry what, JsonObject options, WriteTargets targets,
        CancellationToken cancellation)
    {
        JsonArray rows = [];
        string mark = what.Write!.UndoMark;
        if (what.Key == "build.structure")
        {
            JsonArray placements = options["placements"] as JsonArray ??
                                   new JsonArray(new JsonObject { ["prefab"] = options["prefab"]?.DeepClone(), ["at"] = options["at"]?.DeepClone() });
            foreach (JsonNode? placement in placements)
            {
                rows.Add(Row(null, placement?["at"], JsonNodes.Text(placement?["prefab"]), null, "placed", mark));
            }
        }

        List<string> ids = ActsOnIds(targets);
        Dictionary<string, (string? Display, string? Custom)> names =
            await NamesAsync(what, ids, call.Stream, cancellation).ConfigureAwait(false);
        foreach (string id in ids)
        {
            rows.Add(Row(id, null, NameOf(names, id), null, null, mark));
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

    // Report class hub: the hub reads what each target holds now and states what the write would make it.
    private async Task<JsonArray> HubRowsAsync(VerbCall call, WhatEntry what, JsonObject options, WriteTargets targets,
        CancellationToken cancellation)
    {
        string mark = what.Write!.UndoMark;
        List<(string Id, JsonNode? New, JsonNode? Logic)> changes = Changes(what, options);
        List<string> ids = [];
        foreach ((string id, JsonNode? _, JsonNode? _) in changes)
        {
            ids.Add(id);
        }

        Dictionary<string, (string? Display, string? Custom)> names =
            await NamesAsync(what, ids, call.Stream, cancellation).ConfigureAwait(false);
        JsonArray? current = what.Key == "write.logic"
            ? await CurrentLogicAsync(what, changes, call.Stream, cancellation).ConfigureAwait(false)
            : null;
        JsonArray rows = [];
        for (int index = 0; index < changes.Count; index++)
        {
            (string id, JsonNode? next, JsonNode? logic) = changes[index];
            JsonNode? now = what.Key switch
            {
                "write.logic" => current?[index]?["value"],
                "write.label" => names.TryGetValue(id, out (string? Display, string? Custom) name)
                    ? JsonValue.Create(name.Custom ?? name.Display)
                    : null,
                _ => null
            };
            JsonObject row = Row(id, null, NameOf(names, id), now, next, mark);
            if (logic != null)
            {
                row["logic"] = logic.DeepClone();
            }

            rows.Add(row);
        }

        return rows;
    }

    // What a hub-class write would change, per target: its id, the new value, and the logic type for logic writes.
    private static List<(string Id, JsonNode? New, JsonNode? Logic)> Changes(WhatEntry what, JsonObject options)
    {
        List<(string, JsonNode?, JsonNode?)> changes = [];
        string? entries = what.Key switch
        {
            "write.logic" => "writes",
            "write.label" => "labels",
            "write.paint" => "items",
            _ => null
        };
        if (entries != null && options[entries] is JsonArray list)
        {
            foreach (JsonNode? entry in list)
            {
                if (JsonNodes.Text(entry?["id"]) is { } id)
                {
                    changes.Add((id, NewOf(what, entry!.AsObject()), entry["logic"]));
                }
            }
        }

        foreach (JsonNode? id in AsList(options["id"]))
        {
            if (JsonNodes.Text(id) is { } text)
            {
                changes.Add((text, NewOf(what, options), options["logic"]));
            }
        }

        return changes;
    }

    // The new value of a hub-class write, as its row states it.
    private static JsonNode? NewOf(WhatEntry what, JsonObject given)
    {
        string? text = what.Key switch
        {
            "write.logic" => null,
            "write.label" => null,
            "write.paint" => null,
            "write.chip" => Parts(given),
            "write.screen" => "press " + JsonNodes.Text(given["element"]),
            "write.flight_log" => JsonNodes.Text(given["action"]),
            "write.uplink" => "downlink " + JsonNodes.Text(given["downlink"]),
            _ => null
        };
        return text != null
            ? JsonValue.Create(text)
            : (what.Key switch
            {
                "write.logic" => given["value"],
                "write.label" => given["label"],
                "write.paint" => given["color"],
                _ => null
            })?.DeepClone();
    }

    private static string Parts(JsonObject given)
    {
        List<string> parts = [];
        if (given["code"] is { } code)
        {
            parts.Add($"code ({(JsonNodes.Text(code) ?? string.Empty).Length} characters)");
        }

        if (given["code_file"] is { } file)
        {
            parts.Add("code from " + JsonNodes.Text(file));
        }

        if (given["pins"] is { } pins)
        {
            parts.Add("pins " + pins.ToJsonString());
        }

        if (given["run"] is { } run)
        {
            parts.Add("run " + JsonNodes.Text(run));
        }

        return string.Join(", ", parts);
    }

    // The current logic values the writes would change, in their order (read_logic_many, one row per pair).
    private async Task<JsonArray?> CurrentLogicAsync(WhatEntry what, List<(string Id, JsonNode? New, JsonNode? Logic)> changes,
        long stream, CancellationToken cancellation)
    {
        if (changes.Count == 0)
        {
            return null;
        }

        JsonArray reads = [];
        foreach ((string id, JsonNode? _, JsonNode? logic) in changes)
        {
            reads.Add(new JsonObject { ["reference_id"] = id, ["logic_type"] = logic?.DeepClone() });
        }

        HandlerCall read = new("read_logic_many", new JsonObject { ["reads"] = reads }, _map.Rename.For(what, "read_logic_many"));
        return await SendAsync(read, stream, null, cancellation).ConfigureAwait(false) is GameReply.Ok ok
            ? JsonNodes.FromElement(ok.Result)?["results"] as JsonArray
            : null;
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

    /// <summary>
    /// The names of the things ids name (display and custom name), read in one call (thing_health); ids that are words
    /// ("player") and a read that fails give no name.
    /// </summary>
    private async Task<Dictionary<string, (string? Display, string? Custom)>> NamesAsync(WhatEntry what, List<string> ids,
        long stream, CancellationToken cancellation)
    {
        Dictionary<string, (string?, string?)> names = new(StringComparer.Ordinal);
        JsonArray asked = [];
        foreach (string id in ids)
        {
            if (id.Length > 0 && IsDigits(id))
            {
                asked.Add(id);
            }
        }

        if (asked.Count == 0)
        {
            return names;
        }

        HandlerCall read = new("thing_health", new JsonObject { ["reference_ids"] = asked }, _map.Rename.For(what, "thing_health"));
        if (await SendAsync(read, stream, null, cancellation).ConfigureAwait(false) is GameReply.Ok ok)
        {
            foreach (JsonObject row in BulkWrites.Rows(JsonNodes.FromElement(ok.Result)))
            {
                if (JsonNodes.Text(row["reference_id"]) is { } id)
                {
                    names[id] = (JsonNodes.Text(row["display_name"]), JsonNodes.Text(row["custom_name"]));
                }
            }
        }

        return names;
    }

    private static string? NameOf(Dictionary<string, (string? Display, string? Custom)> names, string id) =>
        names.TryGetValue(id, out (string? Display, string? Custom) name) ? name.Display : null;

    private static bool IsDigits(string text)
    {
        foreach (char character in text)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return true;
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

    private static JsonArray AsList(JsonNode? value) => value switch
    {
        null => [],
        JsonArray list => list,
        _ => new JsonArray(value.DeepClone())
    };

    // ---- bulk writes (write-N) -------------------------------------------------------------------------------------

    // inspect job and undo of a write-N are the hub's: they never reach the mod.
    private Task<VerbReply>? BulkJob(VerbCall call, WhatEntry what, JsonObject options)
    {
        string? id = JsonNodes.Text(options["id"])?.Trim();
        if (id == null || !id.StartsWith(BulkWrites.Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        BulkWrite? write = _bulk.Get(id);
        if (write == null)
        {
            return what.Key is "inspect.job" or "undo"
                ? Task.FromResult<VerbReply>(new VerbReply.Failed("job_not_found",
                    $"No bulk write {id} in this session (bulk writes last until the MCP server stops)."))
                : null;
        }

        return what.Key switch
        {
            "inspect.job" => Task.FromResult<VerbReply>(new VerbReply.Done(write.View())),
            "undo" => UndoBulkAsync(call, what, options, write, CancellationToken.None),
            _ => null
        };
    }

    // undo write-N: reports its steps until confirm (the direct rule's undo), then writes them back newest first.
    private async Task<VerbReply> UndoBulkAsync(VerbCall call, WhatEntry what, JsonObject options, BulkWrite write,
        CancellationToken cancellation)
    {
        List<BulkStep> steps = BulkWrites.Inverse(write);
        WriteTargets targets = WriteTargetPaths.Resolve(what, options);
        bool acting = GateFor(call).Decide(call, targets) is WriteDecision.Act;
        JsonArray rows = [];
        if (!acting || write.Undone)
        {
            foreach (BulkStep step in steps)
            {
                rows.Add(step.Row.DeepClone());
            }

            return new VerbReply.Done(new JsonObject
            {
                ["report_only"] = true,
                ["job_id"] = write.Id,
                ["undone"] = write.Undone,
                ["targets"] = rows
            });
        }

        // One step at a time, newest first; a refused step stops the rest, named in not_run.
        JsonArray notRun = [];
        int done = 0;
        bool stopped = false;
        foreach (BulkStep step in steps)
        {
            if (stopped)
            {
                notRun.Add(step.Row["id"]?.DeepClone());
                continue;
            }

            GameReply reply = await SendAsync(new HandlerCall(step.Handler, (JsonObject)step.Args.DeepClone(),
                _map.Rename.For(write.What, step.Handler)), call.Stream, null, cancellation).ConfigureAwait(false);
            JsonObject row = (JsonObject)step.Row.DeepClone();
            row["ok"] = reply is GameReply.Ok;
            if (reply is GameReply.Ok)
            {
                done++;
            }
            else
            {
                row["error"] = Failure(reply).ToJson();
                stopped = true;
            }

            rows.Add(row);
        }

        write.Undone = done == steps.Count;
        JsonObject answer = new()
        {
            ["job_id"] = write.Id,
            ["undone"] = done,
            ["results"] = rows,
            ["count"] = steps.Count
        };
        if (notRun.Count > 0)
        {
            answer["not_run"] = notRun;
        }

        return new VerbReply.Done(answer);
    }
}
