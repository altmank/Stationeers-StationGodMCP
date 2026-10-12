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

/// <summary>
/// The runs of one hub (docs/architecture/hub.md, Runs): run_script mints script-N on a new stream with a worker from
/// the pool; inspect job and server stop for script-N; the notices and cost lines that ride replies; a world change
/// stops every live run. The seen set is the hub's, fed by the MCP adapter. Undo of runs, the journal and the EOF order
/// build on this (C5).
/// </summary>
public sealed class RunHost : IAsyncDisposable
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

    /// <summary>Starts the idle worker now, so the first run does not wait for Python.</summary>
    public void Warm() => _pool.Warm();

    /// <summary>A run's record by id (script-N), for C5's undo and journal; null when there is none.</summary>
    public RunRecord? Find(string id)
    {
        lock (_gate)
        {
            return _runs.TryGetValue(id.Trim(), out Notes? notes) ? notes.Run.Record : null;
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

        if (JsonNodes.Text(options["code"]) is not { } code || code.Trim().Length == 0)
        {
            return OptionProblem.Refusal("run_script", [new OptionProblem("code", "required: the Python to run")]);
        }

        DateTime asked = DateTime.UtcNow;
        Run run = Start(code, JsonNodes.IsTrue(options["confirm"]));
        using CancellationTokenRegistration cancel = hooks.Cancelled.Register(() =>
            run.RequestStop("stopped", "the agent cancelled the run_script request", world: false));
        await WaitReplyAsync(run, MaxWait(options, _settings.DefaultMaxWait), asked, hooks).ConfigureAwait(false);
        return new VerbReply.Done(new JsonObject { ["text"] = await ReplyTextAsync(run).ConfigureAwait(false) });
    }

    private Run Start(string code, bool confirm)
    {
        string id;
        lock (_gate)
        {
            _next++;
            id = Prefix + _next.ToString(CultureInfo.InvariantCulture);
        }

        RunRecord record = new(id, code, confirm, _link.NewStream(), _link.WorldId);
        Run run = new(this, record, _pipeline, _link, Seen, _settings.Journal, _settings);
        Notes notes = new(run);
        lock (_gate)
        {
            _runs[id] = notes;
            _order.Add(notes);
        }

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
            if (waited >= limit || hooks.Cancelled.IsCancellationRequested)
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

    // The final text (now delivered, so its notices are done), or the running line.
    private async Task<string> ReplyTextAsync(Run run)
    {
        if (run.Ended.IsCompleted)
        {
            lock (_gate)
            {
                _runs[run.Record.Id].Delivered = true;
            }

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

    internal void OnRunEnded(Run run)
    {
        // Nothing to do yet: the record is final and the notices read it. C5's journal closes the run here.
    }

    // ---- inspect job, stop, ref -------------------------------------------------------------------------------------

    /// <summary>inspect job for script-N, or with no id the runs with notices pending; null for any other job.</summary>
    internal async Task<VerbReply?> InspectJobAsync(JsonObject options)
    {
        string? id = JsonNodes.Text(options["id"])?.Trim();
        if (id == null)
        {
            return new VerbReply.Done(Pending());
        }

        if (!id.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        Run? run = RunOf(id);
        if (run == null)
        {
            return new VerbReply.Failed("job_not_found", $"No run {id} in this session.");
        }

        await WaitReplyAsync(run, MaxWait(options, _settings.InspectWait), DateTime.UtcNow, ReplyHooks.None)
            .ConfigureAwait(false);
        return new VerbReply.Done(new JsonObject { ["text"] = await ReplyTextAsync(run).ConfigureAwait(false) });
    }

    // The runs with notices pending: stopped or killed with something left, or ended and not yet seen.
    private JsonObject Pending()
    {
        JsonArray runs = [];
        lock (_gate)
        {
            foreach (Notes notes in _order)
            {
                RunRecord record = notes.Run.Record;
                if (record.IsLive || notes.Delivered)
                {
                    continue;
                }

                runs.Add(new JsonObject
                {
                    ["id"] = record.Id,
                    ["state"] = Run.StateWord(record.State),
                    ["first_line"] = record.FirstLine,
                    ["left"] = Sticky(record)
                });
            }
        }

        return new JsonObject { ["runs"] = runs, ["count"] = runs.Count };
    }

    /// <summary>server stop for script-N, or with no id every live run; null for a job id (not a run).</summary>
    internal VerbReply? Stop(JsonObject options)
    {
        string? id = JsonNodes.Text(options["id"])?.Trim();
        if (id != null && !id.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (id != null)
        {
            Run? run = RunOf(id);
            return run == null
                ? new VerbReply.Failed("job_not_found", $"No run {id} in this session.")
                : new VerbReply.Done(Stopping(run));
        }

        List<Run> live = [];
        lock (_gate)
        {
            foreach (Notes notes in _order)
            {
                if (notes.Run.Record.IsLive)
                {
                    live.Add(notes.Run);
                }
            }
        }

        JsonArray runs = [];
        foreach (Run run in live)
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

    /// <summary>A script's own call of server stop or inspect job for runs; null for every other call.</summary>
    internal async Task<VerbReply?> RunsCallAsync(VerbCall call, CancellationToken cancellation)
    {
        return call.Verb switch
        {
            "server" when call.What == "stop" => Stop(call.Args),
            "inspect" when call.What == "job" => await InspectJobAsync(call.Args).ConfigureAwait(false),
            _ => null
        };
    }

    /// <summary>sg.ref('script-N'): a finished run's whole returned value and printed output.</summary>
    internal VerbReply RunRef(string id)
    {
        Run? run = RunOf(id);
        if (run == null)
        {
            return new VerbReply.Failed("ref_not_found", $"No run {id} in this session.");
        }

        if (!run.Ended.IsCompleted)
        {
            return new VerbReply.Failed("run_live", $"{id} is still running; its returned value comes when it ends.");
        }

        lock (run.Record.Sync)
        {
            return new VerbReply.Done(new JsonObject
            {
                ["ref"] = run.Record.Id,
                ["returned"] = run.Record.Returned?.DeepClone(),
                ["printed"] = run.Record.Printed
            });
        }
    }

    private Run? RunOf(string id)
    {
        lock (_gate)
        {
            return _runs.TryGetValue(id.Trim(), out Notes? notes) ? notes.Run : null;
        }
    }

    // ---- notices ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// The lines that ride a reply (hub.md, Run replies): every run that ended stopped or killed with something left or
    /// writes not done, until its final text is delivered (a sticky line on every reply); on run_script and inspect job
    /// a done notice, once, for each other run that ended unseen; and the cost lines of live runs on run_script,
    /// inspect job and server info, on other replies at most every CostLinesEvery.
    /// </summary>
    internal async Task<List<string>> NoticesAsync(string verb, string? what, string? aboutId)
    {
        bool runReply = verb == "run_script" || (verb == "inspect" && what == "job");
        bool costs = runReply || (verb == "server" && what == "info");
        List<string> lines = [];
        List<Run> live = [];
        lock (_gate)
        {
            if (!costs && DateTime.UtcNow - _lastCostLines >= _settings.CostLinesEvery)
            {
                costs = true;
            }

            foreach (Notes notes in _order)
            {
                RunRecord record = notes.Run.Record;
                if (string.Equals(record.Id, aboutId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (record.IsLive)
                {
                    live.Add(notes.Run);
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

    /// <summary>Marks a run's notices done: the agent inspected it, or undid it by id (C5).</summary>
    public void MarkSeen(string id)
    {
        lock (_gate)
        {
            if (_runs.TryGetValue(id.Trim(), out Notes? notes) && !notes.Run.Record.IsLive)
            {
                notes.Delivered = true;
            }
        }
    }

    // ---- world change, disposal -------------------------------------------------------------------------------------

    // Every live run stops with no cleanup, and its held restores are dropped (they name things of the old world).
    private void OnWorldChanged(string? world)
    {
        List<Run> live = [];
        lock (_gate)
        {
            foreach (Notes notes in _order)
            {
                if (notes.Run.Record.IsLive)
                {
                    live.Add(notes.Run);
                }
            }
        }

        foreach (Run run in live)
        {
            run.RequestStop("world_changed", "the world changed: the run stops and no cleanup acts", world: true);
            _ = _link.HoldRestoreAsync(run.Record.Stream,
                JsonNodes.ToElement(new JsonObject { ["clear_all"] = true }), CancellationToken.None);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _link.WorldChanged -= OnWorldChanged;
        List<Run> live = [];
        lock (_gate)
        {
            foreach (Notes notes in _order)
            {
                if (notes.Run.Record.IsLive)
                {
                    live.Add(notes.Run);
                }
            }
        }

        foreach (Run run in live)
        {
            run.Kill();
        }

        foreach (Run run in live)
        {
            await Task.WhenAny(run.Ended, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        }

        _pool.Dispose();
    }

    /// <summary>A run with what its notices have said so far.</summary>
    private sealed class Notes(Run run)
    {
        internal Run Run { get; } = run;

        /// <summary>The run's final text went out (run_script or inspect job): no notice is owed.</summary>
        internal bool Delivered { get; set; }

        internal bool DoneNoticeSent { get; set; }
    }
}
