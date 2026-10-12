using System.Text.Json;
using System.Text.Json.Nodes;
using StationGodMCP.Client;

namespace StationGodMCP.Server;

/// <summary>A run's wait, hold, ref and more (hub.md, The hub protocol, Runs and Holds).</summary>
internal sealed partial class Run
{
    private const string Invalid = VerbFailure.InvalidArgument;

    // ---- wait -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// {s, kind, in_loop}: a real wait (a stop answers it at once) until a report run's first write. After that, in a
    /// report run, a wait in a loop or a watch counts: the first returns at once, the second ends the pass; any other
    /// wait returns at once. After a stop a wait answers {} at once, without raising.
    /// </summary>
    public async Task<VerbReply> WaitAsync(PipelineRequest request, CancellationToken cancellation)
    {
        Enter(request.Line);
        try
        {
            if (TakeStop(request.Line) is { } stop)
            {
                return stop;
            }

            double? seconds = JsonNodes.Number(request.Parameters["s"]);
            if (seconds is not >= 0 || double.IsInfinity(seconds.Value))
            {
                return new VerbReply.Failed(Invalid, "wait needs s: a number of seconds from 0.");
            }

            bool watch = JsonNodes.Text(request.Parameters["kind"]) == "watch";
            bool inLoop = JsonNodes.IsTrue(request.Parameters["in_loop"]);
            lock (Record.Sync)
            {
                if (_stopCode != null)
                {
                    return Empty();
                }

                if (!Record.Confirm && _wrote)
                {
                    if (_passEnded || !(inLoop || watch))
                    {
                        return Empty();
                    }

                    _countedWaits++;
                    if (_countedWaits >= 2)
                    {
                        _passEnded = true;
                        Record.PassEndLine = request.Line;
                        return new VerbReply.Failed("pass_end",
                            "the report pass ends here: a waiting loop runs once in a report run", passEnd: true);
                    }

                    return watch ? new VerbReply.Done(new JsonObject { ["instant"] = true }) : Empty();
                }
            }

            using CancellationTokenSource either =
                CancellationTokenSource.CreateLinkedTokenSource(cancellation, _stopSignal.Token);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds.Value), either.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                return (VerbReply?)TakeStop(request.Line) ?? Empty();
            }

            return Empty();
        }
        finally
        {
            Exit();
        }
    }

    private static VerbReply.Done Empty() => new(new JsonObject());

    // ---- hold -------------------------------------------------------------------------------------------------------

    /// <summary>{phase, ids, logic, value?, after?}: enter or exit a hold. Holds are logic writes only.</summary>
    public async Task<VerbReply> HoldAsync(PipelineRequest request, CancellationToken cancellation)
    {
        Enter(request.Line);
        try
        {
            if (TakeStop(request.Line) is { } stop)
            {
                return stop;
            }

            JsonObject given = request.Parameters;
            List<string> ids = [];
            foreach (JsonNode? id in given["ids"] is JsonArray list ? list : new JsonArray(given["ids"]?.DeepClone()))
            {
                if (IdText(id) is not { } text)
                {
                    return new VerbReply.Failed(Invalid, "hold needs ids: an id or a list of ids (strings or integers).");
                }

                ids.Add(text);
            }

            if (ids.Count == 0 || given["logic"] is not { } logic || LogicName(logic) == null)
            {
                return new VerbReply.Failed(Invalid, "hold needs ids and logic (a logic type name or number).");
            }

            return JsonNodes.Text(given["phase"]) switch
            {
                "enter" => await EnterHoldAsync(ids, logic, given, request.Line, cancellation).ConfigureAwait(false),
                "exit" => await ExitHoldAsync(ids, logic, given, request.Line, cancellation).ConfigureAwait(false),
                _ => new VerbReply.Failed(Invalid, "hold needs phase: enter or exit.")
            };
        }
        finally
        {
            Exit();
        }
    }

    // Read after where not given, register it with the mod first, then write value through the pipeline.
    private async Task<VerbReply> EnterHoldAsync(List<string> ids, JsonNode logic, JsonObject given, int? line,
        CancellationToken cancellation)
    {
        lock (Record.Sync)
        {
            if (_stopCode != null)
            {
                return new VerbReply.Failed("stopped", "this run was stopped; a new hold does not start.");
            }

            _wrote = true;
        }

        List<JsonNode?> after;
        if (given["after"] is { } stated)
        {
            if (Expanded(stated, ids.Count) is not { } expanded)
            {
                return new VerbReply.Failed(Invalid, "after is one value, or one per id.");
            }

            after = expanded;
        }
        else
        {
            VerbReply read = await SendAsync("read", "logic", new JsonObject { ["reads"] = Reads(ids, logic) },
                new RunCallContext(line), cancellation).ConfigureAwait(false);
            if (read is not VerbReply.Done readDone)
            {
                return read;
            }

            after = [];
            List<JsonObject> rows = BulkWrites.Rows(readDone.Reply);
            for (int index = 0; index < ids.Count; index++)
            {
                if (index >= rows.Count || !JsonNodes.IsTrue(rows[index]["ok"]) && rows[index]["ok"] != null)
                {
                    JsonNode? error = index < rows.Count ? rows[index]["error"] : null;
                    return new VerbReply.Failed(JsonNodes.Text(error?["code"]) ?? "read_failed",
                        $"hold could not read {ids[index]}'s {LogicName(logic)}: " +
                        (JsonNodes.Text(error?["message"]) ?? "no value came back"));
                }

                after.Add(rows[index]["value"]?.DeepClone());
            }
        }

        JsonNode? value = given["value"];
        Dictionary<string, JsonNode?> before = new(StringComparer.Ordinal);
        for (int index = 0; index < ids.Count; index++)
        {
            before[ids[index]] = after[index];
        }

        if (Record.Confirm)
        {
            if (!AllSeen(ids))
            {
                return HoldStop(ids, logic, value, after, line);
            }

            JsonArray set = [];
            for (int index = 0; index < ids.Count; index++)
            {
                set.Add(new JsonObject
                {
                    ["reference_id"] = ids[index],
                    ["logic_type"] = logic.DeepClone(),
                    ["value"] = after[index]?.DeepClone()
                });
            }

            GameReply registered = await _link.HoldRestoreAsync(Record.Stream,
                JsonNodes.ToElement(new JsonObject { ["set"] = set }), cancellation).ConfigureAwait(false);
            if (registered is not GameReply.Ok)
            {
                return VerbPipeline.Failure(registered);
            }

            lock (Record.Sync)
            {
                for (int index = 0; index < ids.Count; index++)
                {
                    _holds.Add((ids[index], LogicName(logic)!, after[index]?.DeepClone()));
                }
            }
        }

        if (value != null)
        {
            VerbReply wrote = await SendAsync("write", "logic", new JsonObject { ["writes"] = Writes(ids, logic, value) },
                new RunCallContext(line, WriteGroup.Held) { KnownBefore = before }, cancellation).ConfigureAwait(false);
            if (wrote is VerbReply.Failed failed)
            {
                if (Record.Confirm)
                {
                    await ClearHoldsAsync(ids, logic, cancellation).ConfigureAwait(false);
                }

                return failed;
            }
        }

        JsonArray afterList = [];
        foreach (JsonNode? one in after)
        {
            afterList.Add(one?.DeepClone());
        }

        return new VerbReply.Done(new JsonObject { ["after"] = afterList });
    }

    // A hold on an unseen target stops the run as an unseen write does, before anything is registered.
    private VerbReply HoldStop(List<string> ids, JsonNode logic, JsonNode? value, List<JsonNode?> after, int? line)
    {
        JsonArray rows = [];
        for (int index = 0; index < ids.Count; index++)
        {
            JsonObject row = VerbPipeline.Row(ids[index], null, null, after[index], value, "[undo]");
            row["logic"] = logic.DeepClone();
            rows.Add(row);
        }

        string fix;
        lock (Record.Sync)
        {
            fix = Fix();
        }

        VerbReply.Failed stop = new("unseen_target", "hold names targets not seen; nothing acted.", fix,
            new JsonObject { ["rows"] = rows }, stopped: true);
        NoteRuleStop(stop, line);
        return stop;
    }

    // Write after (acting as CLEANUP after a stop), then clear the registration.
    private async Task<VerbReply> ExitHoldAsync(List<string> ids, JsonNode logic, JsonObject given, int? line,
        CancellationToken cancellation)
    {
        string logicName = LogicName(logic)!;
        List<JsonNode?> after;
        if (given["after"] is { } stated)
        {
            if (Expanded(stated, ids.Count) is not { } expanded)
            {
                return new VerbReply.Failed(Invalid, "after is one value, or one per id.");
            }

            after = expanded;
        }
        else if (Registered(ids, logicName) is { } registered)
        {
            after = registered;
        }
        else
        {
            return new VerbReply.Failed(Invalid, "hold exit needs after: the values to restore.");
        }

        VerbReply wrote = await SendAsync("write", "logic", new JsonObject { ["writes"] = Writes(ids, logic, after) },
            new RunCallContext(line, WriteGroup.Held, holdExit: true), cancellation).ConfigureAwait(false);
        bool world;
        lock (Record.Sync)
        {
            world = _stopWorld;
        }

        if (Record.Confirm && !world)
        {
            await ClearHoldsAsync(ids, logic, cancellation).ConfigureAwait(false);
        }

        if (wrote is VerbReply.Failed failed)
        {
            return failed;
        }

        JsonNode? reply = ((VerbReply.Done)wrote).Reply;
        bool reported = reply is JsonObject report && JsonNodes.IsTrue(report["report_only"]);
        List<JsonObject> results = BulkWrites.Rows(reply);
        JsonArray rows = [];
        for (int index = 0; index < ids.Count; index++)
        {
            JsonObject row = new() { ["id"] = ids[index], ["logic"] = logic.DeepClone(), ["value"] = after[index]?.DeepClone() };
            JsonObject? result = reported ? null : RowFor(results, index, ids[index]);
            if (reported)
            {
                row["reported"] = true;
            }
            else if (result != null && result["ok"] is { } ok && !JsonNodes.IsTrue(ok))
            {
                row["ok"] = false;
                row["error"] = result["error"]?.DeepClone();
            }
            else
            {
                row["ok"] = true;
                row["restored"] = true;
            }

            rows.Add(row);
        }

        return new VerbReply.Done(new JsonObject { ["rows"] = rows });
    }

    private async Task ClearHoldsAsync(List<string> ids, JsonNode logic, CancellationToken cancellation)
    {
        string logicName = LogicName(logic)!;
        JsonArray clear = [];
        foreach (string id in ids)
        {
            clear.Add(new JsonObject { ["reference_id"] = id, ["logic_type"] = logic.DeepClone() });
        }

        await _link.HoldRestoreAsync(Record.Stream, JsonNodes.ToElement(new JsonObject { ["clear"] = clear }), cancellation)
            .ConfigureAwait(false);
        lock (Record.Sync)
        {
            _holds.RemoveAll(hold => hold.Logic == logicName && ids.Contains(hold.Id));
        }
    }

    // The registered after per id, or null when any id has none.
    private List<JsonNode?>? Registered(List<string> ids, string logic)
    {
        List<JsonNode?> after = [];
        lock (Record.Sync)
        {
            foreach (string id in ids)
            {
                int found = _holds.FindIndex(hold => hold.Id == id && hold.Logic == logic);
                if (found < 0)
                {
                    return null;
                }

                after.Add(_holds[found].After?.DeepClone());
            }
        }

        return after;
    }

    private static List<JsonNode?>? Expanded(JsonNode stated, int count)
    {
        List<JsonNode?> values = [];
        if (stated is JsonArray list)
        {
            if (list.Count != count)
            {
                return null;
            }

            foreach (JsonNode? value in list)
            {
                values.Add(value?.DeepClone());
            }

            return values;
        }

        for (int index = 0; index < count; index++)
        {
            values.Add(stated.DeepClone());
        }

        return values;
    }

    private static JsonArray Reads(List<string> ids, JsonNode logic)
    {
        JsonArray reads = [];
        foreach (string id in ids)
        {
            reads.Add(new JsonObject { ["id"] = id, ["logic"] = logic.DeepClone() });
        }

        return reads;
    }

    private static JsonArray Writes(List<string> ids, JsonNode logic, JsonNode value)
    {
        JsonArray writes = [];
        foreach (string id in ids)
        {
            writes.Add(new JsonObject { ["id"] = id, ["logic"] = logic.DeepClone(), ["value"] = value.DeepClone() });
        }

        return writes;
    }

    private static JsonArray Writes(List<string> ids, JsonNode logic, List<JsonNode?> values)
    {
        JsonArray writes = [];
        for (int index = 0; index < ids.Count; index++)
        {
            writes.Add(new JsonObject
            {
                ["id"] = ids[index],
                ["logic"] = logic.DeepClone(),
                ["value"] = values[index]?.DeepClone()
            });
        }

        return writes;
    }

    // ---- ref and more -----------------------------------------------------------------------------------------------

    /// <summary>rN: {ref, ids (shown), reply}; script-N: {ref, returned, printed} of a finished run.</summary>
    public Task<VerbReply> RefAsync(PipelineRequest request, CancellationToken cancellation)
    {
        string? id = JsonNodes.Text(request.Parameters["id"])?.Trim();
        if (id == null)
        {
            return Answer(new VerbReply.Failed(Invalid, "ref needs id: a ref (r7) or a run (script-4)."));
        }

        if (id.StartsWith("script-", StringComparison.OrdinalIgnoreCase))
        {
            return Answer(_host.RunRef(id));
        }

        if (RefStore.Parse(id) is not { } parsed || _pipeline.Refs.Get(parsed.Id) is not { } stored)
        {
            return Answer(new VerbReply.Failed("ref_not_found",
                $"No ref '{id}' (refs last until the world changes)."));
        }

        JsonArray ids = [];
        foreach (string shown in RefStore.IdsOf(stored, parsed.Path))
        {
            ids.Add(shown);
        }

        return Answer(new VerbReply.Done(new JsonObject
        {
            ["ref"] = id,
            ["ids"] = ids,
            ["reply"] = stored.Whole?.DeepClone()
        }));
    }

    /// <summary>{cursor, limit?}: the next entries of a reply's main list.</summary>
    public Task<VerbReply> MoreAsync(PipelineRequest request, CancellationToken cancellation)
    {
        if (JsonNodes.Text(request.Parameters["cursor"]) is not { } cursor)
        {
            return Answer(new VerbReply.Failed(Invalid, "more needs cursor: the hub cursor a reply carried."));
        }

        int? limit = JsonNodes.Number(request.Parameters["limit"]) is { } most ? (int)most : null;
        return _pipeline.MoreAsync(request.Stream, cursor, limit, cancellation);
    }

    private static Task<VerbReply> Answer(VerbReply reply) => Task.FromResult(reply);
}
