using System.Globalization;
using System.Text.Json.Nodes;
using StationGodMCP.Client;

namespace StationGodMCP.Server;

/// <summary>How runs are timed and started: the reply waits, the kill after a stop, the Python and sg it uses.</summary>
public sealed class RunHostSettings
{
    /// <summary>The longest a confirmed run's reply waits (--confirmed-ceiling-s); zero for no ceiling.</summary>
    public TimeSpan ConfirmedCeiling { get; init; } = TimeSpan.FromSeconds(SidecarOptions.DefaultConfirmedCeilingSeconds);

    /// <summary>A reply's wait when max_wait is not given: the client's limit.</summary>
    public TimeSpan DefaultMaxWait { get; init; } = TimeSpan.FromSeconds(600);

    /// <summary>inspect job's wait on a live run when max_wait is not given.</summary>
    public TimeSpan InspectWait { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan KillAfter { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan KillLatest { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How often a long run_script wait sends notifications/progress.</summary>
    public TimeSpan ProgressEvery { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How often live runs' cost lines ride replies other than run_script, inspect job and server info.</summary>
    public TimeSpan CostLinesEvery { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// At session end (stdin EOF), how long only cleanup writes and hold exits are forwarded before session_end goes
    /// (hub.md, Session end). The client kills the hub about 0.4 s after EOF, so this stays well inside that.
    /// </summary>
    public TimeSpan CleanupWindow { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// How many written targets the hub keeps for undo over all its ended runs; past it the oldest runs' write logs
    /// are let go and their undo answers undo_expired, naming what it can no longer revert.
    /// </summary>
    public int UndoMemoryTargets { get; init; } = 100_000;

    /// <summary>How long adoption waits before it connects again with a resumed old token.</summary>
    public TimeSpan AdoptionReconnect { get; init; } = TimeSpan.FromSeconds(1);

    public IReadOnlyList<string> Python { get; init; } = OperatingSystem.IsWindows() ? ["py", "-3"] : ["python3"];

    /// <summary>The folder holding the sg package (next to the server binary by default).</summary>
    public string SgRoot { get; init; } = WorkerPool.ShippedSgRoot;

    public IRunJournal Journal { get; init; } = NoRunJournal.Instance;
}

/// <summary>What the MCP request carrying a run_script gives the wait: progress to send, and its cancellation.</summary>
internal sealed class ReplyHooks
{
    internal static readonly ReplyHooks None = new();

    /// <summary>Called with the seconds waited every ProgressEvery while the reply waits (when the request asked).</summary>
    internal Func<double, Task>? Progress { get; init; }

    /// <summary>Cancelled when the client cancels the request (notifications/cancelled): the run is stopped.</summary>
    internal CancellationToken Cancelled { get; init; }
}

/// <summary>Where a hub's session is: open, forwarding only cleanup after EOF, or past session_end.</summary>
internal enum SessionPhase
{
    Open,
    Cleanup,
    Ended
}

/// <summary>
/// The runs of one hub (docs/architecture/hub.md, Runs): run_script mints script-N on a new stream with a worker from
/// the pool; inspect job, server stop and undo for script-N and for runs adopted from earlier hubs' journals
/// (script-&lt;pid&gt;.N); the notices and cost lines that ride replies; a world change stops every live run and drops
/// its holds; at stdin EOF the session-end order. The seen set is the hub's, fed by the MCP adapter.
///
/// Lock order: RunHost._gate, then a RunRecord.Sync, then the leaf locks (SeenSet, RefStore, the pipeline's). Code
/// holding a record's lock never takes _gate (it reads Phase, which needs none).
/// </summary>
public sealed partial class RunHost : IAsyncDisposable
{
    private const string Prefix = "script-";

    private readonly IHubLink _link;
    private readonly VerbPipeline _pipeline;
    private readonly RunHostSettings _settings;
    private readonly WorkerPool _pool;
    private readonly object _gate = new();
    private readonly Dictionary<string, Notes> _runs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Notes> _order = [];
    private DateTime _lastCostLines = DateTime.MinValue;
    private int _next;
    // Read without a lock (Volatile): a run reads it under its record's lock, and the lock order is RunHost._gate before
    // any RunRecord.Sync, never the other way. Changed only under _gate.
    private int _phase = (int)SessionPhase.Open;

    internal RunHost(IHubLink link, VerbPipeline pipeline, SeenSet seen, RunHostSettings settings)
    {
        _link = link;
        _pipeline = pipeline;
        Seen = seen;
        _settings = settings;
        _pool = new WorkerPool(settings.Python, settings.SgRoot);
        _link.WorldChanged += OnWorldChanged;
    }

    /// <summary>The hub's seen set (hub.md, The seen set).</summary>
    public SeenSet Seen { get; }

    /// <summary>Where the session is; lock-free, so a run may read it while holding its record's lock.</summary>
    internal SessionPhase Phase => (SessionPhase)Volatile.Read(ref _phase);

    /// <summary>Starts the idle worker now, so the first run does not wait for Python.</summary>
    public void Warm() => _pool.Warm();

    /// <summary>A run's record by id (script-N, or script-&lt;pid&gt;.N adopted); null when there is none.</summary>
    public RunRecord? Find(string id)
    {
        lock (_gate)
        {
            return _runs.TryGetValue(id.Trim(), out Notes? notes) ? notes.Record : null;
        }
    }

    // ---- run_script -------------------------------------------------------------------------------------------------

    internal async Task<VerbReply> RunScriptAsync(JsonObject options, ReplyHooks hooks)
    {
        if (OptionCheck.Check(_pipeline.Catalogue, new VerbCall("run_script", null, options, Face.Direct), out _, out _) is
            { } refused)
        {
            return refused;
        }

        string? code = JsonNodes.Text(options["code"]);
        string? name = JsonNodes.Text(options["name"])?.Trim();
        if (code != null && name != null)
        {
            return OptionProblem.Refusal("run_script",
                [new OptionProblem("name", "not with code: give exactly one of code or name")]);
        }

        if (code == null && name == null)
        {
            return OptionProblem.Refusal("run_script", [new OptionProblem("code",
                "exactly one of code or name: code is inline Python, name a saved script (find script lists them)")]);
        }

        if (code is { } inline && inline.Trim().Length == 0)
        {
            return OptionProblem.Refusal("run_script", [new OptionProblem("code", "required: the Python to run")]);
        }

        if (code != null && options["args"] != null)
        {
            return OptionProblem.Refusal("run_script",
                [new OptionProblem("args", "with name only: the saved script's main(...) arguments")]);
        }

        if (options["output_file"] != null)
        {
            return OptionProblem.Refusal("run_script",
                [new OptionProblem("output_file", "not taken by run_script: its reply is text; print to a file in the code")]);
        }

        if (Phase != SessionPhase.Open)
        {
            return SessionEnded();
        }

        // A run is made for one world: before the first connection the link knows none, so it connects first; with no
        // world loaded (the menu, the game out of reach) no run starts, since its ids would name nothing it can pin.
        if (_link.WorldId == null)
        {
            await _link.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
        }

        if (_link.WorldId == null)
        {
            return new VerbReply.Failed("no_world", "no world loaded: start the run when the game is in a world.");
        }

        RunScript? saved = null;
        if (name != null)
        {
            if (_pipeline.Scripts is not { } store)
            {
                return new VerbReply.Failed(VerbFailure.NotAvailable,
                    "no script store is configured (--scripts-dir): run the code inline instead");
            }

            JsonObject args = options["args"] as JsonObject ?? new JsonObject();
            switch (store.Open(name, _link.WorldId))
            {
                case ScriptOutcome<SavedScript>.Refused refusedScript:
                    return new VerbReply.Failed(refusedScript.Code, refusedScript.Message, data: refusedScript.Data);
                case ScriptOutcome<SavedScript>.Done opened:
                    // The arguments are checked before the run starts (SPEC-NOTES 5b): a bad argument is a refusal, not
                    // a failed run, and no worker is taken.
                    if (ScriptArguments.Check(opened.Value.Parameters, args) is { } problems)
                    {
                        return OptionProblem.Refusal($"run_script {name}", problems,
                            ScriptArguments.Named(opened.Value.Parameters));
                    }

                    code = opened.Value.Source;
                    saved = new RunScript(opened.Value.Name, opened.Value.Version, args);
                    break;
                default:
                    throw new InvalidOperationException("An open is done or refused.");
            }
        }

        DateTime asked = DateTime.UtcNow;
        Run run = Start(code!, JsonNodes.IsTrue(options["confirm"]), saved);
        using CancellationTokenRegistration cancel = hooks.Cancelled.Register(() =>
            run.RequestStop("stopped", "the agent cancelled the run_script request", world: false));
        await WaitReplyAsync(run, MaxWait(options, _settings.DefaultMaxWait), asked, hooks).ConfigureAwait(false);
        // A cancelled request's reply is never read, so nothing in it counts as shown.
        return new VerbReply.Done(new JsonObject
        {
            ["text"] = await ReplyTextAsync(run, shown: !hooks.Cancelled.IsCancellationRequested).ConfigureAwait(false)
        });
    }

    internal static VerbReply.Failed SessionEnded() =>
        new("session_ended", "The session is ending: only a stopped run's cleanup writes and held restores go now.");

    private Run Start(string code, bool confirm, RunScript? script = null)
    {
        string id;
        lock (_gate)
        {
            _next++;
            id = Prefix + _next.ToString(CultureInfo.InvariantCulture);
        }

        RunRecord record = new(id, code, confirm, _link.NewStream(), _link.WorldId, script);
        Run run = new(this, record, _pipeline, _link, Seen, _settings.Journal, _settings);
        Notes notes = new(record, run);
        lock (_gate)
        {
            _runs[id] = notes;
            _order.Add(notes);
        }

        _settings.Journal.Started(record);
        _ = Task.Run(() => run.LiveAsync(_pool));
        return run;
    }

    // max_wait may only shorten the wait.
    private static TimeSpan MaxWait(JsonObject options, TimeSpan longest) =>
        JsonNodes.Number(options["max_wait"]) is { } seconds && seconds >= 0 && seconds < longest.TotalSeconds
            ? TimeSpan.FromSeconds(seconds)
            : longest;

    // Until the run ends or its wait is up. The wait is asked again as the run goes: its first write moves a read-only
    // confirmed run to the ceiling.
    private async Task WaitReplyAsync(Run run, TimeSpan maxWait, DateTime asked, ReplyHooks hooks)
    {
        TimeSpan nextProgress = _settings.ProgressEvery;
        while (!run.Ended.IsCompleted)
        {
            TimeSpan limit = run.Ceilinged && _settings.ConfirmedCeiling > TimeSpan.Zero && _settings.ConfirmedCeiling < maxWait
                ? _settings.ConfirmedCeiling
                : maxWait;
            TimeSpan waited = DateTime.UtcNow - asked;
            if (waited >= limit || hooks.Cancelled.IsCancellationRequested || Phase != SessionPhase.Open)
            {
                return;
            }

            if (hooks.Progress != null && waited >= nextProgress)
            {
                await hooks.Progress(Math.Floor(waited.TotalSeconds)).ConfigureAwait(false);
                nextProgress += _settings.ProgressEvery;
            }

            TimeSpan step = limit - waited;
            if (step > TimeSpan.FromMilliseconds(100))
            {
                step = TimeSpan.FromMilliseconds(100);
            }

            await Task.WhenAny(run.Ended, Task.Delay(step)).ConfigureAwait(false);
        }
    }

    // The final text (now delivered, so its notices are done, and what it shows is seen), or the running line.
    private async Task<string> ReplyTextAsync(Run run, bool shown = true)
    {
        if (run.Ended.IsCompleted)
        {
            if (!shown)
            {
                return RunText.Final(run.Record, null);
            }

            MarkSeen(run.Record.Id);
            return RunText.Final(run.Record, Seen);
        }

        StreamCost? cost = await LiveCostAsync(run).ConfigureAwait(false);
        return RunText.Running(run.Record, run.Line, run.ActedWrites, run.LastWrite, cost);
    }

    private async Task<StreamCost?> LiveCostAsync(Run run)
    {
        try
        {
            StreamCost[] costs = await _link.StreamCostsAsync([run.Record.Stream], CancellationToken.None).ConfigureAwait(false);
            return costs.Length > 0 ? costs[0] : null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>
    /// The run's record is final: the journal closes it, old write logs go past the memory bound, and the streams kept
    /// for held restores that could not be written back are tried again.
    /// </summary>
    internal void OnRunEnded(Run run)
    {
        _settings.Journal.Ended(run.Record);
        KeepUndoMemory();
        foreach (Run kept in KeptRuns())
        {
            if (kept != run)
            {
                _ = kept.RetryKeptAsync();
            }
        }
    }

    // The ended runs whose stream is kept for held restores not yet written back.
    private List<Run> KeptRuns()
    {
        List<Run> kept = [];
        lock (_gate)
        {
            foreach (Notes notes in _order)
            {
                if (notes.Run is { } run && !notes.Record.IsLive && run.KeptStream)
                {
                    kept.Add(run);
                }
            }
        }

        return kept;
    }

    // ---- inspect job, stop, ref -------------------------------------------------------------------------------------

    /// <summary>
    /// inspect job for a run, for a list of ids (one row each), or with no id the runs with notices pending; null for
    /// any other job.
    /// </summary>
    internal async Task<VerbReply?> InspectJobAsync(JsonObject options)
    {
        if (options["id"] is JsonArray ids)
        {
            await AdoptedAsync().ConfigureAwait(false);
            return await GatheredAsync(options, ids).ConfigureAwait(false);
        }

        string? id = JsonNodes.Text(options["id"])?.Trim();
        if (id == null)
        {
            await AdoptedAsync().ConfigureAwait(false);
            return new VerbReply.Done(Pending());
        }

        return IsRunId(id) ? await RunJobAsync(id, options).ConfigureAwait(false) : null;
    }

    /// <summary>
    /// An id list answers one row per id (D13), in the order given: {results: [{index, ok, reference_id, payload |
    /// error}], count, success_count, error_count}. A run's payload is its text view, a bulk write's its view, any
    /// other id the pipeline's answer for it (the mod's); each fails alone.
    /// </summary>
    private async Task<VerbReply> GatheredAsync(JsonObject options, JsonArray ids)
    {
        JsonArray rows = [];
        int succeeded = 0;
        for (int index = 0; index < ids.Count; index++)
        {
            JsonNode? id = ids[index];
            string text = (JsonNodes.Text(id) ?? string.Empty).Trim();
            VerbReply reply = IsRunId(text)
                ? await RunJobAsync(text, options).ConfigureAwait(false)
                : await _pipeline.JobIdAsync(options, id, CancellationToken.None).ConfigureAwait(false);
            (JsonObject row, bool ok) = VerbPipeline.RowOf(index, id, reply);
            if (ok)
            {
                succeeded++;
            }

            rows.Add(row);
        }

        return new VerbReply.Done(VerbPipeline.GatheredRows(rows, succeeded));
    }

    // One run's own answer: its text view (waited for per max_wait), or job_not_found.
    private async Task<VerbReply> RunJobAsync(string id, JsonObject options)
    {
        await AdoptedAsync().ConfigureAwait(false);
        Notes? notes = NotesOf(id);
        if (notes == null)
        {
            return new VerbReply.Failed("job_not_found", $"No run {id} in this session.");
        }

        if (notes.Run is not { } run)
        {
            MarkSeen(notes.Record.Id);
            return new VerbReply.Done(new JsonObject { ["text"] = RunText.Final(notes.Record, Seen) });
        }

        await WaitReplyAsync(run, MaxWait(options, _settings.InspectWait), DateTime.UtcNow, ReplyHooks.None)
            .ConfigureAwait(false);
        return new VerbReply.Done(new JsonObject { ["text"] = await ReplyTextAsync(run).ConfigureAwait(false) });
    }

    // The runs with notices pending: ended and not yet seen (an adopted run until it is inspected or undone).
    private JsonObject Pending()
    {
        JsonArray runs = [];
        lock (_gate)
        {
            foreach (Notes notes in _order)
            {
                RunRecord record = notes.Record;
                if (record.IsLive || notes.Delivered)
                {
                    continue;
                }

                JsonObject entry = new()
                {
                    ["id"] = record.Id,
                    ["state"] = Run.StateWord(record.State),
                    ["first_line"] = record.FirstLine,
                    ["left"] = Sticky(record)
                };
                if (record.Adopted)
                {
                    entry["earlier_session"] = true;
                    entry["restores"] = record.Restores;
                }

                runs.Add(entry);
            }
        }

        return new JsonObject { ["runs"] = runs, ["count"] = runs.Count };
    }

    /// <summary>server stop for a run, or with no id every live run; null for a job id (not a run).</summary>
    internal VerbReply? Stop(JsonObject options)
    {
        string? id = JsonNodes.Text(options["id"])?.Trim();
        if (id != null && !id.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (id != null)
        {
            Notes? notes = NotesOf(id);
            if (notes == null)
            {
                return new VerbReply.Failed("job_not_found", $"No run {id} in this session.");
            }

            return new VerbReply.Done(notes.Run is { } run
                ? Stopping(run)
                : new JsonObject
                {
                    ["id"] = notes.Record.Id,
                    ["first_line"] = notes.Record.FirstLine,
                    ["state"] = Run.StateWord(notes.Record.State)
                });
        }

        JsonArray runs = [];
        foreach (Run run in LiveRuns())
        {
            runs.Add(Stopping(run));
        }

        return new VerbReply.Done(new JsonObject { ["runs"] = runs, ["count"] = runs.Count });
    }

    private static JsonObject Stopping(Run run) => new()
    {
        ["id"] = run.Record.Id,
        ["first_line"] = run.Record.FirstLine,
        ["state"] = run.RequestStop("stopped", "stopped by server stop", world: false),
        ["line"] = run.Line
    };

    /// <summary>
    /// A script's own call of server stop, inspect job or undo for runs; null for every other call. A script may stop
    /// or inspect only its own run: anything about another run (or every run) is the agent's to ask, and nothing a
    /// worker is answered feeds the seen set or marks a run's notices done.
    /// </summary>
    internal Task<VerbReply?> RunsCallAsync(VerbCall call, Run caller, CancellationToken cancellation)
    {
        string? id = JsonNodes.Text(call.Args["id"])?.Trim();
        bool aboutRuns = (call.Verb == "server" && call.What == "stop") ||
                         (call.Verb == "inspect" && call.What == "job" && (id == null || IsRunId(id))) ||
                         (call.Verb == "undo" && id != null && IsRunId(id));
        if (!aboutRuns)
        {
            return Task.FromResult<VerbReply?>(null);
        }

        if (call.Verb == "undo" || id == null || !string.Equals(id, caller.Record.Id, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<VerbReply?>(new VerbReply.Failed("not_allowed",
                "runs are inspected, stopped and undone by the agent directly; a script may stop or inspect only its own run"));
        }

        VerbReply own = call.Verb == "server"
            ? new VerbReply.Done(Stopping(caller))
            : new VerbReply.Done(new JsonObject
            {
                ["text"] = RunText.Running(caller.Record, caller.Line, caller.ActedWrites, caller.LastWrite, null)
            });
        return Task.FromResult<VerbReply?>(own);
    }

    private static bool IsRunId(string id) => id.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>sg.ref('script-N'): a finished run's whole returned value and printed output.</summary>
    internal VerbReply RunRef(string id)
    {
        Notes? notes = NotesOf(id);
        if (notes == null)
        {
            return new VerbReply.Failed("ref_not_found", $"No run {id} in this session.");
        }

        if (notes.Record.IsLive)
        {
            return new VerbReply.Failed("run_live", $"{id} is still running; its returned value comes when it ends.");
        }

        lock (notes.Record.Sync)
        {
            return new VerbReply.Done(new JsonObject
            {
                ["ref"] = notes.Record.Id,
                ["returned"] = notes.Record.Returned?.DeepClone(),
                ["printed"] = notes.Record.Printed
            });
        }
    }

    /// <summary>The code line a live run's latest sg call came from; null when it has made none or is not live.</summary>
    internal int? LineOf(string id) => NotesOf(id)?.Run?.Line;

    private Notes? NotesOf(string id)
    {
        lock (_gate)
        {
            return _runs.TryGetValue(id.Trim(), out Notes? notes) ? notes : null;
        }
    }

    private List<Run> LiveRuns()
    {
        List<Run> live = [];
        lock (_gate)
        {
            foreach (Notes notes in _order)
            {
                if (notes.Run is { } run && notes.Record.IsLive)
                {
                    live.Add(run);
                }
            }
        }

        return live;
    }

    // ---- notices ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// The lines that ride a reply (hub.md, Run replies): on the session's first reply one line per adopted run that
    /// left something; every run that ended stopped or killed with something left or writes not done, until its final
    /// text is delivered (a sticky line on every reply); on run_script and inspect job a done notice, once, for each
    /// other run that ended unseen; the cost lines of live runs on run_script, inspect job and server info, on other
    /// replies at most every CostLinesEvery; and "journal off" once when the journal stopped.
    /// </summary>
    internal async Task<List<string>> NoticesAsync(string verb, string? what, string? aboutId)
    {
        List<string> lines = await FirstReplyLinesAsync().ConfigureAwait(false);
        if ((_settings.Journal as Journal)?.TakeNotice() is { } off)
        {
            lines.Add(off);
        }

        bool runReply = verb == "run_script" || (verb == "inspect" && what == "job");
        bool costs = runReply || (verb == "server" && what == "info");
        List<Run> live = [];
        lock (_gate)
        {
            if (!costs && DateTime.UtcNow - _lastCostLines >= _settings.CostLinesEvery)
            {
                costs = true;
            }

            foreach (Notes notes in _order)
            {
                RunRecord record = notes.Record;
                if (string.Equals(record.Id, aboutId, StringComparison.OrdinalIgnoreCase) || record.Adopted)
                {
                    continue;
                }

                if (record.IsLive)
                {
                    live.Add(notes.Run!);
                }
                else if (!notes.Delivered && Sticky(record) is { } left)
                {
                    lines.Add($"notice: {record.Id} ({record.FirstLine}) {Run.StateWord(record.State)} with something " +
                              $"left ({left}); inspect or undo it by id; if you did not start it, leave it");
                }
                else if (runReply && !notes.Delivered && !notes.DoneNoticeSent)
                {
                    notes.DoneNoticeSent = true;
                    lines.Add($"notice: {record.Id} ({record.FirstLine}) {Run.StateWord(record.State)}; if you did not " +
                              "start it, leave it");
                }
            }

            if (costs && live.Count > 0)
            {
                _lastCostLines = DateTime.UtcNow;
            }
        }

        if (costs)
        {
            foreach (Run run in live)
            {
                StreamCost? cost = await LiveCostAsync(run).ConfigureAwait(false);
                lines.Add(RunText.CostLine(run.Record, cost, run.ActedWrites));
            }
        }

        if (verb == "server" && what == "info" && KeptRuns() is { Count: > 0 } keptRuns)
        {
            lines.Add($"kept streams: {keptRuns.Count} (held restores of ended runs not yet written back; the mod applies " +
                      "them at session end)");
        }

        return lines;
    }

    // What a stopped or killed run left: its left: values and writes not done; null when nothing.
    private static string? Sticky(RunRecord record)
    {
        lock (record.Sync)
        {
            if (record.State is not (RunState.Stopped or RunState.Killed) ||
                (record.Left.Count == 0 && record.Refused.Count == 0))
            {
                return null;
            }

            List<string> parts = [];
            if (record.Left.Count > 0)
            {
                parts.Add("left: " + string.Join(", ", record.Left));
            }

            if (record.Refused.Count > 0)
            {
                parts.Add($"{record.Refused.Count} writes not done");
            }

            return string.Join("; ", parts);
        }
    }

    /// <summary>Marks a run's notices done: its final text went out, the agent inspected it, or undid it by id.</summary>
    public void MarkSeen(string id)
    {
        lock (_gate)
        {
            if (_runs.TryGetValue(id.Trim(), out Notes? notes) && !notes.Record.IsLive)
            {
                notes.Delivered = true;
            }
        }
    }

    // ---- world change, disposal -------------------------------------------------------------------------------------

    // Every live run stops with no cleanup, and its held restores are dropped (they name things of the old world).
    // Undo of the old world's runs expires by itself: each run keeps the world it ran in.
    private void OnWorldChanged(string? world)
    {
        // Ids are reused across saves: what was shown of the old world says nothing of this one.
        Seen.Clear();
        List<Run> live = LiveRuns();
        List<string> dropped = [];
        foreach (Run run in live)
        {
            run.RequestStop("world_changed", "the world changed: the run stops and no cleanup acts", world: true);
            dropped.Add(run.Record.Id);
            _ = _link.HoldRestoreAsync(run.Record.Stream,
                JsonNodes.ToElement(new JsonObject { ["clear_all"] = true }), CancellationToken.None);
        }

        _settings.Journal.WorldChanged(world, dropped);
    }

    public async ValueTask DisposeAsync()
    {
        _link.WorldChanged -= OnWorldChanged;
        List<Run> live = LiveRuns();
        foreach (Run run in live)
        {
            run.Kill();
        }

        List<Task> ended = [];
        foreach (Run run in live)
        {
            ended.Add(run.Ended);
        }

        await Task.WhenAny(Task.WhenAll(ended), Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        _pool.Dispose();
        CloseJournals();
    }

    /// <summary>A run (live or ended, this hub's or adopted) with what its notices have said so far.</summary>
    private sealed class Notes(RunRecord record, Run? run)
    {
        internal RunRecord Record { get; } = record;

        /// <summary>The live run; null for a run adopted from an earlier hub's journal.</summary>
        internal Run? Run { get; } = run;

        /// <summary>The run's final text went out (run_script or inspect job), or it was undone: no notice is owed.</summary>
        internal bool Delivered { get; set; }

        internal bool DoneNoticeSent { get; set; }

        /// <summary>For an adopted run: its line went out on the session's first reply.</summary>
        internal bool Reported { get; set; }

        internal AdoptedJournal? Journal { get; init; }
    }
}
