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

    public Task<GameReply> CallAsync(GameCall call, CancellationToken cancellation)
    {
        lock (_gate)
        {
            _log.Add(call.Method);
        }

        if (Override?.Invoke(call) is { } given)
        {
            return Task.FromResult(given);
        }

        JsonObject args = JsonNode.Parse(call.Parameters.GetRawText())!.AsObject();
        return Task.FromResult(call.Method switch
        {
            "thing_health" => Names(args),
            "read_logic_many" => Reads(args),
            "write_logic_many" => Writes(args["writes"]!.AsArray()),
            "write_logic" => Writes(new JsonArray(args.DeepClone())),
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

    private GameReply Writes(JsonArray writes)
    {
        JsonArray rows = new();
        int index = 0;
        foreach (JsonNode? write in writes)
        {
            string id = Text(write!["reference_id"] ?? write["id"]);
            string logic = Text(write["logic_type"] ?? write["logic"]);
            double value = (double)write["value"]!.AsValue().GetValue<double>();
            double before = Value(id, logic);
            Set(id, logic, value);
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
        lock (_gate)
        {
            Ended.Add(stream);
            _log.Add("stream_end");
        }

        return Task.CompletedTask;
    }

    public Task SessionEndAsync() => Task.CompletedTask;

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
