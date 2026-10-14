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

    // A logic write sent as a report (confirm not true: the hub sends confirm: false on its report path) answers each
    // write as a report row; every other call answers {}.
    public Func<GameCall, GameReply> Answer { get; set; } = call =>
        call.Method == "write" && (string?)call.Params()["what"] == "logic" && (bool?)call.Params()["confirm"] != true
            ? LogicReport(call)
            : Ok("{}");

    /// <summary>write logic's report: a row per write with the value there now (0), nothing written.</summary>
    public static GameReply LogicReport(GameCall call)
    {
        JsonObject args = call.Params();
        JsonArray writes = args["writes"] as JsonArray ?? new JsonArray();
        foreach (JsonNode? id in args["id"] as JsonArray ?? new JsonArray(args["id"]?.DeepClone()))
        {
            writes.Add(new JsonObject { ["id"] = id?.DeepClone(), ["value"] = args["value"]?.DeepClone() });
        }

        JsonArray rows = new();
        foreach (JsonNode? write in writes)
        {
            string? id = (string?)write!["id"];
            rows.Add(new JsonObject
            {
                ["id"] = id,
                ["current"] = 0,
                ["new"] = write["value"]?.DeepClone(),
                ["undo"] = "[undo]",
                ["text"] = $"[{id}, ?, 0 -> {write["value"]?.ToJsonString()}] [undo]"
            });
        }

        return Ok(new JsonObject { ["report_only"] = true, ["targets"] = rows }.ToJsonString());
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

    /// <summary>The one call a verb call sends to the mod (its wire form).</summary>
    public static GameCall Sent(string verb, string? what, string options, Face face = Face.Direct, IWriteGate? gate = null)
    {
        FakeGameLink game = new();
        game.Pipeline(gate).Call(verb, what, options, face).Done();
        return Assert.Single(game.Calls);
    }

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
