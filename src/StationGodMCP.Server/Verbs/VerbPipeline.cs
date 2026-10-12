using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using StationGodMCP.Client;

namespace StationGodMCP.Server;

/// <summary>
/// The verb pipeline every caller goes through (docs/architecture/hub.md): record the call, check it (OptionCheck), let
/// the write gate decide on a write with its resolved targets, route an id list by the what's x-list (native: the
/// handler takes it; split: one call per id, gathered into rows; one: refused, naming run_script), translate onto
/// today's handlers (LegacyVerbMap) and send each call on the caller's stream with its rename envelope. help is answered
/// from the generated pages; run_script and script runs are the runs piece's.
/// </summary>
public sealed class VerbPipeline
{
    internal const string RunScriptPlaceholder = "run_script lands with the runs piece";

    private const double DefaultMaxWaitSeconds = 30;

    // Job states that are not final: the poll goes on while one of these is reported.
    private static readonly string[] Unsettled = ["queued", "waiting", "scheduled", "running", "started", "pending"];

    private readonly IGameLink _link;
    private readonly IWriteGate _gate;
    private readonly CallRecorder? _recorder;
    private readonly VerbCatalogue _catalogue;
    private readonly LegacyVerbMap _map;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _inFlight;

    internal VerbPipeline(IGameLink link, IWriteGate? gate = null, CallRecorder? recorder = null,
        VerbCatalogue? catalogue = null, HandlerSchemas? schemas = null)
    {
        _link = link;
        _gate = gate ?? PassThroughGate.Instance;
        _recorder = recorder;
        _catalogue = catalogue ?? VerbCatalogue.Embedded;
        _map = new LegacyVerbMap(_catalogue, schemas ?? HandlerSchemas.BuiltIn);
    }

    internal VerbCatalogue Catalogue => _catalogue;

    internal LegacyVerbMap Map => _map;

    /// <summary>How long a job poll waits between polls.</summary>
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    public async Task<VerbReply> CallAsync(VerbCall call, CancellationToken cancellation)
    {
        int inFlight = Interlocked.Increment(ref _inFlight);
        try
        {
            _recorder?.Record(_clock.ElapsedMilliseconds, call.Face, call.Stream, inFlight, call.Verb, call.What, call.Args);
            return await RunAsync(call, cancellation).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
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
            call = new VerbCall(call.Verb, null, helpArgs, call.Face, call.Stream, call.Line);
        }

        if (OptionCheck.Check(_catalogue, call, out VerbEntry? verb, out WhatEntry? what) is { } refused)
        {
            return refused;
        }

        if (verb!.Name == "help")
        {
            return HelpPages.Answer(_catalogue, call.Args);
        }

        if (call.Args["repeat"] != null)
        {
            return new VerbReply.Failed(VerbFailure.NotAvailable, "repeat lands with the refs piece.");
        }

        if (RefGiven(call.Args["id"]))
        {
            return new VerbReply.Failed(VerbFailure.NotAvailable, "refs (\"r3\") land with the refs piece; give the ids.");
        }

        JsonObject options = Normalised(what!, call);
        JsonArray? ids = what!.Options.ContainsKey("id") ? options["id"] as JsonArray : null;
        if (ids != null && what.ListForm == "one")
        {
            return OptionProblem.Refusal(what.Label, [new OptionProblem("id", OptionCheck.OneId(what))]);
        }

        if (what.Write != null && Gate(call, what, options) is { } decided)
        {
            return decided;
        }

        if (ids != null && what.ListForm == "split")
        {
            return await SplitAsync(call, what, options, ids, cancellation).ConfigureAwait(false);
        }

        return await ExecuteAsync(_map.Translate(what, options), call, what, options, cancellation).ConfigureAwait(false);
    }

    // On the script and program faces a what that takes an id list answers rows even for one id (D13).
    private static JsonObject Normalised(WhatEntry what, VerbCall call)
    {
        JsonObject options = new();
        foreach ((string name, JsonNode? value) in call.Args)
        {
            if (value != null)
            {
                options[name] = value.DeepClone();
            }
        }

        if (call.Face != Face.Direct && what.TakesIdList && JsonNodes.Text(options["id"]) is { } one)
        {
            options["id"] = new JsonArray(JsonValue.Create(one));
        }

        return options;
    }

    // A ref is r and digits, with a path after a dot ("r12.things"); ids are digits or words.
    private static bool RefGiven(JsonNode? id)
    {
        if (id is JsonArray list)
        {
            foreach (JsonNode? entry in list)
            {
                if (RefGiven(entry))
                {
                    return true;
                }
            }

            return false;
        }

        string? text = JsonNodes.Text(id)?.Trim();
        if (text is not { Length: > 1 } || text[0] != 'r' || !char.IsAsciiDigit(text[1]))
        {
            return false;
        }

        int index = 1;
        while (index < text.Length && char.IsAsciiDigit(text[index]))
        {
            index++;
        }

        return index == text.Length || text[index] == '.';
    }

    // The one place the write gate is asked, with the targets the what's x-target paths resolve to.
    private VerbReply? Gate(VerbCall call, WhatEntry what, JsonObject options)
    {
        WriteTargets targets = WriteTargetPaths.Resolve(what.Write!, options);
        switch (_gate.Decide(call, targets))
        {
            case WriteDecision.Act:
                return null;
            case WriteDecision.Report:
                return new VerbReply.Failed(VerbFailure.NotAvailable, "Reports land with the write rule piece.");
            case WriteDecision.Refuse refuse:
                return new VerbReply.Failed("refused", $"{what.Label} was refused by the write rule.",
                    data: new JsonObject { ["rows"] = new JsonArray(refuse.Row.DeepClone()) });
            case WriteDecision.Stop stop:
                return new VerbReply.Failed("unseen_target", $"{what.Label} names targets not seen; nothing acted.",
                    stop.Fix, new JsonObject { ["rows"] = stop.Rows.DeepClone() }, stopped: true);
            default:
                throw new InvalidOperationException("A write decision is act, report, refuse or stop.");
        }
    }

    // split: one call per id, gathered as PLAN's rows {index, ok, reference_id, ...reply | error}.
    private async Task<VerbReply> SplitAsync(VerbCall call, WhatEntry what, JsonObject options, JsonArray ids,
        CancellationToken cancellation)
    {
        JsonArray rows = [];
        int succeeded = 0;
        for (int index = 0; index < ids.Count; index++)
        {
            JsonNode? id = ids[index];
            JsonObject one = (JsonObject)options.DeepClone();
            one["id"] = id?.DeepClone();
            VerbReply reply = await ExecuteAsync(_map.Translate(what, one), call, what, one, cancellation)
                .ConfigureAwait(false);
            JsonObject row = new() { ["index"] = index };
            switch (reply)
            {
                case VerbReply.Done done:
                    succeeded++;
                    row["ok"] = true;
                    row["reference_id"] = id?.DeepClone();
                    if (done.Reply is JsonObject fields)
                    {
                        foreach ((string key, JsonNode? value) in fields)
                        {
                            if (key is not ("index" or "ok" or "reference_id"))
                            {
                                row[key] = value?.DeepClone();
                            }
                        }
                    }
                    else
                    {
                        row["payload"] = done.Reply?.DeepClone();
                    }

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

    private async Task<VerbReply> ExecuteAsync(Translation translation, VerbCall call, WhatEntry what, JsonObject options,
        CancellationToken cancellation)
    {
        switch (translation)
        {
            case Translation.Answered answered:
                return answered.Reply;
            case Translation.Probe probe:
            {
                GameReply read = await SendAsync(probe.Read, call, null, cancellation).ConfigureAwait(false);
                return await ExecuteAsync(probe.Then(read), call, what, options, cancellation).ConfigureAwait(false);
            }
            case Translation.Calls calls when calls.List.Count == 1:
                return await RunCallAsync(calls.List[0], call, what, options, cancellation).ConfigureAwait(false);
            case Translation.Calls calls:
                return await PartsAsync(calls.List, call, what, options, cancellation).ConfigureAwait(false);
            default:
                throw new InvalidOperationException("A translation is calls, a probe or an answer.");
        }
    }

    // Several parts in order, one key each; the first failure stops the rest, named in not_run.
    private async Task<VerbReply> PartsAsync(List<HandlerCall> calls, VerbCall call, WhatEntry what, JsonObject options,
        CancellationToken cancellation)
    {
        JsonObject reply = new();
        for (int index = 0; index < calls.Count; index++)
        {
            HandlerCall part = calls[index];
            VerbReply answered = await RunCallAsync(part, call, what, options, cancellation).ConfigureAwait(false);
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
        CancellationToken cancellation)
    {
        if (ReadFiles(handlerCall, what) is { } unreadable)
        {
            return unreadable;
        }

        (JsonElement? shape, Unparsed unparsed) = Shape(options);
        GameReply reply = await SendAsync(handlerCall, call, shape, cancellation).ConfigureAwait(false);
        if (reply is GameReply.Refused refusedFirst && handlerCall.Fallback is { } fallback &&
            refusedFirst.Code == handlerCall.FallbackCode)
        {
            reply = await SendAsync(fallback, call, shape, cancellation).ConfigureAwait(false);
        }

        if (reply is GameReply.Ok ok)
        {
            JsonNode? result = JsonNodes.FromElement(ok.Result);
            if (what.Options.ContainsKey("max_wait"))
            {
                result = await SettledAsync(handlerCall, result, call, options, cancellation).ConfigureAwait(false);
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

        return Failure(reply);
    }

    internal static VerbReply.Failed Failure(GameReply reply) => reply switch
    {
        GameReply.Refused refused => new VerbReply.Failed(refused.Code, refused.Message,
            data: refused.Data is { } data ? JsonNodes.FromElement(data) : null),
        GameReply.Unreachable unreachable => new VerbReply.Failed(VerbFailure.GameUnavailable, unreachable.Message,
            maybeRan: unreachable.MaybeRan),
        _ => throw new InvalidOperationException("Only a refused or unreachable reply is a failure.")
    };

    /// <summary>
    /// A job the reply names is polled until it is final or max_wait (default 30 s) passes: a job poll itself, or a
    /// confirmed write that answered with a job_id still queued or running.
    /// </summary>
    private async Task<JsonNode?> SettledAsync(HandlerCall handlerCall, JsonNode? result, VerbCall call, JsonObject options,
        CancellationToken cancellation)
    {
        double seconds = JsonNodes.Number(options["max_wait"]) ?? DefaultMaxWaitSeconds;
        string? jobId = JsonNodes.Text(result?["job_id"]);
        HandlerCall? poll = handlerCall.PollsJob
            ? handlerCall
            : jobId != null && LegacyVerbMap.JobPoller(jobId) is { } poller
                ? new HandlerCall(poller, new JsonObject { ["job_id"] = jobId }, handlerCall.Rename)
                : null;
        if (poll == null || seconds <= 0)
        {
            return result;
        }

        Stopwatch waited = Stopwatch.StartNew();
        while (!Settled(result) && waited.Elapsed.TotalSeconds + PollInterval.TotalSeconds <= seconds)
        {
            await Task.Delay(PollInterval, cancellation).ConfigureAwait(false);
            if (await SendAsync(poll, call, null, cancellation).ConfigureAwait(false) is not GameReply.Ok polled)
            {
                break;
            }

            result = JsonNodes.FromElement(polled.Result);
        }

        return result;
    }

    private static bool Settled(JsonNode? result) =>
        JsonNodes.Text(result?["status"]) is not { } status || Array.IndexOf(Unsettled, status) < 0;

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

    private Task<GameReply> SendAsync(HandlerCall handlerCall, VerbCall call, JsonElement? shape,
        CancellationToken cancellation) =>
        _link.CallAsync(new GameCall(handlerCall.Handler, JsonNodes.ToElement(handlerCall.Args), call.Stream)
        {
            Shape = shape,
            Rename = handlerCall.Rename
        }, cancellation);
}
