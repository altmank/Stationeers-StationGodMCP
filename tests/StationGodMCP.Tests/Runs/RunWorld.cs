#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using StationGodMCP.Client;
using StationGodMCP.Server;
using StationGodMCP.Tests.Hub;
using Xunit;

namespace StationGodMCP.Tests.Runs;

/// <summary>
/// A hub link over a tiny world for runs: devices with logic values (read_logic_many, write_logic, write_logic_many),
/// names (thing_health), one find result (999), held restores, stream costs, and a world change on demand. Every call
/// and hold_restore is logged in order.
/// </summary>
internal sealed class RunWorld : IHubLink
{
    private readonly object _gate = new();
    private readonly Dictionary<string, double> _logic = new(StringComparer.Ordinal);
    private readonly List<string> _log = new();
    private readonly CancellationTokenSource _sessionEnd = new();
    private long _lastStream;

    public JsonElement? Welcome { get; set; } = FakeHubLink.Element(FakeHubLink.ModWelcome);

    public string? WorldId { get; private set; } = "world-1";

    public JsonElement? World => FakeHubLink.Element("{\"id\":\"" + WorldId + "\",\"save\":\"fake\",\"epoch\":0}");

    public int MaxRequestBytes => 4 * 1024 * 1024;

    public GameCatalogue Catalogue => GameCatalogue.BuiltIn;

    public Task EndStreamIfConnectedAsync(long stream) => EndStreamAsync(stream);

    public event Action<string?>? WorldChanged;

    public event Action<JsonElement>? EventReceived
    {
        add { }
        remove { }
    }

    /// <summary>A call answered before the world's own handling (null: the world answers).</summary>
    public Func<GameCall, GameReply?>? Override { get; set; }

    /// <summary>How long a call waits before it is answered (cancellable); null or zero: at once.</summary>
    public Func<GameCall, TimeSpan>? CallDelay { get; set; }

    /// <summary>
    /// A gate a call waits on before it is answered (null: none): the test opens it, so a call stays in flight for
    /// exactly as long as the test says. session_end answers a gated call cancelled, as the mod does.
    /// </summary>
    public Func<GameCall, Task?>? Gate { get; set; }

    /// <summary>True for a (id, logic, value) write the game refuses (its row answers ok: false).</summary>
    public Func<string, string, double, bool>? RefusesWrite { get; set; }

    /// <summary>True makes stream_end throw, as a link fault would.</summary>
    public bool EndStreamThrows { get; set; }

    /// <summary>The world changes here without the event reaching anyone yet (the race a world change can win).</summary>
    public void ChangeWorldQuietly(string? world) => WorldId = world;

    /// <summary>Asked at each hold_restore with its change (null: the world answers it ok); the test may stop a run there.</summary>
    public Func<JsonElement, GameReply?>? OnHoldRestore { get; set; }

    /// <summary>Called at each stream_costs (a run's end reads its costs there).</summary>
    public Action? OnStreamCosts { get; set; }

    public StreamCost Cost { get; set; } = new(0, 12, 48.0, 2, 2, 0.6);

    public List<long> Ended { get; } = new();

    public List<long> Released { get; } = new();

    public double Value(string id, string logic)
    {
        lock (_gate)
        {
            return _logic.TryGetValue(id + "/" + logic, out double value) ? value : 0;
        }
    }

    public void Set(string id, string logic, double value)
    {
        lock (_gate)
        {
            _logic[id + "/" + logic] = value;
        }
    }

    /// <summary>The calls and hold_restores so far, in order: "verb.what" (KeyOf) or "hold_restore {change}".</summary>
    public List<string> Log
    {
        get
        {
            lock (_gate)
            {
                return new List<string>(_log);
            }
        }
    }

    public int Count(string method)
    {
        int count = 0;
        foreach (string entry in Log)
        {
            count += entry == method || entry.StartsWith(method + " ", StringComparison.Ordinal) ? 1 : 0;
        }

        return count;
    }

    public void ChangeWorld(string world)
    {
        WorldId = world;
        WorldChanged?.Invoke(world);
    }

    public Task<bool> ConnectAsync(CancellationToken cancellation) => Task.FromResult(true);

    public long NewStream() => Interlocked.Increment(ref _lastStream);

    public async Task<GameReply> CallAsync(GameCall call, CancellationToken cancellation)
    {
        JsonObject args = JsonNode.Parse(call.Parameters.GetRawText())!.AsObject();
        bool report = Reports(call, args);
        lock (_gate)
        {
            // A report is logged apart: it changes nothing.
            _log.Add(report ? "report:" + KeyOf(call) : KeyOf(call));
        }

        if (_sessionEnd.IsCancellationRequested)
        {
            return FakeGameLink.Refused("session_ended", "the session ended");
        }

        // As GameLink does: a call made for a world the link has left is refused unsent.
        if (call.World != null && call.World != WorldId)
        {
            lock (_gate)
            {
                _log.Add("refused_world " + KeyOf(call));
            }

            return FakeGameLink.Refused("world_changed", "the world changed");
        }

        if (Gate?.Invoke(call) is { } gate)
        {
            // As the mod does: the call runs when the gate opens even if its caller has given up (a late reply), and
            // its reply then settles it (OnReply); session_end cancels it unrun.
            return await LateAsync(call, gate).WaitAsync(cancellation);
        }

        if (CallDelay?.Invoke(call) is { } delay && delay > TimeSpan.Zero)
        {
            using CancellationTokenSource either = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _sessionEnd.Token);
            try
            {
                await Task.Delay(delay, either.Token);
            }
            catch (OperationCanceledException) when (_sessionEnd.IsCancellationRequested && !cancellation.IsCancellationRequested)
            {
                return FakeGameLink.Refused("cancelled", "session_end cancelled the call before it ran");
            }
        }

        GameReply reply = Answer(call, args, report);
        call.OnReply?.Invoke(reply);
        return reply;
    }

    private async Task<GameReply> LateAsync(GameCall call, Task gate)
    {
        GameReply reply;
        try
        {
            await gate.WaitAsync(_sessionEnd.Token);
            JsonObject args = JsonNode.Parse(call.Parameters.GetRawText())!.AsObject();
            reply = Answer(call, args, Reports(call, args));
        }
        catch (OperationCanceledException)
        {
            reply = FakeGameLink.Refused("cancelled", "session_end cancelled the call before it ran");
        }

        call.OnReply?.Invoke(reply);
        return reply;
    }

    private GameReply Answer(GameCall call, JsonObject args, bool report)
    {
        if (Override?.Invoke(call) is { } given)
        {
            return given;
        }

        return KeyOf(call) switch
        {
            "inspect.health" => Names(args),
            "read.logic" => Reads(args),
            "write.logic" => Writes(Entries(args, "writes"), report),
            "find.thing" => FakeGameLink.Ok("""{"things":[{"reference_id":"999","display_name":"Pump"}],"count":1,"total":1}"""),
            _ => FakeGameLink.Ok("{}")
        };
    }

    /// <summary>A verb call's verb and what ("write.logic"); a call with no what is its method alone.</summary>
    public static string KeyOf(GameCall call) =>
        JsonNode.Parse(call.Parameters.GetRawText())?["what"] is JsonValue what ? call.Method + "." + what : call.Method;

    // A write sent without confirm, of a what that takes confirm, is a report: the mod's, changing nothing.
    private static bool Reports(GameCall call, JsonObject args) =>
        args["confirm"] == null &&
        VerbCatalogue.Embedded.Verb(call.Method)?.What((string?)args["what"]) is { Write: not null } what &&
        what.Options.ContainsKey("confirm");

    // A list option's entries, or the call's own id (one or a list) with its other options as one entry each.
    private static JsonArray Entries(JsonObject args, string list)
    {
        if (args[list] is JsonArray given)
        {
            return given;
        }

        JsonArray entries = new();
        foreach (JsonNode? id in args["id"] as JsonArray ?? new JsonArray(args["id"]?.DeepClone()))
        {
            JsonObject entry = (JsonObject)args.DeepClone();
            entry["id"] = id?.DeepClone();
            entries.Add(entry);
        }

        return entries;
    }

    private static GameReply Names(JsonObject args)
    {
        JsonArray rows = new();
        foreach (JsonNode? id in args["id"] as JsonArray ?? new JsonArray(args["id"]?.DeepClone()))
        {
            rows.Add(new JsonObject { ["reference_id"] = id?.DeepClone(), ["display_name"] = "Device " + Text(id) });
        }

        return FakeGameLink.Ok(new JsonObject { ["results"] = rows }.ToJsonString());
    }

    private GameReply Reads(JsonObject args)
    {
        JsonArray rows = new();
        int index = 0;
        foreach (JsonNode? read in Entries(args, "reads"))
        {
            string id = Text(read!["id"]);
            string logic = Text(read["logic"]);
            rows.Add(new JsonObject
            {
                ["index"] = index++,
                ["ok"] = true,
                ["reference_id"] = id,
                ["logic_type"] = new JsonObject { ["name"] = logic },
                ["value"] = Value(id, logic)
            });
        }

        return FakeGameLink.Ok(new JsonObject { ["results"] = rows }.ToJsonString());
    }

    private GameReply Writes(JsonArray writes, bool report)
    {
        JsonArray rows = new();
        int index = 0;
        JsonArray targets = new();
        foreach (JsonNode? write in writes)
        {
            string id = Text(write!["id"]);
            string logic = Text(write["logic"]);
            double value = (double)write["value"]!.AsValue().GetValue<double>();
            double before = Value(id, logic);
            if (!report && RefusesWrite?.Invoke(id, logic, value) == true)
            {
                rows.Add(new JsonObject
                {
                    ["index"] = index++,
                    ["ok"] = false,
                    ["reference_id"] = id,
                    ["error"] = new JsonObject { ["code"] = "logic_not_writable", ["message"] = "the device refused it" }
                });
                continue;
            }

            if (!report)
            {
                Set(id, logic, value);
            }

            JsonObject target = VerbPipeline.Row(id, null, "Device " + id, JsonValue.Create(before), JsonValue.Create(value), "[undo]");
            target["logic"] = logic;
            targets.Add(target);
            rows.Add(new JsonObject
            {
                ["index"] = index++,
                ["ok"] = true,
                ["reference_id"] = id,
                ["logic_type"] = new JsonObject { ["name"] = logic },
                ["requested_value"] = value,
                ["previous_value"] = before,
                ["current_value"] = value
            });
        }

        if (report)
        {
            return FakeGameLink.Ok(new JsonObject { ["report_only"] = true, ["targets"] = targets }.ToJsonString());
        }

        return FakeGameLink.Ok(new JsonObject
        {
            ["results"] = rows,
            ["count"] = rows.Count,
            ["success_count"] = rows.Count,
            ["error_count"] = 0
        }.ToJsonString());
    }

    private static string Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : node?.ToJsonString() ?? "";

    public Task EndStreamAsync(long stream)
    {
        if (EndStreamThrows)
        {
            throw new IOException("the link broke");
        }

        lock (_gate)
        {
            Ended.Add(stream);
            _log.Add("stream_end");
        }

        return Task.CompletedTask;
    }

    /// <summary>When session_end came (null: not yet).</summary>
    public DateTime? SessionEndedAt { get; private set; }

    /// <summary>
    /// session_end, as the mod takes it: calls still waiting are answered cancelled, and from now on every call is
    /// refused session_ended unrun.
    /// </summary>
    public Task SessionEndAsync()
    {
        lock (_gate)
        {
            SessionEndedAt ??= DateTime.UtcNow;
            _log.Add("session_end");
        }

        _sessionEnd.Cancel();
        return Task.CompletedTask;
    }

    public Task<GameReply> HoldRestoreAsync(long stream, JsonElement change, string? world, CancellationToken cancellation)
    {
        if (world != null && world != WorldId)
        {
            lock (_gate)
            {
                _log.Add("refused_world hold_restore");
            }

            return Task.FromResult(FakeGameLink.Refused("world_changed", "the world changed"));
        }

        return HoldRestoreAsync(stream, change, cancellation);
    }

    public Task<GameReply> HoldRestoreAsync(long stream, JsonElement change, CancellationToken cancellation)
    {
        lock (_gate)
        {
            _log.Add("hold_restore " + change.GetRawText());
        }

        return Task.FromResult(OnHoldRestore?.Invoke(change) ?? FakeGameLink.Ok("""{"restores":[]}"""));
    }

    public Task<StreamCost[]> StreamCostsAsync(IReadOnlyList<long> streams, CancellationToken cancellation)
    {
        lock (_gate)
        {
            _log.Add("stream_costs");
        }

        OnStreamCosts?.Invoke();

        return Task.FromResult(new[] { new StreamCost(streams[0], Cost.Calls, Cost.MainThreadMs, Cost.OverBudgetFrames,
            Cost.Updates, Cost.UpdateMs) });
    }

    public Task<JsonElement?> WaitJobAsync(string jobId, TimeSpan timeout, CancellationToken cancellation) =>
        throw new NotSupportedException();

    public Task CursorCloseAsync(string cursorId) => Task.CompletedTask;

    public Task ReleaseAsync(long stream)
    {
        lock (_gate)
        {
            Released.Add(stream);
        }

        return Task.CompletedTask;
    }
}

/// <summary>A RunHost over a RunWorld with the real Python worker; skips the test when Python is missing.</summary>
internal sealed class RunBench : IAsyncDisposable
{
    private static readonly Lazy<string?> PythonProblem = new(FindPythonProblem);

    public RunBench(RunHostSettings? settings = null, Func<RunHostSettings, RunHostSettings>? adjust = null)
    {
        World = new RunWorld();
        Pipeline = new VerbPipeline(World, shown: Seen) { PollInterval = TimeSpan.Zero };
        RunHostSettings chosen = settings ?? new RunHostSettings
        {
            SgRoot = SgRoot,
            // Long enough that no stopped run's finally is cut off by a loaded machine; the tests that are about the
            // kill set their own.
            KillAfter = TimeSpan.FromSeconds(30),
            KillLatest = TimeSpan.FromSeconds(60)
        };
        Host = new RunHost(World, Pipeline, Seen, adjust != null ? adjust(chosen) : chosen);
    }

    public RunWorld World { get; }

    public SeenSet Seen { get; } = new();

    public VerbPipeline Pipeline { get; }

    public RunHost Host { get; }

    /// <summary>The folder holding sg: the copy shipped next to the binary, else the repository's.</summary>
    public static string SgRoot
    {
        get
        {
            string shipped = Path.Combine(AppContext.BaseDirectory, "python");
            if (File.Exists(Path.Combine(shipped, "sg", "worker.py")))
            {
                return shipped;
            }

            DirectoryInfo? folder = new(AppContext.BaseDirectory);
            while (folder != null && !File.Exists(Path.Combine(folder.FullName, "clients", "python", "sg", "worker.py")))
            {
                folder = folder.Parent;
            }

            return Path.Combine(folder!.FullName, "clients", "python");
        }
    }

    /// <summary>Why the run tests cannot run here (no Python), or null.</summary>
    public static string? MissingPython => PythonProblem.Value;

    /// <summary>
    /// Starts a run and returns once its latest sg call comes from the given line (it is inside that call, a wait say),
    /// so what the test does next meets the run where the test means it to: no reply wait races the worker's start.
    /// </summary>
    public async Task<string> StartAsync(string code, bool confirm, int line)
    {
        string text = await RunAsync(code, confirm, maxWait: 0);
        string id = text.Split(' ', 2)[0];
        await UntilAsync(() => Host.LineOf(id) == line, $"{id} reaching line {line}");
        return id;
    }

    /// <summary>Waits for a condition the run's own progress makes true (no deadline races: only a bound on a hang).</summary>
    public static async Task UntilAsync(Func<bool> condition, string what)
    {
        DateTime until = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, $"Timed out waiting for {what}.");
            await Task.Delay(10);
        }
    }

    /// <summary>Waits for the run to end.</summary>
    public Task EndedAsync(string id) => UntilAsync(() => !Host.Find(id)!.IsLive, $"{id} to end");

    public async Task<string> RunAsync(string code, bool confirm = false, double? maxWait = null)
    {
        JsonObject options = new() { ["code"] = code };
        if (confirm)
        {
            options["confirm"] = true;
        }

        if (maxWait is { } wait)
        {
            options["max_wait"] = wait;
        }

        VerbReply reply = await Host.RunScriptAsync(options, ReplyHooks.None);
        return Text(reply);
    }

    public static string Text(VerbReply? reply) =>
        reply is VerbReply.Done { Reply: JsonObject body } && body["text"] is JsonValue text
            ? text.GetValue<string>()
            : throw new Xunit.Sdk.XunitException("Expected a run's text, got " +
                                                 (reply is VerbReply.Failed failed ? failed.ToJson().ToJsonString() : "something else"));

    public async ValueTask DisposeAsync() => await Host.DisposeAsync();

    private static string? FindPythonProblem()
    {
        try
        {
            ProcessStartInfo start = new(OperatingSystem.IsWindows() ? "py" : "python3")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (OperatingSystem.IsWindows())
            {
                start.ArgumentList.Add("-3");
            }

            start.ArgumentList.Add("--version");
            using Process? process = Process.Start(start);
            if (process == null || !process.WaitForExit(20000) || process.ExitCode != 0)
            {
                return "Python 3 (py -3 or python3) did not run: the run tests need the real script worker.";
            }

            return null;
        }
        catch (Exception exception)
        {
            return $"Python 3 (py -3 or python3) is not installed ({exception.Message}): the run tests need the real script worker.";
        }
    }
}

/// <summary>
/// The tests that start Python workers or measure time: one collection, run on its own after the rest, so neither they
/// nor the rest of the suite steal the CPU from each other's timing.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PythonCollection
{
    public const string Name = "Python workers and timing";
}

/// <summary>A test that needs the real script worker: skipped, saying why, when Python 3 cannot run.</summary>
public sealed class PythonFactAttribute : FactAttribute
{
    public PythonFactAttribute()
    {
        if (RunBench.MissingPython is { } missing)
        {
            Skip = missing;
        }
    }
}
