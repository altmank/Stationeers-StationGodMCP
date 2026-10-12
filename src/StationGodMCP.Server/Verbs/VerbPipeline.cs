using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using StationGodMCP.Client;

namespace StationGodMCP.Server;

/// <summary>
/// The verb pipeline every caller goes through (docs/architecture/hub.md): record the call, resolve a repeat and ref
/// targets, check it (OptionCheck), let the write gate decide on a write with its resolved targets (act, report by the
/// what's report class, refuse, stop), route an id list by the what's x-list (native: the handler takes it; split: one
/// call per id, gathered into rows; one: refused, naming run_script), translate onto the mod's handlers
/// (LegacyVerbMap) and send each call on the caller's stream with its rename envelope. Direct replies get refs and the
/// cap; script and program list replies get a hub cursor. help is answered from the generated pages; run_script and
/// script runs are the runs piece's.
/// </summary>
public sealed partial class VerbPipeline
{
    internal const string RunScriptPlaceholder = "run_script lands with the runs piece";

    private const double DefaultMaxWaitSeconds = 30;

    // The mod's final job states (jobs: applied, applied_with_differences, applied_unchecked, stopped, refused; gas
    // moves: applied, failed); queued, waiting, scheduled and verifying go on.
    private static readonly string[] Final =
        ["applied", "applied_with_differences", "applied_unchecked", "stopped", "refused", "failed"];

    private readonly IGameLink _link;
    private readonly IWriteGate? _gate;
    private readonly DirectWriteGate _directGate;
    private readonly CallRecorder? _recorder;
    private readonly VerbCatalogue _catalogue;
    private readonly HandlerSchemas _schemas;
    private readonly LegacyVerbMap _map;
    private readonly IShownText? _shown;
    private readonly RefStore _refs = new();
    private readonly BulkWrites _bulk = new();
    private readonly HubCursors _cursors = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _inFlight;

    /// <summary>
    /// gate, when given, decides every write that does not carry its own (VerbCall.Gate); otherwise the direct and
    /// program faces get the direct write rule and a script call without a gate reports.
    /// </summary>
    internal VerbPipeline(IGameLink link, IWriteGate? gate = null, CallRecorder? recorder = null,
        VerbCatalogue? catalogue = null, HandlerSchemas? schemas = null, IShownText? shown = null)
    {
        _link = link;
        _gate = gate;
        _recorder = recorder;
        _shown = shown;
        _catalogue = catalogue ?? VerbCatalogue.Embedded;
        _schemas = schemas ?? HandlerSchemas.BuiltIn;
        _map = new LegacyVerbMap(_catalogue, _schemas);
        _directGate = new DirectWriteGate(_catalogue);
        _link.WorldChanged += OnWorldChanged;
    }

    internal VerbCatalogue Catalogue => _catalogue;

    internal LegacyVerbMap Map => _map;

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
    public void EndStream(long stream) => _cursors.FreeStream(stream);

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
            return new VerbReply.Failed(VerbFailure.NotAvailable, RunScriptPlaceholder + ".");
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

        Dictionary<string, string> refs = new(StringComparer.Ordinal);
        if (ResolveRefs(what!, call.Args, refs, out JsonObject resolved) is { } unknown)
        {
            return unknown;
        }

        JsonObject options = Normalised(what!, call.Face, resolved);
        if (BulkJob(call, what!, options) is { } bulk)
        {
            return await bulk.ConfigureAwait(false);
        }

        JsonArray? ids = what!.Options.ContainsKey("id") ? options["id"] as JsonArray : null;
        if (ids != null && what.ListForm == "one")
        {
            return OptionProblem.Refusal(what.Label, [new OptionProblem("id", OptionCheck.OneId(what))]);
        }

        VerbReply reply = what.Write != null
            ? await WriteAsync(call, what, options, ids, refs, cancellation).ConfigureAwait(false)
            : await ReadAsync(call, what, options, ids, cancellation).ConfigureAwait(false);
        return call.Face == Face.Direct ? Capped(call, what, options, reply) : reply;
    }

    // On the script and program faces a what that takes an id list answers rows even for one id (D13).
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

    // A read: split per id, a script or program list page with a hub cursor, or the translation as it is.
    private async Task<VerbReply> ReadAsync(VerbCall call, WhatEntry what, JsonObject options, JsonArray? ids,
        CancellationToken cancellation)
    {
        if (ids != null && what.ListForm == "split")
        {
            return await SplitAsync(call, what, options, ids, null, cancellation).ConfigureAwait(false);
        }

        Translation translation = _map.Translate(what, options);
        if (call.Face != Face.Direct && what.MainList != null && options["limit"] == null &&
            translation is Translation.Calls { List.Count: 1 } single)
        {
            return await FirstPageAsync(call, what, options, single.List[0], cancellation).ConfigureAwait(false);
        }

        return await ExecuteAsync(translation, call, what, options, null, cancellation).ConfigureAwait(false);
    }

    // split: one call per id, gathered as PLAN's rows {index, ok, reference_id, payload | error}.
    private async Task<VerbReply> SplitAsync(VerbCall call, WhatEntry what, JsonObject options, JsonArray ids, bool? acting,
        CancellationToken cancellation)
    {
        JsonArray rows = [];
        int succeeded = 0;
        for (int index = 0; index < ids.Count; index++)
        {
            JsonNode? id = ids[index];
            JsonObject one = (JsonObject)options.DeepClone();
            one["id"] = id?.DeepClone();
            VerbReply reply = await ExecuteAsync(_map.Translate(what, one), call, what, one, acting, cancellation)
                .ConfigureAwait(false);
            JsonObject row = new() { ["index"] = index };
            switch (reply)
            {
                case VerbReply.Done done:
                    succeeded++;
                    row["ok"] = true;
                    row["reference_id"] = id?.DeepClone();
                    row["payload"] = done.Reply?.DeepClone();

                    break;
                case VerbReply.Failed failed:
                    row["ok"] = false;
                    row["reference_id"] = id?.DeepClone();
                    row["error"] = failed.ToJson();
                    break;
            }

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

    // acting: null for a read; for a write, true sends it to act and false to report.
    private async Task<VerbReply> ExecuteAsync(Translation translation, VerbCall call, WhatEntry what, JsonObject options,
        bool? acting, CancellationToken cancellation)
    {
        switch (translation)
        {
            case Translation.Answered answered:
                return answered.Reply;
            case Translation.Probe probe:
            {
                GameReply read = await SendAsync(probe.Read, call.Stream, null, cancellation).ConfigureAwait(false);
                return await ExecuteAsync(probe.Then(read), call, what, options, acting, cancellation).ConfigureAwait(false);
            }
            case Translation.Calls calls when calls.List.Count == 1:
                return await RunCallAsync(calls.List[0], call, what, options, acting, cancellation).ConfigureAwait(false);
            case Translation.Calls calls:
                return await PartsAsync(calls.List, call, what, options, acting, cancellation).ConfigureAwait(false);
            default:
                throw new InvalidOperationException("A translation is calls, a probe or an answer.");
        }
    }

    // Several parts in order, one key each; the first failure stops the rest, named in not_run.
    private async Task<VerbReply> PartsAsync(List<HandlerCall> calls, VerbCall call, WhatEntry what, JsonObject options,
        bool? acting, CancellationToken cancellation)
    {
        JsonObject reply = new();
        for (int index = 0; index < calls.Count; index++)
        {
            HandlerCall part = calls[index];
            VerbReply answered = await RunCallAsync(part, call, what, options, acting, cancellation).ConfigureAwait(false);
            if (answered is VerbReply.Done done)
            {
                reply[part.Part!] = done.Reply?.DeepClone();
                continue;
            }

            VerbReply.Failed failed = (VerbReply.Failed)answered;
            JsonArray notRun = [];
            for (int rest = index + 1; rest < calls.Count; rest++)
            {
                notRun.Add(calls[rest].Part);
            }

            if (index == 0)
            {
                return notRun.Count == 0
                    ? failed
                    : new VerbReply.Failed(failed.Code, $"{failed.Message} Not run: {Joined(notRun)}.", failed.Fix,
                        failed.Data, failed.MaybeRan, failed.Stopped, failed.PassEnd);
            }

            reply[part.Part!] = failed.ToJson();
            reply["not_run"] = notRun;
            return new VerbReply.Done(reply);
        }

        return new VerbReply.Done(reply);
    }

    private static string Joined(JsonArray parts)
    {
        List<string> names = [];
        foreach (JsonNode? part in parts)
        {
            names.Add(JsonNodes.Text(part) ?? string.Empty);
        }

        return string.Join(", ", names);
    }

    private async Task<VerbReply> RunCallAsync(HandlerCall handlerCall, VerbCall call, WhatEntry what, JsonObject options,
        bool? acting, CancellationToken cancellation)
    {
        if (ReadFiles(handlerCall, what) is { } unreadable)
        {
            return unreadable;
        }

        if (acting is { } acts)
        {
            ApplyMode(handlerCall, acts);
            ApplyMode(handlerCall.Fallback, acts);
        }

        (JsonElement? shape, Unparsed unparsed) = Shape(options);
        GameReply reply = await SendAsync(handlerCall, call.Stream, shape, cancellation).ConfigureAwait(false);
        if (reply is GameReply.Refused refusedFirst && handlerCall.Fallback is { } fallback &&
            refusedFirst.Code == handlerCall.FallbackCode)
        {
            reply = await SendAsync(fallback, call.Stream, shape, cancellation).ConfigureAwait(false);
        }

        if (reply is GameReply.Ok ok)
        {
            JsonNode? result = JsonNodes.FromElement(ok.Result);
            if (what.Options.ContainsKey("max_wait"))
            {
                (result, VerbReply.Failed? pollFailed) =
                    await SettledAsync(handlerCall, result, call.Stream, options, cancellation).ConfigureAwait(false);
                if (pollFailed != null)
                {
                    return pollFailed;
                }
            }

            if (handlerCall.Reshape != null)
            {
                result = handlerCall.Reshape(result);
            }

            if (unparsed.Fields.Count > 0 || unparsed.Omit.Count > 0)
            {
                result = JsonNodes.FromElement(SidecarArguments.WithUnmatched(JsonNodes.ToElement(result), unparsed));
            }

            return new VerbReply.Done(result);
        }

        return Failure(reply, handlerCall, what);
    }

    /// <summary>
    /// The gate's choice on the handler call: dry_run false and confirm true to act, dry_run true and no confirm to
    /// report (the handler's own report). A poll, a status read or an undo form carries neither.
    /// </summary>
    private void ApplyMode(HandlerCall? handlerCall, bool acting)
    {
        if (handlerCall == null || handlerCall.Args["job_id"] != null || handlerCall.Args["status"] != null ||
            handlerCall.Args["undo"] != null)
        {
            return;
        }

        if (_schemas.Arg(handlerCall.Handler, "dry_run") != null)
        {
            handlerCall.Args["dry_run"] = !acting;
        }

        if (_schemas.Arg(handlerCall.Handler, "confirm") != null)
        {
            if (acting)
            {
                handlerCall.Args["confirm"] = true;
            }
            else
            {
                handlerCall.Args.Remove("confirm");
            }
        }
    }

    internal static VerbReply.Failed Failure(GameReply reply) => Failure(reply, null, null);

    /// <summary>
    /// A refused or unreachable reply as a failure. The mod words its refusals in the caller's names (the rename
    /// envelope); the link's own unreachable text names the method it sent, so that is said as the what.
    /// </summary>
    internal static VerbReply.Failed Failure(GameReply reply, HandlerCall? sent, WhatEntry? what) => reply switch
    {
        GameReply.Refused refused => new VerbReply.Failed(refused.Code, refused.Message,
            data: refused.Data is { } data ? JsonNodes.FromElement(data) : null),
        GameReply.Unreachable unreachable => new VerbReply.Failed(VerbFailure.GameUnavailable,
            Unnamed(unreachable.Message, sent, what), maybeRan: unreachable.MaybeRan),
        _ => throw new InvalidOperationException("Only a refused or unreachable reply is a failure.")
    };

    private static string Unnamed(string message, HandlerCall? sent, WhatEntry? what) =>
        sent == null ? message : message.Replace(sent.Handler, what?.Label ?? "the call", StringComparison.Ordinal);

    /// <summary>
    /// A job the reply names is waited for until it is final or max_wait (default 30 s) passes: on the jobs topic
    /// (IGameLink.WaitJobAsync), then one poll for its view; a link without job waits is polled instead. A job still
    /// running answers its job id with running: true.
    /// </summary>
    private async Task<(JsonNode? Result, VerbReply.Failed? Failed)> SettledAsync(HandlerCall handlerCall,
        JsonNode? result, long stream, JsonObject options, CancellationToken cancellation)
    {
        double seconds = JsonNodes.Number(options["max_wait"]) ?? DefaultMaxWaitSeconds;
        string? jobId = JsonNodes.Text(result?["job_id"]) ?? JsonNodes.Text(handlerCall.Args["job_id"]);
        HandlerCall? poll = handlerCall.PollsJob
            ? handlerCall
            : jobId != null && LegacyVerbMap.JobPoller(jobId) is { } poller
                ? new HandlerCall(poller, new JsonObject { ["job_id"] = jobId }, handlerCall.Rename)
                : null;
        if (poll == null || jobId == null || Settled(result))
        {
            return (result, null);
        }

        if (seconds > 0)
        {
            GameReply? polled;
            try
            {
                // Null when the timeout passes, and early after a reconnect: either way one poll gives the job's view.
                await _link.WaitJobAsync(jobId, TimeSpan.FromSeconds(seconds), cancellation).ConfigureAwait(false);
                polled = await SendAsync(poll, stream, null, cancellation).ConfigureAwait(false);
            }
            catch (NotSupportedException)
            {
                polled = await PolledAsync(poll, result, stream, seconds, cancellation).ConfigureAwait(false);
            }

            switch (polled)
            {
                case GameReply.Ok ok:
                    result = JsonNodes.FromElement(ok.Result);
                    break;
                case not null:
                    // A poll the mod refuses (a dropped job, a broken link) is the answer, not the stale view.
                    return (result, Failure(polled, poll, null));
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
    private async Task<GameReply?> PolledAsync(HandlerCall poll, JsonNode? result, long stream, double seconds,
        CancellationToken cancellation)
    {
        Stopwatch waited = Stopwatch.StartNew();
        GameReply? last = null;
        while (!Settled(result) && waited.Elapsed.TotalSeconds + PollInterval.TotalSeconds <= seconds)
        {
            await Task.Delay(PollInterval, cancellation).ConfigureAwait(false);
            last = await SendAsync(poll, stream, null, cancellation).ConfigureAwait(false);
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

    // x-file-arguments: the file's text goes as the argument it fills, refused in the caller's names.
    private static VerbReply? ReadFiles(HandlerCall handlerCall, WhatEntry what)
    {
        foreach (VerbFileArgument file in handlerCall.Files)
        {
            if (handlerCall.Args[file.Handler.Name] is not { } path)
            {
                continue;
            }

            JsonObject given = new() { [file.Option] = path.DeepClone() };
            if (handlerCall.Args[file.Handler.Into] is { } inline)
            {
                given[file.Into] = inline.DeepClone();
            }

            FileArgument named = new(file.Option, file.Into, file.Handler.Description);
            switch (FileArguments.Read(JsonNodes.ToElement(given), [named]))
            {
                case FileRead.Refused refused:
                    return OptionProblem.Refusal(what.Label, [new OptionProblem(file.Option, refused.Message)]);
                case FileRead.Ready ready:
                    handlerCall.Args.Remove(file.Handler.Name);
                    handlerCall.Args[file.Handler.Into] = ready.Arguments.GetProperty(file.Into).GetString();
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

    private Task<GameReply> SendAsync(HandlerCall handlerCall, long stream, JsonElement? shape,
        CancellationToken cancellation) =>
        _link.CallAsync(new GameCall(handlerCall.Handler, JsonNodes.ToElement(handlerCall.Args), stream)
        {
            Shape = shape,
            Rename = handlerCall.Rename
        }, cancellation);
}
