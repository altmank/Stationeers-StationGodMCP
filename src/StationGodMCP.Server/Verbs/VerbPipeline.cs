using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using StationGodMCP.Client;

namespace StationGodMCP.Server;

/// <summary>
/// The verb pipeline every caller goes through (docs/architecture/hub.md): record the call, resolve a repeat and ref
/// targets, check it (OptionCheck), let the write gate decide on a write with its resolved targets (act, report, refuse,
/// stop), and send it to the mod as the verb itself: {method: verb, params: {what, ...options}}, without the options
/// the hub answers (shaping, refs, repeat, job waits, file paths). Direct replies get refs and the cap; script and
/// program list replies get a hub cursor. help is answered from the generated pages; run_script and the calls naming a
/// run (inspect job, server stop, undo of a script run) are refused here, since a pipeline holds no runs: the runs
/// belong to the MCP server face, whose client session owns them.
/// </summary>
public sealed partial class VerbPipeline
{
    /// <summary>Why a pipeline with no run host refuses the calls that name a run (a --hub child serves the game and
    /// the four script whats, but holds no runs).</summary>
    private const string RunlessHub =
        "belongs to the MCP server face, whose client session owns the runs; this hub serves no runs";

    /// <summary>run_script's refusal: the runs' own verb, refused on the face that serves the store's four whats.</summary>
    internal const string RunScriptRefusal = $"run_script {RunlessHub} (the four script whats work here).";

    /// <summary>The code a drifted target's undo row carries (catalogue/verbs/_errors.json).</summary>
    internal const string DriftedCode = "target_drifted";

    private const double DefaultMaxWaitSeconds = 30;

    // Options the hub answers itself: they never reach the mod.
    private static readonly string[] HubOptions =
    [
        SidecarArguments.FieldsArgument, SidecarArguments.OmitArgument, SidecarArguments.LimitsArgument,
        SidecarArguments.OutputFileArgument, "whole", "repeat", "max_wait"
    ];

    // The mod's final job states (jobs: applied, applied_with_differences, applied_unchecked, stopped, refused; gas
    // moves: applied, failed); queued, waiting, scheduled and verifying go on.
    private static readonly string[] Final =
        ["applied", "applied_with_differences", "applied_unchecked", "stopped", "refused", "failed"];

    private readonly IGameLink _link;
    private readonly IWriteGate? _gate;
    private readonly DirectWriteGate _directGate;
    private readonly CallRecorder? _recorder;
    private readonly VerbCatalogue _catalogue;
    private readonly ScriptStore? _scripts;
    private readonly IShownText? _shown;
    private readonly RefStore _refs = new();
    private readonly BulkWrites _bulk = new();
    private readonly HubCursors _cursors = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<long, string> _worlds = new();
    private readonly Dictionary<long, Dictionary<object, DateTime>> _unsettled = new();
    private int _inFlight;

    /// <summary>
    /// gate, when given, decides every write that does not carry its own (VerbCall.Gate); otherwise the direct and
    /// program faces get the direct write rule and a script call without a gate reports. scripts, when given, serves
    /// the four script whats (write/find/inspect/remove script) and run_script {name, args}.
    /// </summary>
    internal VerbPipeline(IGameLink link, IWriteGate? gate = null, CallRecorder? recorder = null,
        VerbCatalogue? catalogue = null, IShownText? shown = null, ScriptStore? scripts = null)
    {
        _link = link;
        _gate = gate;
        _recorder = recorder;
        _shown = shown;
        _catalogue = catalogue ?? VerbCatalogue.Embedded;
        _scripts = scripts;
        _directGate = new DirectWriteGate(_catalogue);
        _link.WorldChanged += OnWorldChanged;
    }

    internal VerbCatalogue Catalogue => _catalogue;

    internal RefStore Refs => _refs;

    internal HubCursors Cursors => _cursors;

    /// <summary>How long a job poll waits between polls when the link has no job waits.</summary>
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    public async Task<VerbReply> CallAsync(VerbCall call, CancellationToken cancellation)
    {
        int inFlight = Interlocked.Increment(ref _inFlight);
        try
        {
            if (!call.Recorded)
            {
                _recorder?.Record(_clock.ElapsedMilliseconds, call.Face, call.Stream, inFlight, call.Verb, call.What, call.Args);
            }
            if (call.Face == Face.Direct)
            {
                NoteReAsk(call);
            }

            return await RunAsync(call, cancellation).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    /// <summary>Records a call where it arrives, before anything checks it (the MCP adapter's own refusals included).</summary>
    internal void RecordInput(Face face, long stream, string verb, string? what, JsonObject options) =>
        _recorder?.Record(_clock.ElapsedMilliseconds, face, stream, Volatile.Read(ref _inFlight) + 1, verb, what, options);

    /// <summary>The text actually sent to the MCP client (the cap's IShownText hook).</summary>
    internal void TextShown(string text) => _shown?.Shown(text);

    /// <summary>The stream ended: its hub cursors go.</summary>
    public void EndStream(long stream)
    {
        _cursors.FreeStream(stream);
        lock (_worlds)
        {
            _worlds.Remove(stream);
        }
    }

    /// <summary>
    /// Every call on the stream is made for this world (a run's, fixed at its start): once the link has left it, the
    /// calls are refused world_changed unsent, even before the world change reaches the run.
    /// </summary>
    internal void PinWorld(long stream, string? world)
    {
        if (world == null)
        {
            return;
        }

        lock (_worlds)
        {
            _worlds[stream] = world;
        }
    }

    private string? WorldOf(long stream)
    {
        lock (_worlds)
        {
            return _worlds.TryGetValue(stream, out string? world) ? world : null;
        }
    }

    // Ids in refs and cursors name things of the world they came from.
    private void OnWorldChanged(string? world)
    {
        _refs.Clear();
        _cursors.Clear();
    }

    private async Task<VerbReply> RunAsync(VerbCall call, CancellationToken cancellation)
    {
        if (call.Verb == "run_script")
        {
            return new VerbReply.Failed(VerbFailure.NotAvailable, RunScriptRefusal);
        }

        if (call.Verb == "help" && call.What != null)
        {
            // help's what is an option (help {verb, what}), not a what of help.
            JsonObject helpArgs = (JsonObject)call.Args.DeepClone();
            helpArgs["what"] = call.What;
            call = new VerbCall(call.Verb, null, helpArgs, call.Face, call.Stream, call.Line) { Gate = call.Gate };
        }

        if (call.Args["repeat"] != null && call.Face == Face.Direct)
        {
            switch (Repeat(call))
            {
                case RepeatOf.Answer answer:
                    return answer.Reply;
                case RepeatOf.Call again:
                    call = again.Repeated;
                    break;
            }
        }

        if (OptionCheck.Check(_catalogue, call, out VerbEntry? verb, out WhatEntry? what) is { } refused)
        {
            return refused;
        }

        if (verb!.Name == "help")
        {
            return HelpPages.Answer(_catalogue, call.Args);
        }

        List<RefUse> refs = [];
        if (ResolveRefs(what!, call.Args, refs, out JsonObject resolved) is { } unknown)
        {
            return unknown;
        }

        JsonObject options = Normalised(what!, call.Face, resolved);
        VerbReply reply = IsScriptWhat(what!)
            ? await ScriptAsync(call, what!, options).ConfigureAwait(false)
            : what!.Write != null
                ? await WriteAsync(call, what, options, refs, cancellation).ConfigureAwait(false)
                : await ReadAsync(call, what, options, cancellation).ConfigureAwait(false);
        if (call.Note != null && reply is VerbReply.Done { Reply: JsonObject noted })
        {
            noted["note"] = call.Note;
        }

        return call.Face == Face.Direct ? Capped(call, what!, options, reply) : Unmarked(reply);
    }

    // The replay a report keeps for its ref is the direct face's; other faces never see it.
    private static VerbReply Unmarked(VerbReply reply)
    {
        if (reply is VerbReply.Done { Reply: JsonObject marked })
        {
            marked.Remove(ReplayKey);
            marked.Remove(ReplayNoteKey);
        }

        return reply;
    }

    // On the script and program faces a what whose id list answers rows answers rows even for one id (D13).
    private static JsonObject Normalised(WhatEntry what, Face face, JsonObject given)
    {
        JsonObject options = new();
        foreach ((string name, JsonNode? value) in given)
        {
            if (value != null)
            {
                options[name] = value.DeepClone();
            }
        }

        if (face != Face.Direct && what.TakesIdList && JsonNodes.Text(options["id"]) is { } one)
        {
            options["id"] = new JsonArray(JsonValue.Create(one));
        }

        return options;
    }

    // A read: the hub's own ids (bulk writes, runs), a script or program list page with a hub cursor, or the mod's reply.
    private async Task<VerbReply> ReadAsync(VerbCall call, WhatEntry what, JsonObject options, CancellationToken cancellation)
    {
        if (what.Key == "inspect.job")
        {
            if (options["id"] is JsonArray ids && HoldsBulk(ids))
            {
                return await RowsAsync(call, what, options, ids, null, cancellation).ConfigureAwait(false);
            }

            if (OwnJob(options) is { } own)
            {
                return own;
            }
        }

        if (call.Face != Face.Direct && what.MainList != null && options["limit"] == null)
        {
            return await FirstPageAsync(call, what, options, cancellation).ConfigureAwait(false);
        }

        return await ModAsync(call, what, options, cancellation).ConfigureAwait(false);
    }

    // inspect job for an id the hub holds: a bulk write's view, or a run's, refused below when no run host is here.
    private VerbReply? OwnJob(JsonObject options)
    {
        string? id = JsonNodes.Text(options["id"])?.Trim();
        if (id == null)
        {
            return new VerbReply.Failed(VerbFailure.NotAvailable, $"inspect job with no id (the runs with notices) {RunlessHub}.");
        }

        if (id.StartsWith("script-", StringComparison.OrdinalIgnoreCase))
        {
            return new VerbReply.Failed(VerbFailure.NotAvailable, $"inspect job for a script run {RunlessHub}.");
        }

        if (BulkWrites.IsBulkId(id))
        {
            return _bulk.Get(id) is { } write ? new VerbReply.Done(write.View()) : NoBulk(id);
        }

        return null;
    }

    private static bool HoldsBulk(JsonArray ids)
    {
        foreach (JsonNode? id in ids)
        {
            if (BulkWrites.IsBulkId(JsonNodes.Text(id)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// An id list holding the hub's own ids (write-N): one answer per id, gathered as PLAN's rows {index, ok,
    /// reference_id, payload | error}; an undo's ids go newest first (by the number each ends in).
    /// </summary>
    private async Task<VerbReply> RowsAsync(VerbCall call, WhatEntry what, JsonObject options, JsonArray ids, bool? acting,
        CancellationToken cancellation)
    {
        JsonObject[] rowsByIndex = new JsonObject[ids.Count];
        int succeeded = 0;
        foreach (int index in Order(what, ids))
        {
            JsonNode? id = ids[index];
            JsonObject one = (JsonObject)options.DeepClone();
            one["id"] = id?.DeepClone();
            VerbReply reply = await OneAsync(call, what, one, acting, cancellation).ConfigureAwait(false);
            JsonObject row = new() { ["index"] = index, ["reference_id"] = id?.DeepClone() };
            switch (reply)
            {
                case VerbReply.Done done:
                    succeeded++;
                    row["ok"] = true;
                    row["payload"] = done.Reply?.DeepClone();
                    break;
                case VerbReply.Failed failed:
                    row["ok"] = false;
                    row["error"] = failed.ToJson();
                    break;
            }

            rowsByIndex[index] = row;
        }

        JsonArray rows = [];
        foreach (JsonObject row in rowsByIndex)
        {
            rows.Add(row);
        }

        return new VerbReply.Done(new JsonObject
        {
            ["results"] = rows,
            ["count"] = ids.Count,
            ["success_count"] = succeeded,
            ["error_count"] = ids.Count - succeeded
        });
    }

    // One id of a gathered list: the hub's own (a write-N's view or undo) or the mod's.
    private async Task<VerbReply> OneAsync(VerbCall call, WhatEntry what, JsonObject one, bool? acting,
        CancellationToken cancellation)
    {
        string? id = JsonNodes.Text(one["id"])?.Trim();
        if (what.Key == "inspect.job" && BulkWrites.IsBulkId(id))
        {
            return _bulk.Get(id!) is { } write ? new VerbReply.Done(write.View()) : NoBulk(id!);
        }

        if (what.Key == "undo" && BulkWrites.IsBulkId(id))
        {
            return await OwnUndoAsync(call, id!, one, acting == true, cancellation).ConfigureAwait(false);
        }

        return await ModAsync(call, what, acting is { } acts ? Confirmed(what, one, acts) : one, cancellation).ConfigureAwait(false);
    }

    // The order a gathered list's ids go in: as given, but an undo's newest first (by the number its id ends in).
    private static List<int> Order(WhatEntry what, JsonArray ids)
    {
        List<int> order = Enumerable.Range(0, ids.Count).ToList();
        if (what.Key == "undo")
        {
            order.Sort((a, b) =>
            {
                int byNumber = Trailing(ids[b]).CompareTo(Trailing(ids[a]));
                return byNumber != 0 ? byNumber : a.CompareTo(b);
            });
        }

        return order;
    }

    private static long Trailing(JsonNode? id)
    {
        string text = JsonNodes.Text(id) ?? string.Empty;
        int start = text.Length;
        while (start > 0 && char.IsAsciiDigit(text[start - 1]))
        {
            start--;
        }

        return start < text.Length && long.TryParse(text[start..], out long number) ? number : -1;
    }

    /// <summary>
    /// The verb call sent to the mod as it is, with the hub's options taken out: a file path read into the option it
    /// fills, the shaping as the call's shape, and a job the reply names waited for when the what takes max_wait.
    /// </summary>
    private async Task<VerbReply> ModAsync(VerbCall call, WhatEntry what, JsonObject options, CancellationToken cancellation)
    {
        if (ReadFiles(what, options, out JsonObject sent) is { } unreadable)
        {
            return unreadable;
        }

        (JsonElement? shape, Unparsed unparsed) = Shape(options);
        GameReply reply = await SendAsync(what, sent, call.Stream, shape, cancellation, call).ConfigureAwait(false);
        if (reply is not GameReply.Ok ok)
        {
            return Failure(reply);
        }

        JsonNode? result = JsonNodes.FromElement(ok.Result);
        if (what.Options.ContainsKey("max_wait"))
        {
            (result, VerbReply.Failed? pollFailed) =
                await SettledAsync(what, options, result, call.Stream, cancellation).ConfigureAwait(false);
            if (pollFailed != null)
            {
                return pollFailed;
            }
        }

        if (unparsed.Fields.Count > 0 || unparsed.Omit.Count > 0)
        {
            result = JsonNodes.FromElement(SidecarArguments.WithUnmatched(JsonNodes.ToElement(result), unparsed));
        }

        return new VerbReply.Done(result);
    }

    // The options a write is sent with: confirm true to act, no confirm to report (a what with no confirm acts as sent).
    private static JsonObject Confirmed(WhatEntry what, JsonObject options, bool acting)
    {
        JsonObject sent = (JsonObject)options.DeepClone();
        if (acting && what.Options.ContainsKey("confirm"))
        {
            sent["confirm"] = true;
        }
        else
        {
            sent.Remove("confirm");
        }

        return sent;
    }

    /// <summary>A refused or unreachable reply as a failure.</summary>
    internal static VerbReply.Failed Failure(GameReply reply) => reply switch
    {
        GameReply.Refused refused => new VerbReply.Failed(refused.Code, refused.Message,
            data: refused.Data is { } data ? JsonNodes.FromElement(data) : null),
        GameReply.Unreachable unreachable => new VerbReply.Failed(VerbFailure.GameUnavailable, unreachable.Message,
            maybeRan: unreachable.MaybeRan),
        _ => throw new InvalidOperationException("Only a refused or unreachable reply is a failure.")
    };

    /// <summary>
    /// A job the reply names (or inspect job's own id) is waited for until it is final or max_wait (default 30 s)
    /// passes: on the jobs topic (IGameLink.WaitJobAsync), then one inspect job for its view; a link without job waits
    /// is polled instead. A job still running answers its job id with running: true.
    /// </summary>
    private async Task<(JsonNode? Result, VerbReply.Failed? Failed)> SettledAsync(WhatEntry what, JsonObject options,
        JsonNode? result, long stream, CancellationToken cancellation)
    {
        double seconds = JsonNodes.Number(options["max_wait"]) ?? DefaultMaxWaitSeconds;
        string? jobId = JsonNodes.Text(result?["job_id"]) ??
                        (what.Key == "inspect.job" ? JsonNodes.Text(options["id"])?.Trim() : null);
        if (jobId == null || Settled(result))
        {
            return (result, null);
        }

        WhatEntry job = _catalogue.Verb("inspect")!.What("job")!;
        JsonObject poll = new() { ["id"] = jobId };
        if (what.Key == "inspect.job" && options["verbose"] is { } verbose)
        {
            poll["verbose"] = verbose.DeepClone();
        }

        if (seconds > 0)
        {
            GameReply? polled;
            try
            {
                // Null when the timeout passes, and early after a reconnect: either way one poll gives the job's view.
                await _link.WaitJobAsync(jobId, TimeSpan.FromSeconds(seconds), cancellation).ConfigureAwait(false);
                polled = await SendAsync(job, poll, stream, null, cancellation).ConfigureAwait(false);
            }
            catch (NotSupportedException)
            {
                polled = await PolledAsync(job, poll, result, stream, seconds, cancellation).ConfigureAwait(false);
            }

            switch (polled)
            {
                case GameReply.Ok ok:
                    result = JsonNodes.FromElement(ok.Result);
                    break;
                case not null:
                    // A poll the mod refuses (a dropped job, a broken link) is the answer, not the stale view.
                    return (result, Failure(polled));
            }
        }

        if (!Settled(result) && result is JsonObject running)
        {
            running["job_id"] ??= jobId;
            running["running"] = true;
        }

        return (result, null);
    }

    // A link without job waits: poll until the job is final or the time is up; the last poll's reply (null: none).
    private async Task<GameReply?> PolledAsync(WhatEntry job, JsonObject poll, JsonNode? result, long stream, double seconds,
        CancellationToken cancellation)
    {
        Stopwatch waited = Stopwatch.StartNew();
        GameReply? last = null;
        while (!Settled(result) && waited.Elapsed.TotalSeconds + PollInterval.TotalSeconds <= seconds)
        {
            await Task.Delay(PollInterval, cancellation).ConfigureAwait(false);
            last = await SendAsync(job, poll, stream, null, cancellation).ConfigureAwait(false);
            if (last is not GameReply.Ok polled)
            {
                break;
            }

            result = JsonNodes.FromElement(polled.Result);
        }

        return last;
    }

    private static bool Settled(JsonNode? result) =>
        JsonNodes.Text(result?["status"]) is not { } status || Array.IndexOf(Final, status) >= 0;

    /// <summary>
    /// A file option (name_file, a path on this machine) goes as the text of the option it names (code_file into
    /// code), refused in the caller's names; the options are copied, the caller's left as they were.
    /// </summary>
    private static VerbReply? ReadFiles(WhatEntry what, JsonObject options, out JsonObject sent)
    {
        sent = (JsonObject)options.DeepClone();
        foreach (string name in what.OptionNames)
        {
            if (!name.EndsWith("_file", StringComparison.Ordinal) || sent[name] is not { } path)
            {
                continue;
            }

            string into = name[..^"_file".Length];
            if (!what.Options.ContainsKey(into))
            {
                continue;
            }

            JsonObject given = new() { [name] = path.DeepClone() };
            if (sent[into] is { } inline)
            {
                given[into] = inline.DeepClone();
            }

            FileArgument file = new(name, into, $"A file whose text is sent as {into}.");
            switch (FileArguments.Read(JsonNodes.ToElement(given), [file]))
            {
                case FileRead.Refused refused:
                    return OptionProblem.Refusal(what.Label, [new OptionProblem(name, refused.Message)]);
                case FileRead.Ready ready:
                    sent.Remove(name);
                    sent[into] = ready.Arguments.GetProperty(into).GetString();
                    break;
            }
        }

        return null;
    }

    // fields, omit and list_limits go to the mod as the call's shape; selectors that cannot parse are reported unmatched.
    private static (JsonElement? Shape, Unparsed Unparsed) Shape(JsonObject options)
    {
        JsonElement? fields = Given(options, SidecarArguments.FieldsArgument);
        JsonElement? omit = Given(options, SidecarArguments.OmitArgument);
        JsonElement? limits = Given(options, SidecarArguments.LimitsArgument);
        return fields == null && omit == null && limits == null
            ? (null, new Unparsed([], []))
            : new SidecarArguments(default, fields, new OutputChoice.Inline(), omit, limits).Shape();
    }

    private static JsonElement? Given(JsonObject options, string name) =>
        options[name] is { } value ? JsonNodes.ToElement(value) : null;

    /// <summary>The wire params of a verb call: {what, ...options} without the hub's own options.</summary>
    internal static JsonObject WireParams(WhatEntry what, JsonObject options)
    {
        JsonObject parameters = new();
        if (what.Name != VerbCatalogue.WhatlessKey)
        {
            parameters["what"] = what.Name;
        }

        foreach ((string name, JsonNode? value) in options)
        {
            if (value != null && name != "what" && Array.IndexOf(HubOptions, name) < 0)
            {
                parameters[name] = value.DeepClone();
            }
        }

        return parameters;
    }

    // Every call is unsettled until the mod's reply comes (OnReply, even a late one after the caller gave up) or the
    // link answers it itself; a call the caller cancelled stays unsettled until its late reply, or until it is old
    // enough that none will come. A reply that comes after the caller gave up reaches call.LateReply first and settles
    // the call after: what the reply tells the caller is in place before an undo may stop waiting for it.
    private async Task<GameReply> SendAsync(WhatEntry what, JsonObject options, long stream, JsonElement? shape,
        CancellationToken cancellation, VerbCall? call = null)
    {
        object token = new();
        lock (_worlds)
        {
            if (!_unsettled.TryGetValue(stream, out Dictionary<object, DateTime>? calls))
            {
                calls = new Dictionary<object, DateTime>(ReferenceEqualityComparer.Instance);
                _unsettled[stream] = calls;
            }

            calls[token] = DateTime.UtcNow;
        }

        void Settle()
        {
            lock (_worlds)
            {
                if (_unsettled.TryGetValue(stream, out Dictionary<object, DateTime>? calls))
                {
                    calls.Remove(token);
                }
            }
        }

        bool awaited = true;
        void Answered(GameReply answer)
        {
            try
            {
                if (!awaited && call?.LateReply is { } late)
                {
                    late(answer is GameReply.Ok ok ? JsonNodes.FromElement(ok.Result) : null);
                }
            }
            finally
            {
                Settle();
            }
        }

        GameReply reply;
        try
        {
            reply = await _link.CallAsync(new GameCall(what.Verb, JsonNodes.ToElement(WireParams(what, options)), stream)
            {
                World = WorldOf(stream),
                Shape = shape,
                OnReply = Answered
            }, cancellation).ConfigureAwait(false);
        }
        finally
        {
            awaited = false;
        }

        Settle();
        return reply;
    }

    /// <summary>
    /// True while a call on the stream may still reach the game: sent, not answered, and younger than SettleBound (a
    /// cancelled call keeps its slot at the link until its late reply).
    /// </summary>
    internal bool Unsettled(long stream)
    {
        lock (_worlds)
        {
            if (!_unsettled.TryGetValue(stream, out Dictionary<object, DateTime>? calls))
            {
                return false;
            }

            foreach (DateTime sent in calls.Values)
            {
                if (DateTime.UtcNow - sent < SettleBound)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>How long a call's late reply is waited for before it is taken as never coming.</summary>
    internal TimeSpan SettleBound { get; init; } = TimeSpan.FromMinutes(2);
}
