#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using StationGodMCP.Client;
using StationGodMCP.Server;
using Xunit;

namespace StationGodMCP.Tests.Hub;

/// <summary>An IGameLink that answers each call from Answer (every call {} by default) and keeps every call it got.</summary>
internal sealed class FakeGameLink : IGameLink
{
    public ConcurrentQueue<GameCall> Calls { get; } = new();

    // The player's id is asked before a move to "player"; a logic write's dry run reports each write as the mod's
    // does; every other call answers {}.
    public Func<GameCall, GameReply> Answer { get; set; } = call => call.Method switch
    {
        "player_vitals" => Ok("""{"reference_id":"77"}"""),
        "write_logic" or "write_logic_many" when call.Params()["dry_run"] is JsonValue dry && (bool)dry => LogicReport(call),
        _ => Ok("{}")
    };

    /// <summary>write_logic(_many)'s dry run: each write with the value there now (0), nothing written.</summary>
    public static GameReply LogicReport(GameCall call)
    {
        JsonObject args = call.Params();
        JsonArray writes = args["writes"] as JsonArray ?? new JsonArray(args.DeepClone());
        JsonArray rows = new();
        foreach (JsonNode? write in writes)
        {
            rows.Add(new JsonObject
            {
                ["index"] = rows.Count,
                ["ok"] = true,
                ["reference_id"] = write!["reference_id"]?.DeepClone(),
                ["logic_type"] = new JsonObject { ["name"] = write["logic_type"]?.DeepClone() },
                ["requested_value"] = write["value"]?.DeepClone(),
                ["previous_value"] = 0,
                ["opens"] = false
            });
        }

        JsonObject report = args["writes"] is JsonArray
            ? new JsonObject { ["report_only"] = true, ["problems"] = new JsonArray(), ["warnings"] = new JsonArray(), ["results"] = rows }
            : (JsonObject)rows[0]!.DeepClone();
        report["report_only"] = true;
        return Ok(report.ToJsonString());
    }

    /// <summary>The jobs topic: the job's facts, or null (a timeout, or early after a reconnect); unset: no job waits.</summary>
    public Func<string, JsonElement?>? WaitJob { get; set; }

    public List<string> Waited { get; } = new();

    public string? WorldId => "world-1";

    public event Action<string?>? WorldChanged;

    public void ChangeWorld(string? world) => WorldChanged?.Invoke(world);

    public static GameReply Ok(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return new GameReply.Ok(document.RootElement.Clone(), 1, 0.1, false);
    }

    public static GameReply Refused(string code, string message) => new GameReply.Refused(code, message, null);

    public IReadOnlyList<GameCall> CallsTo(string method) => Calls.Where(call => call.Method == method).ToList();

    public GameCall Single(string method) => Assert.Single(CallsTo(method));

    public long NewStream() => throw new NotSupportedException();

    public Task<GameReply> CallAsync(GameCall call, CancellationToken cancellation)
    {
        Calls.Enqueue(call);
        return Task.FromResult(Answer(call));
    }

    public Task EndStreamAsync(long stream) => throw new NotSupportedException();

    public Task SessionEndAsync() => Task.CompletedTask;

    public Task<GameReply> HoldRestoreAsync(long stream, JsonElement change, CancellationToken cancellation) =>
        throw new NotSupportedException();

    public Task<StreamCost[]> StreamCostsAsync(IReadOnlyList<long> streams, CancellationToken cancellation) =>
        throw new NotSupportedException();

    public Task<JsonElement?> WaitJobAsync(string jobId, TimeSpan timeout, CancellationToken cancellation)
    {
        if (WaitJob == null)
        {
            throw new NotSupportedException();
        }

        Waited.Add(jobId);
        return Task.FromResult(WaitJob(jobId));
    }

    /// <summary>A pipeline over this link with job polls that do not wait.</summary>
    public VerbPipeline Pipeline(IWriteGate? gate = null, CallRecorder? recorder = null) =>
        new(this, gate, recorder) { PollInterval = TimeSpan.Zero };
}

/// <summary>Short ways to call the pipeline and read its replies.</summary>
internal static class Verbs
{
    public static JsonObject Options(string json) => JsonNode.Parse(json)!.AsObject();

    public static VerbReply Call(this VerbPipeline pipeline, string verb, string? what, string options = "{}",
        Face face = Face.Direct) =>
        pipeline.CallAsync(new VerbCall(verb, what, Options(options), face), CancellationToken.None).GetAwaiter().GetResult();

    public static JsonNode? Done(this VerbReply reply) =>
        reply is VerbReply.Done done
            ? done.Reply
            : throw new Xunit.Sdk.XunitException("Expected a reply, got " + ((VerbReply.Failed)reply).ToJson().ToJsonString());

    public static VerbReply.Failed Failed(this VerbReply reply) =>
        reply as VerbReply.Failed ??
        throw new Xunit.Sdk.XunitException("Expected a failure, got " + ((VerbReply.Done)reply).Reply?.ToJsonString());

    public static JsonObject Params(this GameCall call) => JsonNode.Parse(call.Parameters.GetRawText())!.AsObject();

    public static JsonObject RenameArgs(this GameCall call) =>
        JsonNode.Parse(call.Rename!.Value.GetRawText())!["args"]!.AsObject();

    public static JsonObject RenameTools(this GameCall call) =>
        JsonNode.Parse(call.Rename!.Value.GetRawText())!["tools"]!.AsObject();

    /// <summary>Asserts the same JSON, keys in any order.</summary>
    public static void Same(string expected, JsonNode? actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), actual), $"expected {expected}, got {actual?.ToJsonString()}");

    /// <summary>The problems of an invalid_argument, as "path: problem".</summary>
    public static List<string> Problems(this VerbReply.Failed failed)
    {
        Assert.Equal("invalid_argument", failed.Code);
        List<string> problems = new();
        foreach (JsonNode? problem in failed.Data!["problems"]!.AsArray())
        {
            problems.Add((string)problem!["path"]! + ": " + (string)problem["problem"]!);
        }

        return problems;
    }
}
