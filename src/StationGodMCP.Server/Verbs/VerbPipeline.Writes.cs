using System.Text;
using System.Text.Json.Nodes;
using StationGodMCP.Client;

namespace StationGodMCP.Server;

/// <summary>
/// Writes: the one place the gate is asked (with the targets the x-target paths, refs and spreads resolve to), the
/// report forms by report class, the act path with move item's names and undo marks, and the bulk writes (write-N).
/// </summary>
public sealed partial class VerbPipeline
{
    private const string ReplayKey = "__replay";
    private const string ReplayNoteKey = "__replay_note";

    private static readonly string[] BulkLists = ["writes", "labels", "items", "moves"];

    private IWriteGate GateFor(VerbCall call) =>
        call.Gate ?? _gate ?? (call.Face == Face.Script ? ReportingGate.Instance : _directGate);

    private async Task<VerbReply> WriteAsync(VerbCall call, WhatEntry what, JsonObject options, JsonArray? ids,
        List<RefUse> refs, CancellationToken cancellation)
    {
        // A spread the hub expands (paint a network): the report lists every target, and acting takes their ids.
        Dictionary<string, JsonNode?>? current = null;
        JsonObject asked = options;
        if (what.Key == "write.paint" && options["network"] != null)
        {
            switch (await PaintedNetworkAsync(call, what, options, cancellation).ConfigureAwait(false))
            {
                case (VerbReply.Failed failed, _, _):
                    return failed;
                case (null, { } expanded, { } colors):
                    options = expanded;
                    current = colors;
                    ids = null;
                    break;
            }
        }

        // A call the handlers cannot take is refused before the gate: a report of it would promise what cannot act.
        if (!HubForm(what, options, ids))
        {
            JsonObject first = (JsonObject)options.DeepClone();
            if (ids != null && what.ListForm == "split")
            {
                first["id"] = ids.Count > 0 ? ids[0]?.DeepClone() : null;
            }

            if (_map.Translate(what, first) is Translation.Answered { Reply: VerbReply.Failed cannot })
            {
                return cannot;
            }
        }

        WriteTargets targets = WriteTargetPaths.Resolve(what, asked, refs);
        switch (GateFor(call).Decide(call, targets))
        {
            case WriteDecision.Act:
                return await ActAsync(call, what, options, ids, targets, refs.Count > 0, cancellation).ConfigureAwait(false);
            case WriteDecision.Report:
                VerbReply report = await ReportAsync(call, what, options, ids, targets, current, cancellation)
                    .ConfigureAwait(false);
                Replayable(report, what, options, targets, asked != options);
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

    // undo of a write-N or the paste, and paint of a network, are the hub's own forms: no translation to check first.
    private static bool HubForm(WhatEntry what, JsonObject options, JsonArray? ids)
    {
        if (what.Key != "undo")
        {
            return false;
        }

        foreach (JsonNode? id in ids ?? new JsonArray(options["id"]?.DeepClone()))
        {
            string? text = JsonNodes.Text(id)?.Trim();
            if (BulkWrites.IsBulkId(text) || string.Equals(text, "paste", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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

    private async Task<VerbReply> ActAsync(VerbCall call, WhatEntry what, JsonObject options, JsonArray? ids,
        WriteTargets targets, bool fromRef, CancellationToken cancellation)
    {
        if (what.Key == "undo" && ids == null && OneOwn(options) is { } own)
        {
            return await OwnUndoAsync(call, what, options, own, true, cancellation).ConfigureAwait(false);
        }

        bool moves = what.Key == "move.item";
        Dictionary<string, (string? Display, string? Custom)>? names = moves
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

        if (ids != null || fromRef || options["id"] is JsonArray)
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

    /// <summary>
    /// Move item's rows by the handler that answered: a slot move names what moved (display_name) and can be undone
    /// unless it merged into another stack; a store move (into or out of a vault or silo) or a forced move cannot.
    /// </summary>
    private static void MarkMoves(JsonNode? reply, Dictionary<string, (string? Display, string? Custom)> names, bool force)
    {
        bool store = BulkWrites.IsStoreReply(reply);
        foreach (JsonObject row in BulkWrites.Rows(reply))
        {
            if (row["display_name"] == null && JsonNodes.Text(row["reference_id"]) is { } id &&
                names.TryGetValue(id, out (string? Display, string? Custom) name))
            {
                row["display_name"] = name.Display;
            }

            if (row["ok"] is { } ok && !JsonNodes.IsTrue(ok))
            {
                continue;
            }

            if (store || force)
            {
                row["undo"] = "[cannot undo: store move]";
            }
            else
            {
                row["undo"] = JsonNodes.Text(row["merged_into"]) is { } into ? $"[cannot undo: merged into {into}]" : "[undo]";
            }
        }
    }

    // ---- reports ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The report a write gives instead of acting, one shape on every face: {report_only: true, targets: [{id or cell,
    /// name, current, new, undo, text}], placed: [] for a build}. Report class dry_run and mod send the handler's own
    /// report (dry_run: true) and keep its keys; hub reads the current values itself; none sends nothing.
    /// </summary>
    private async Task<VerbReply> ReportAsync(VerbCall call, WhatEntry what, JsonObject options, JsonArray? ids,
        WriteTargets targets, Dictionary<string, JsonNode?>? current, CancellationToken cancellation)
    {
        if (what.Key == "undo" && ids == null && OneOwn(options) is { } own)
        {
            return await OwnUndoAsync(call, what, options, own, false, cancellation).ConfigureAwait(false);
        }

        JsonObject report;
        JsonArray rows;
        switch (what.Write!.Report)
        {
            case "hub":
                report = new JsonObject();
                rows = await HubRowsAsync(call, what, options, current, cancellation).ConfigureAwait(false);
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

                report = done.Reply is JsonObject handlerReport
                    ? handlerReport
                    : new JsonObject { ["reply"] = done.Reply?.DeepClone() };
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

    /// <summary>
    /// What a repeat with confirm of this report acts on, kept in the report until its ref is minted: the options with
    /// refs as their ids and an expanded spread as its ids; a spread the handler selects itself is sent again, and the
    /// repeat says the selection may differ.
    /// </summary>
    private static void Replayable(VerbReply report, WhatEntry what, JsonObject options, WriteTargets targets, bool expanded)
    {
        if (report is not VerbReply.Done { Reply: JsonObject shown })
        {
            return;
        }

        JsonObject replay = (JsonObject)options.DeepClone();
        replay.Remove("confirm");
        shown[ReplayKey] = replay;
        if (expanded)
        {
            return;
        }

        foreach (WriteTarget target in targets.Targets)
        {
            if (target.Source == TargetSource.Selector)
            {
                shown[ReplayNoteKey] = $"{what.Label} was sent again with confirm: its selection may differ from the report's.";
                return;
            }
        }
    }

    // The rows of dry_run, mod and none reports: one per acts-on id (named), a build's placements by cell, each spread.
    private async Task<JsonArray> TargetRowsAsync(VerbCall call, WhatEntry what, JsonObject options, WriteTargets targets,
        CancellationToken cancellation)
    {
        JsonArray rows = [];
        string mark = RowMark(what, options, null);
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
    private async Task<JsonArray> HubRowsAsync(VerbCall call, WhatEntry what, JsonObject options,
        Dictionary<string, JsonNode?>? known, CancellationToken cancellation)
    {
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
                _ => known != null && known.TryGetValue(id, out JsonNode? held) ? held : null
            };
            JsonObject row = Row(id, null, NameOf(names, id), now, next, RowMark(what, options, logic));
            if (logic != null)
            {
                row["logic"] = logic.DeepClone();
            }

            rows.Add(row);
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
            "write.chip" => Parts(given),
            "write.screen" => "press " + JsonNodes.Text(given["element"]),
            "write.flight_log" => JsonNodes.Text(given["action"]),
            "write.uplink" => "downlink " + JsonNodes.Text(given["downlink"]),
            _ => null
        };
        if (text != null)
        {
            return JsonValue.Create(text);
        }

        JsonNode? value = what.Key switch
        {
            "write.logic" => given["value"],
            "write.label" => given["label"],
            "write.paint" => given["color"],
            _ => null
        };
        return value?.DeepClone();
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

    /// <summary>
    /// Paint of a network: its pieces (and with include: ["devices"] its devices) read page by page (connections, the
    /// survey network handler), as ids with their current colours; the options become paint's own id list.
    /// </summary>
    private async Task<(VerbReply.Failed? Failed, JsonObject? Options, Dictionary<string, JsonNode?>? Colors)>
        PaintedNetworkAsync(VerbCall call, WhatEntry what, JsonObject options, CancellationToken cancellation)
    {
        bool devices = JsonNodes.Holds(options["include"], "devices");
        JsonArray ids = [];
        Dictionary<string, JsonNode?> colors = new(StringComparer.Ordinal);
        int offset = 0;
        while (true)
        {
            JsonObject args = new() { ["network_id"] = options["network"]!.DeepClone(), ["offset"] = offset, ["limit"] = 500 };
            if (options["kind"] is { } kind)
            {
                args["kind"] = kind.DeepClone();
            }

            HandlerCall read = new("connections", args, _map.Rename.For(what, "connections"));
            GameReply reply = await SendAsync(read, call.Stream, null, cancellation).ConfigureAwait(false);
            if (reply is not GameReply.Ok ok)
            {
                return (Failure(reply, read, what), null, null);
            }

            JsonNode? page = JsonNodes.FromElement(ok.Result);
            JsonArray members = page?["members"] as JsonArray ?? [];
            foreach (JsonNode? member in members)
            {
                bool device = JsonNodes.Text(member?["member"]) == "device";
                if ((device && !devices) || JsonNodes.Text(member?["reference_id"]) is not { } id)
                {
                    continue;
                }

                ids.Add(id);
                colors[id] = member!["color"]?["name"]?.DeepClone() ?? member["color"]?.DeepClone();
            }

            offset += members.Count;
            if (!JsonNodes.IsTrue(page?["has_more"]) || members.Count == 0)
            {
                break;
            }
        }

        JsonObject expanded = new();
        foreach ((string name, JsonNode? value) in options)
        {
            if (name is not ("network" or "kind" or "include"))
            {
                expanded[name] = value?.DeepClone();
            }
        }

        expanded["id"] = ids;
        return (null, expanded, colors);
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

    // ---- the hub's own undo forms: write-N and the paste -----------------------------------------------------------

    // The one id of an undo when it is the hub's own form ("paste" or write-N); null for a job id.
    private static string? OneOwn(JsonObject options)
    {
        string? id = JsonNodes.Text(options["id"])?.Trim();
        return BulkWrites.IsBulkId(id) || string.Equals(id, "paste", StringComparison.OrdinalIgnoreCase) ? id : null;
    }

    private async Task<VerbReply> OwnUndoAsync(VerbCall call, WhatEntry what, JsonObject options, string id, bool acting,
        CancellationToken cancellation)
    {
        if (!BulkWrites.IsBulkId(id))
        {
            return acting
                ? await ExecuteAsync(_map.Translate(what, options), call, what, options, true, cancellation).ConfigureAwait(false)
                : await PasteUndoReportAsync(call, what, cancellation).ConfigureAwait(false);
        }

        return _bulk.Get(id) is { } write
            ? await UndoBulkAsync(call, write, options, acting, cancellation).ConfigureAwait(false)
            : NoBulk(id);
    }

    private static VerbReply.Failed NoBulk(string id) =>
        new("job_not_found", $"No bulk write {id} in this session (bulk writes last until the MCP server stops).");

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

    /// <summary>
    /// undo write-N. A report lists every step with its own mark (cannot-undo ones too). Acting writes the steps back
    /// newest first, from from_step, skipping steps already done; before each it reads the target again and leaves one
    /// that changed since the write (the user, a chip, a run) unless allow: ["overwrite"]; a refused step stops the
    /// rest. One undo runs at a time; a write with nothing left to undo says so.
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
            if (step.Handler == null || step.Number < from || write.Done.Contains(step.Number))
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
                await DriftAsync(call, write.What, step.Check, cancellation).ConfigureAwait(false) is { } now)
            {
                row["ok"] = false;
                row["drifted"] = true;
                row["current"] = now.DeepClone();
                row["note"] = "changed since the write; left as it is (allow: [\"overwrite\"] writes it back anyway)";
                drifted.Add(step.Number);
                rows.Add(row);
                continue;
            }

            GameReply reply = await SendAsync(new HandlerCall(step.Handler, (JsonObject)step.Args.DeepClone(),
                _map.Rename.For(write.What, step.Handler)), call.Stream, null, cancellation).ConfigureAwait(false);
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

    // What the target holds now when it is not what the write left; null when it is the same (or cannot be read).
    private async Task<JsonNode?> DriftAsync(VerbCall call, WhatEntry what, BulkCheck check, CancellationToken cancellation)
    {
        JsonNode? now;
        if (check.Logic != null)
        {
            HandlerCall read = new("read_logic_many", new JsonObject
            {
                ["reads"] = new JsonArray(new JsonObject { ["reference_id"] = check.Id, ["logic_type"] = check.Logic.DeepClone() })
            }, _map.Rename.For(what, "read_logic_many"));
            if (await SendAsync(read, call.Stream, null, cancellation).ConfigureAwait(false) is not GameReply.Ok ok)
            {
                return null;
            }

            now = JsonNodes.FromElement(ok.Result)?["results"]?[0]?["value"];
        }
        else
        {
            Dictionary<string, (string? Display, string? Custom)> names =
                await NamesAsync(what, [check.Id], call.Stream, cancellation).ConfigureAwait(false);
            if (!names.TryGetValue(check.Id, out (string? Display, string? Custom) name))
            {
                return null;
            }

            now = JsonValue.Create(name.Custom ?? string.Empty);
        }

        bool same = JsonNodes.Number(now) is { } number && JsonNodes.Number(check.Left) is { } left
            ? Math.Abs(number - left) < 1e-6
            : JsonNode.DeepEquals(now, check.Left) ||
              (JsonNodes.Text(now) ?? string.Empty) == (JsonNodes.Text(check.Left) ?? string.Empty);
        return same ? null : now ?? JsonValue.Create("?");
    }
}
