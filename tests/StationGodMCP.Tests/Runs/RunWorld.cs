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
    public void ChangeWorldQuietly(string world) => WorldId = world;

    public StreamCost Cost { get; set; } = new(0, 12, 48.0, 2);

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

    /// <summary>The calls and hold_restores so far, in order: "method" or "hold_restore {change}".</summary>
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
        bool report = args["dry_run"] is JsonValue dry && dry.GetValueKind() == JsonValueKind.True;
        lock (_gate)
        {
            // A dry run is logged apart: it reports and changes nothing.
            _log.Add(report ? "report:" + call.Method : call.Method);
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
                _log.Add("refused_world " + call.Method);
            }

            return FakeGameLink.Refused("world_changed", "the world changed");
        }

        if (Gate?.Invoke(call) is { } gate)
        {
            using CancellationTokenSource either = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _sessionEnd.Token);
            try
            {
                await gate.WaitAsync(either.Token);
            }
            catch (OperationCanceledException) when (_sessionEnd.IsCancellationRequested && !cancellation.IsCancellationRequested)
            {
                return FakeGameLink.Refused("cancelled", "session_end cancelled the call before it ran");
            }
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

        if (Override?.Invoke(call) is { } given)
        {
            return given;
        }

        return (call.Method switch
        {
            "thing_health" => Names(args),
            "read_logic_many" => Reads(args),
            "write_logic_many" => Writes(args["writes"]!.AsArray(), report),
            "write_logic" => Writes(new JsonArray(args.DeepClone()), report),
            "find_things" => FakeGameLink.Ok("""{"things":[{"reference_id":"999","display_name":"Pump"}],"count":1,"total":1}"""),
            _ => FakeGameLink.Ok("{}")
        });
    }

    private static GameReply Names(JsonObject args)
    {
        JsonArray rows = new();
        foreach (JsonNode? id in args["reference_ids"] as JsonArray ?? new JsonArray(args["reference_id"]?.DeepClone()))
        {
            rows.Add(new JsonObject { ["reference_id"] = id?.DeepClone(), ["display_name"] = "Device " + Text(id) });
        }

        return FakeGameLink.Ok(new JsonObject { ["results"] = rows }.ToJsonString());
    }

    private GameReply Reads(JsonObject args)
    {
        JsonArray rows = new();
        int index = 0;
        foreach (JsonNode? read in args["reads"]!.AsArray())
        {
            string id = Text(read!["reference_id"] ?? read["id"]);
            string logic = Text(read["logic_type"] ?? read["logic"]);
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
        foreach (JsonNode? write in writes)
        {
            string id = Text(write!["reference_id"] ?? write["id"]);
            string logic = Text(write["logic_type"] ?? write["logic"]);
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

        return FakeGameLink.Ok(new JsonObject
        {
            ["report_only"] = report,
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

        return Task.FromResult(FakeGameLink.Ok("""{"restores":[]}"""));
    }

    public Task<StreamCost[]> StreamCostsAsync(IReadOnlyList<long> streams, CancellationToken cancellation)
    {
        lock (_gate)
        {
            _log.Add("stream_costs");
        }

        return Task.FromResult(new[] { new StreamCost(streams[0], Cost.Calls, Cost.MainThreadMs, Cost.OverBudgetFrames) });
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
            KillAfter = TimeSpan.FromSeconds(1),
            KillLatest = TimeSpan.FromSeconds(3)
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
        DateTime until = DateTime.UtcNow + TimeSpan.FromSeconds(30);
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
