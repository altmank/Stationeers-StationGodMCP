using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using StationGodMCP.Client;

namespace StationGodMCP.Server;

/// <summary>
/// One live run (docs/architecture/hub.md, Runs): the worker's session sends its verb calls, waits and holds here, and
/// its run_analysis and run_end. The run gives each write its gate (Run.Gate.cs), delivers a stop once, keeps the write
/// log on its RunRecord, and at the end reads its stream's costs, ends its stream and reaps its worker. The hub's own
/// record decides how the run ended, whatever the worker says.
/// </summary>
internal sealed partial class Run : IRunChannel, IPipelineExtras, IVerbCalls
{
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(2);

    private readonly RunHost _host;
    private readonly VerbPipeline _pipeline;
    private readonly IHubLink _link;
    private readonly SeenSet _seen;
    private readonly IRunJournal _journal;
    private readonly RunHostSettings _settings;
    private readonly HashSet<string> _literals = new(StringComparer.Ordinal);
    private readonly HashSet<double> _literalNumbers = new();
    private readonly HashSet<string> _builds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WrittenTarget> _lastWritten = new(StringComparer.Ordinal);
    private readonly List<string> _writtenOrder = [];
    private readonly HashSet<string> _contested = new(StringComparer.Ordinal);
    private readonly List<(string Id, string Logic, JsonNode? After)> _holds = [];
    private readonly List<string> _pendingJobs = [];
    private readonly CancellationTokenSource _stopSignal = new();
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _readOnly;
    private bool _analysed;
    private bool _wrote;
    private int _countedWaits;
    private bool _passEnded;
    private string? _stopCode;
    private string? _stopMessage;
    private bool _stopWorld;
    private bool _stopDelivered;
    private int _stops;
    private DateTime _stopAt;
    private bool _killed;
    private bool _killing;
    private bool _keptStream;
    private int _inFlight;
    private int? _line;
    private string? _lastWrite;
    private int _sequence;
    private JsonElement? _runEnd;
    private string? _runEndProblem;
    private HubSessionEnd _sessionEnd;
    private ScriptWorker? _worker;

    internal Run(RunHost host, RunRecord record, VerbPipeline pipeline, IHubLink link, SeenSet seen, IRunJournal journal,
        RunHostSettings settings)
    {
        _host = host;
        Record = record;
        _pipeline = pipeline;
        _link = link;
        _seen = seen;
        _journal = journal;
        _settings = settings;
    }

    internal RunRecord Record { get; }

    /// <summary>Completes when the run has ended and its record is final.</summary>
    internal Task Ended => _ended.Task;

    /// <summary>
    /// True when the run's reply waits at most the confirmed ceiling: a confirmed run whose analysis says it writes, or
    /// that has made a write or hold call (the analysis is advisory; the first write call decides). Until the analysis
    /// comes a run is not taken to write: a slow worker start must not answer a read-only run at the ceiling.
    /// </summary>
    internal bool Ceilinged
    {
        get
        {
            lock (Record.Sync)
            {
                return Record.Confirm && ((_analysed && !_readOnly) || _wrote);
            }
        }
    }

    internal int? Line
    {
        get
        {
            lock (Record.Sync)
            {
                return _line;
            }
        }
    }

    internal string? LastWrite
    {
        get
        {
            lock (Record.Sync)
            {
                return _lastWrite;
            }
        }
    }

    internal int ActedWrites
    {
        get
        {
            lock (Record.Sync)
            {
                int count = 0;
                foreach (WriteEntry write in Record.Writes)
                {
                    count += write.Acted ? 1 : 0;
                }

                return count;
            }
        }
    }

    /// <summary>The welcome's run key: the worker reads its code and route from it.</summary>
    internal JsonObject WelcomeRun() => new()
    {
        ["id"] = Record.Id,
        ["code"] = Record.Code,
        ["confirm"] = Record.Confirm,
        ["face"] = "script",
        ["limits"] = new JsonObject { ["cut_bytes"] = RunText.CutBytes }
    };

    // ---- life -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Takes a worker, serves its session until it ends, then finishes the run. A fault of the hub's own is logged and
    /// ends the run failed; the run always ends.
    /// </summary>
    internal async Task LiveAsync(WorkerPool pool)
    {
        try
        {
            await ServeAsync(pool).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"[StationGodMCP] {Record.Id} failed in the hub: {exception}");
            lock (Record.Sync)
            {
                Record.Error ??= "the hub failed on the run: " + exception.Message;
            }

            Kill();
            End(RunState.Failed);
        }
    }

    private async Task ServeAsync(WorkerPool pool)
    {
        // Every call of the run is made for the world it started in (the link refuses it unsent once that is gone).
        _pipeline.PinWorld(Record.Stream, Record.World);
        ScriptWorker worker;
        try
        {
            worker = await pool.TakeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await FinishUnstartedAsync("the script worker did not start: " + exception.Message).ConfigureAwait(false);
            return;
        }

        lock (Record.Sync)
        {
            _worker = worker;
        }

        HubSession session = new(_link, _pipeline, new HubSessionSettings(Face.Script)
        {
            ClientId = Record.Id,
            Run = WelcomeRun(),
            RunChannel = this,
            Extras = this,
            Verbs = this,
            MainStream = Record.Stream
        });
        bool killNow;
        lock (Record.Sync)
        {
            // A second stop before the worker was taken.
            killNow = _killed;
        }

        if (killNow)
        {
            worker.Kill();
        }

        try
        {
            _sessionEnd = await session.RunAsync(worker.FromWorker, worker.ToWorker, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The worker's pipes broke: it is gone.
        }

        if (!await worker.ExitedAsync(ExitWait).ConfigureAwait(false))
        {
            worker.Kill();
            await worker.ExitedAsync(ExitWait).ConfigureAwait(false);
        }

        await FinishAsync(session, worker).ConfigureAwait(false);
    }

    private async Task FinishUnstartedAsync(string error)
    {
        lock (Record.Sync)
        {
            Record.Error = error;
        }

        try
        {
            await _link.EndStreamAsync(Record.Stream).ConfigureAwait(false);
        }
        finally
        {
            _pipeline.EndStream(Record.Stream);
            End(RunState.Failed);
        }
    }

    // The end, in order: the session's calls are cancelled, the end state is decided, held restores are cleared after a
    // normal end and written back after any other (a hold is not lost when the hub kills a run), what a stopped run
    // left is read, the costs are read, then the stream ends and the worker is reaped. The run ends whatever fails.
    private async Task FinishAsync(HubSession session, ScriptWorker worker)
    {
        RunState state = RunState.Failed;
        try
        {
            await session.CloseAsync().ConfigureAwait(false);
            state = EndState(session.Welcomed, worker.StderrTail);
            bool world;
            bool normal;
            lock (Record.Sync)
            {
                world = _stopWorld;
                normal = state is RunState.Done or RunState.Report or RunState.Failed && !_stopDelivered;
            }

            try
            {
                if (!world && HasHolds() && _host.Phase != SessionPhase.Ended)
                {
                    if (normal)
                    {
                        GameReply cleared = await _link.HoldRestoreAsync(Record.Stream,
                            JsonNodes.ToElement(new JsonObject { ["clear_all"] = true }), Record.World,
                            CancellationToken.None).ConfigureAwait(false);
                        if (cleared is GameReply.Ok)
                        {
                            ClearedHolds(null);
                        }
                    }
                    else
                    {
                        await RestoreHoldsAsync().ConfigureAwait(false);
                    }
                }

                if (state is RunState.Stopped or RunState.Killed && !world)
                {
                    await ReadLeftAsync().ConfigureAwait(false);
                }

                StreamCost[] costs = await _link.StreamCostsAsync([Record.Stream], CancellationToken.None)
                    .ConfigureAwait(false);
                lock (Record.Sync)
                {
                    Record.Cost = costs.Length > 0 ? costs[0] : null;
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // The game went away: the run still ends, without its costs.
            }

            // stream_end clears a stream's held restores unwritten, so a run whose holds could not be written back keeps
            // its stream: the mod then applies them at session_end (or when the resume window passes).
            if (world || !HasHolds())
            {
                await _link.EndStreamAsync(Record.Stream).ConfigureAwait(false);
            }
            else
            {
                lock (Record.Sync)
                {
                    _keptStream = true;
                }
            }

            ReleaseWhenSettled();
            _pipeline.EndStream(Record.Stream);
            worker.Dispose();
        }
        finally
        {
            End(state);
        }
    }

    // A run that ended inside a hold (killed, dead, stopped and swallowed): its registered afters are written as
    // CLEANUP, then the registrations whose restore acted are cleared.
    private async Task RestoreHoldsAsync()
    {
        List<(string Id, string Logic, JsonNode? After)> holds;
        lock (Record.Sync)
        {
            holds = [.. _holds];
        }

        List<string> logics = [];
        foreach ((string _, string logic, JsonNode? _) in holds)
        {
            if (!logics.Contains(logic))
            {
                logics.Add(logic);
            }
        }

        foreach (string logic in logics)
        {
            List<string> ids = [];
            List<JsonNode?> after = [];
            foreach ((string id, string held, JsonNode? value) in holds)
            {
                if (held == logic)
                {
                    ids.Add(id);
                    after.Add(value?.DeepClone());
                }
            }

            VerbReply wrote = await SendAsync("write", "logic", new JsonObject { ["writes"] = Writes(ids, JsonValue.Create(logic), after) },
                new RunCallContext(null, WriteGroup.Cleanup, holdExit: true), CancellationToken.None).ConfigureAwait(false);
            List<string> restored = [];
            if (wrote is VerbReply.Done done && !(done.Reply is JsonObject report && JsonNodes.IsTrue(report["report_only"])))
            {
                List<JsonObject> rows = BulkWrites.Rows(done.Reply);
                for (int index = 0; index < ids.Count; index++)
                {
                    JsonObject? row = RowFor(rows, index, ids[index]);
                    if (row != null && (row["ok"] is not { } ok || JsonNodes.IsTrue(ok)))
                    {
                        restored.Add(ids[index]);
                    }
                }
            }

            if (restored.Count > 0)
            {
                await ClearHoldsAsync(restored, JsonValue.Create(logic), CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>True while the run's stream is kept open for held restores that could not be written back.</summary>
    internal bool KeptStream
    {
        get
        {
            lock (Record.Sync)
            {
                return _keptStream;
            }
        }
    }

    /// <summary>
    /// Tries a kept stream's write-back again (at a later run's end): the held values are written as CLEANUP and
    /// cleared, and once none is left the stream ends. Not after a world change or session_end.
    /// </summary>
    internal async Task RetryKeptAsync()
    {
        lock (Record.Sync)
        {
            if (!_keptStream || _stopWorld)
            {
                return;
            }
        }

        if (_host.Phase == SessionPhase.Ended || _link.WorldId != Record.World)
        {
            return;
        }

        await RestoreHoldsAsync().ConfigureAwait(false);
        if (!HasHolds())
        {
            lock (Record.Sync)
            {
                _keptStream = false;
            }

            await _link.EndStreamAsync(Record.Stream).ConfigureAwait(false);
        }
    }

    // Holds whose registration the mod cleared (all of them when ids is null): forgotten here and in the journal.
    private void ClearedHolds(List<string>? ids, string? logic = null)
    {
        List<(string Id, string Logic, JsonNode? After)> gone = [];
        lock (Record.Sync)
        {
            foreach ((string Id, string Logic, JsonNode? After) hold in _holds)
            {
                if (ids == null || (hold.Logic == logic && ids.Contains(hold.Id)))
                {
                    gone.Add(hold);
                }
            }

            _holds.RemoveAll(hold => ids == null || (hold.Logic == logic && ids.Contains(hold.Id)));
        }

        foreach ((string id, string held, JsonNode? _) in gone)
        {
            _journal.HoldCleared(Record, id, held);
        }
    }

    // The record is final: its state, its end, and the one OnRunEnded.
    private void End(RunState state)
    {
        lock (Record.Sync)
        {
            if (Record.State == RunState.Running)
            {
                Record.State = state;
                Record.Ended = DateTime.UtcNow;
            }
        }

        _stopSignal.Cancel();
        if (_ended.TrySetResult())
        {
            _host.OnRunEnded(this);
        }
    }

    // The hub's own record decides: no hello is a worker that did not start; no run_end a kill or a death; a stop it
    // delivered is stopped whatever the worker said.
    private RunState EndState(bool welcomed, string stderr)
    {
        lock (Record.Sync)
        {
            if (!welcomed)
            {
                Record.Error = "the script worker did not start: " +
                               (stderr.Length > 0 ? stderr : "it exited before it said hello");
                return RunState.Failed;
            }

            if (_runEndProblem != null)
            {
                Record.Error = "the worker's run_end could not be read: " + _runEndProblem;
                return _stopDelivered ? RunState.Stopped : RunState.Failed;
            }

            if (_runEnd == null && _sessionEnd == HubSessionEnd.ProtocolError)
            {
                Record.Error = "the worker sent a line the hub could not read (protocol_error); the run ends there" +
                               (stderr.Length > 0 ? ": " + stderr : string.Empty);
                return _stopDelivered ? RunState.Stopped : RunState.Failed;
            }

            if (_runEnd is not { } end)
            {
                if (!_killed && stderr.Length > 0)
                {
                    Record.Error = "the worker died: " + stderr;
                }

                return RunState.Killed;
            }

            Record.Returned = end.TryGetProperty("returned", out JsonElement returned) ? JsonNodes.FromElement(returned) : null;
            Record.Printed = end.TryGetProperty("printed", out JsonElement printed) && printed.ValueKind == JsonValueKind.String
                ? printed.GetString()!
                : string.Empty;
            Record.Error = end.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.String
                ? error.GetString()
                : null;
            if (_stopDelivered)
            {
                return RunState.Stopped;
            }

            string? said = end.TryGetProperty("state", out JsonElement given) && given.ValueKind == JsonValueKind.String
                ? given.GetString()
                : null;
            return said switch
            {
                // REPORT is for a run that reported a write; one that only read is done.
                "done" => Record.Confirm || Record.Reported.Count == 0 ? RunState.Done : RunState.Report,
                "pass_end" => RunState.Report,
                "stopped" => RunState.Stopped,
                _ => RunState.Failed
            };
        }
    }

    private bool HasHolds()
    {
        lock (Record.Sync)
        {
            return _holds.Count > 0;
        }
    }

    // A run's jobs pin their snapshots until they settle, so release waits for them.
    private void ReleaseWhenSettled()
    {
        List<string> jobs;
        lock (Record.Sync)
        {
            jobs = [.. _pendingJobs];
        }

        if (jobs.Count == 0)
        {
            _ = _link.ReleaseAsync(Record.Stream);
            return;
        }

        _ = Task.Run(async () =>
        {
            foreach (string job in jobs)
            {
                try
                {
                    await _link.WaitJobAsync(job, TimeSpan.FromMinutes(10), CancellationToken.None).ConfigureAwait(false);
                }
                catch (NotSupportedException)
                {
                    // No job waits on this link: release now.
                }
            }

            await _link.ReleaseAsync(Record.Stream).ConfigureAwait(false);
        });
    }

    // left: per logic target the run wrote, its value now where that differs from the value before the run.
    private async Task ReadLeftAsync()
    {
        List<(string Key, string Id, string Logic, JsonNode? Before, JsonNode? Last)> written = [];
        lock (Record.Sync)
        {
            foreach (string key in _writtenOrder)
            {
                WrittenTarget last = _lastWritten[key];
                int cut = key.IndexOf('\n');
                written.Add((key, key[..cut], key[(cut + 1)..], FirstBefore(key), last.After ?? last.New));
            }
        }

        if (written.Count == 0)
        {
            return;
        }

        JsonArray reads = [];
        foreach ((string _, string id, string logic, JsonNode? _, JsonNode? _) in written)
        {
            reads.Add(new JsonObject { ["id"] = id, ["logic"] = logic });
        }

        VerbReply reply = await _pipeline.CallAsync(new VerbCall("read", "logic", new JsonObject { ["reads"] = reads },
            Face.Script, Record.Stream), CancellationToken.None).ConfigureAwait(false);
        List<JsonObject> rows = reply is VerbReply.Done done ? BulkWrites.Rows(done.Reply) : [];
        lock (Record.Sync)
        {
            for (int index = 0; index < written.Count; index++)
            {
                (string _, string id, string logic, JsonNode? before, JsonNode? last) = written[index];
                JsonNode? now = index < rows.Count && JsonNodes.IsTrue(rows[index]["ok"]) ? rows[index]["value"] : last;
                if (!SameValue(now, before))
                {
                    Record.Left.Add(new LeftTarget(id, logic, now?.DeepClone(), before?.DeepClone()));
                }
            }
        }
    }

    // Under Sync: the before of the run's first acted write to the target.
    private JsonNode? FirstBefore(string key)
    {
        foreach (WriteEntry write in Record.Writes)
        {
            foreach (WrittenTarget target in write.Targets)
            {
                if (target.Acted && write.Acted && target.Field != null && Key(target.Id, target.Field) == key)
                {
                    return target.Before;
                }
            }
        }

        return null;
    }

    // ---- stops ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Stops the run (server stop, a user cancel, a world change): delivered once at its next verb, wait or hold call
    /// (at once to a wait). The worker is killed KillAfter later, longer while a call is in flight but never past
    /// KillLatest, or at once on a second stop. Answers the run's state word for the stop's reply.
    /// </summary>
    internal string RequestStop(string code, string message, bool world, bool secondKills = true)
    {
        bool first;
        bool killNow;
        lock (Record.Sync)
        {
            if (!Record.IsLive)
            {
                return StateWord(Record.State);
            }

            _stops++;
            first = _stopCode == null;
            if (first)
            {
                _stopCode = code;
                _stopMessage = message;
                _stopAt = DateTime.UtcNow;
                Record.StopCode = code;
                Record.StopMessage = message;
            }

            if (world)
            {
                _stopWorld = true;
                Record.WorldChanged = true;
            }

            killNow = !first && !world && secondKills;
        }

        _stopSignal.Cancel();
        if (first)
        {
            _ = KillLaterAsync();
        }

        if (killNow)
        {
            Kill();
        }

        return "stopping";
    }

    private async Task KillLaterAsync()
    {
        while (!_ended.Task.IsCompleted)
        {
            TimeSpan since;
            int inFlight;
            lock (Record.Sync)
            {
                since = DateTime.UtcNow - _stopAt;
                inFlight = _inFlight;
            }

            // At KillAfter the run takes no new call; a call already in flight is waited for, never past KillLatest.
            if (since >= _settings.KillAfter)
            {
                lock (Record.Sync)
                {
                    _killing = true;
                    inFlight = _inFlight;
                }
            }

            if (since >= _settings.KillLatest || (since >= _settings.KillAfter && inFlight == 0))
            {
                Kill();
                return;
            }

            await Task.WhenAny(_ended.Task, Task.Delay(TimeSpan.FromMilliseconds(50))).ConfigureAwait(false);
        }
    }

    internal void Kill()
    {
        ScriptWorker? worker;
        lock (Record.Sync)
        {
            _killed = true;
            worker = _worker;
        }

        worker?.Kill();
    }

    // The stop, once: the error of the call that takes it.
    private VerbReply.Failed? TakeStop(int? line)
    {
        lock (Record.Sync)
        {
            if (_stopCode == null || _stopDelivered)
            {
                return null;
            }

            _stopDelivered = true;
            return new VerbReply.Failed(_stopCode, _stopMessage ?? "the run was stopped",
                data: new JsonObject { ["line"] = line }, stopped: true);
        }
    }

    private bool StopRequested
    {
        get
        {
            lock (Record.Sync)
            {
                return _stopCode != null;
            }
        }
    }

    // A write-rule stop: the pipeline already answered it with stopped: true, so it counts as delivered.
    private void NoteRuleStop(VerbReply.Failed stop, int? line)
    {
        bool first;
        lock (Record.Sync)
        {
            _stops++;
            first = _stopCode == null;
            if (first)
            {
                _stopCode = stop.Code;
                _stopMessage = $"line {line?.ToString(CultureInfo.InvariantCulture) ?? "?"}: {stop.Message}";
                _stopAt = DateTime.UtcNow;
                Record.StopCode = _stopCode;
                Record.StopMessage = _stopMessage;
                Record.StopRows = stop.Data?["rows"] as JsonArray is { } rows ? (JsonArray)rows.DeepClone() : null;
                Record.StopFix = stop.Fix;
            }

            _stopDelivered = true;
        }

        _stopSignal.Cancel();
        if (first)
        {
            _ = KillLaterAsync();
        }
    }

    // ---- the session's calls ----------------------------------------------------------------------------------------

    public async Task<VerbReply> VerbAsync(VerbCall call, CancellationToken cancellation)
    {
        if (!Enter(call.Line))
        {
            return Killing();
        }

        try
        {
            if (TakeStop(call.Line) is { } stop)
            {
                return stop;
            }

            // At session end only writes may go (the gate lets the cleanup ones act); after session_end nothing.
            if (_host.Phase == SessionPhase.Ended ||
                (_host.Phase == SessionPhase.Cleanup && _pipeline.Catalogue.Verb(call.Verb)?.What(call.What)?.Write == null))
            {
                return RunHost.SessionEnded();
            }

            if (await _host.RunsCallAsync(call, this, cancellation).ConfigureAwait(false) is { } runs)
            {
                return runs;
            }

            return await SendAsync(call.Verb, call.What, call.Args, new RunCallContext(call.Line), cancellation)
                .ConfigureAwait(false);
        }
        finally
        {
            Exit();
        }
    }

    public Task AnalysisAsync(JsonElement message)
    {
        lock (Record.Sync)
        {
            if (message.TryGetProperty("literals", out JsonElement literals) && literals.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement literal in literals.EnumerateArray())
                {
                    JsonNode? node = JsonNodes.FromElement(literal);
                    if (IdText(node) is { } text)
                    {
                        _literals.Add(text);
                    }

                    if (JsonNodes.Number(node) is { } number)
                    {
                        _literalNumbers.Add(number);
                    }
                }
            }

            bool writes = message.TryGetProperty("writes", out JsonElement w) && w.ValueKind == JsonValueKind.True;
            bool holds = message.TryGetProperty("holds", out JsonElement h) && h.ValueKind == JsonValueKind.True;
            _readOnly = !writes && !holds;
            _analysed = true;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// run_end: kept if it reads as one, else its reason; either way the worker is killed now, so code that forged an
    /// early run_end cannot go on running after the hub has stopped listening.
    /// </summary>
    public Task EndedAsync(JsonElement message)
    {
        string? problem = RunEndProblem(message);
        lock (Record.Sync)
        {
            if (problem == null)
            {
                _runEnd = message.Clone();
            }
            else
            {
                _runEndProblem = problem;
            }
        }

        Kill();
        return Task.CompletedTask;
    }

    private static readonly string[] EndStates = ["done", "stopped", "pass_end", "failed"];

    private static string? RunEndProblem(JsonElement message)
    {
        foreach (JsonProperty property in message.EnumerateObject())
        {
            if (property.Name is not ("type" or "state" or "returned" or "printed" or "error"))
            {
                return $"it has an unknown key '{property.Name}'";
            }
        }

        if (!message.TryGetProperty("state", out JsonElement state) || state.ValueKind != JsonValueKind.String ||
            Array.IndexOf(EndStates, state.GetString()) < 0)
        {
            return "its state is not done, stopped, pass_end or failed";
        }

        foreach (string text in new[] { "printed", "error" })
        {
            if (message.TryGetProperty(text, out JsonElement value) &&
                value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            {
                return $"its {text} is not text";
            }
        }

        return null;
    }

    // False once the kill is due: from then on the run's calls are refused (Killing), so it cannot keep the kill off.
    private bool Enter(int? line)
    {
        lock (Record.Sync)
        {
            if (_killing)
            {
                return false;
            }

            _inFlight++;
            Record.Calls++;
            if (line != null)
            {
                _line = line;
            }
        }

        return true;
    }

    private static VerbReply.Failed Killing() =>
        new("run_killed", "the run was stopped and its kill is due; nothing more is sent for it");

    private void Exit()
    {
        lock (Record.Sync)
        {
            _inFlight--;
        }
    }

    // One call through the pipeline with this run's gate for it, then what its reply tells the run.
    private async Task<VerbReply> SendAsync(string verb, string? what, JsonObject args, RunCallContext context,
        CancellationToken cancellation)
    {
        // A report run's writes never act, so only a run that acts needs the late reply's values for its write log.
        Action<JsonNode?>? late = Record.Confirm ? answer => LateFill(context, answer) : null;
        VerbCall gated = new(verb, what, args, Face.Script, Record.Stream, context.Line)
        {
            Gate = new RunGate(this, context),
            LateReply = late
        };
        VerbReply reply;
        try
        {
            reply = await _pipeline.CallAsync(gated, cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelled after the gate let it act (a kill, a closing session): it may have reached the game, so it stays
            // in the log as maybe ran, and the journal hears of it, before the cancellation goes on.
            if (context.Pending is { } pending)
            {
                lock (Record.Sync)
                {
                    pending.MaybeRan = true;
                    pending.Acted = true;
                }

                _journal.After(Record, pending);
            }

            throw;
        }

        Observe(verb, what, context, reply);
        return reply;
    }

    // ---- small helpers ----------------------------------------------------------------------------------------------

    /// <summary>An id as the seen set holds it: a string as written, an integer as its digits.</summary>
    internal static string? IdText(JsonNode? node)
    {
        if (JsonNodes.Text(node) is { } text)
        {
            return text;
        }

        // An integer as written, digit for digit: ids pass 2^53, where a double would round them.
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number)
        {
            return null;
        }

        string raw = value.ToJsonString();
        int start = raw.StartsWith('-') ? 1 : 0;
        if (raw.Length == start)
        {
            return null;
        }

        for (int index = start; index < raw.Length; index++)
        {
            if (!char.IsAsciiDigit(raw[index]))
            {
                return null;
            }
        }

        return raw;
    }

    private static string Key(string id, string logic) => id + "\n" + logic;

    internal static bool SameValue(JsonNode? left, JsonNode? right)
    {
        if (JsonNodes.Number(left) is { } a && JsonNodes.Number(right) is { } b)
        {
            return a == b;
        }

        return JsonNode.DeepEquals(left, right);
    }

    internal static string Shown(JsonNode? value) =>
        value == null ? "?" : JsonNodes.Text(value) ?? value.ToJsonString();

    internal static string StateWord(RunState state) => state switch
    {
        RunState.Running => "running",
        RunState.Done => "done",
        RunState.Report => "REPORT",
        RunState.Stopped => "stopped",
        RunState.Failed => "failed",
        _ => "killed"
    };
}
