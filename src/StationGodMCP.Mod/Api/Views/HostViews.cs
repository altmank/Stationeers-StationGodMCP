#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Api.Views;

/// <summary>
/// A reply: type reply, the call's id, ok, shaped (on a result: whether the call's shape was applied), the
/// result or the error, and, when the call reached a handler, its main-thread time, its wait in the queue and the frame
/// it ran in.
/// </summary>
internal sealed class CallReplyView
{
    private CallReplyView(string? id, bool ok, bool? shaped, object? result, ErrorView? error, double? elapsedMs,
        double? queueMs, long? frame)
    {
        Id = id;
        Ok = ok;
        Shaped = shaped;
        Result = result;
        Error = error;
        ElapsedMs = elapsedMs;
        QueueMs = queueMs;
        Frame = frame;
    }

    public string Type => "reply";

    [JsonProperty(NullValueHandling = NullValueHandling.Include)]
    public string? Id { get; }

    public bool Ok { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public bool? Shaped { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public object? Result { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ErrorView? Error { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public double? ElapsedMs { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public double? QueueMs { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public long? Frame { get; }

    internal static CallReplyView Of(string? id, object result, bool shaped, double elapsedMs, double queueMs, long frame) =>
        new CallReplyView(id, true, shaped, result, null, elapsedMs, queueMs, frame);

    /// <summary>An error from a handler: with its times and frame.</summary>
    internal static CallReplyView Failed(string? id, ErrorView error, double elapsedMs, double queueMs, long frame) =>
        new CallReplyView(id, false, null, null, error, elapsedMs, queueMs, frame);

    /// <summary>An error for a call that never reached a handler (refused, timed out, cancelled).</summary>
    internal static CallReplyView Refused(string? id, ErrorView error) =>
        new CallReplyView(id, false, null, null, error, null, null, null);
}

/// <summary>game_clock: the game's clock, for clients that must count game time.</summary>
internal sealed class GameClockView
{
    internal GameClockView(float gameTimeS, bool paused, float timeOfDayRatio, uint daysPast)
    {
        GameTimeS = gameTimeS;
        Paused = paused;
        TimeOfDayRatio = timeOfDayRatio;
        DaysPast = daysPast;
    }

    /// <summary>Unity Time.time: stops while paused and restarts from zero on every launch; use differences.</summary>
    public float GameTimeS { get; }

    public bool Paused { get; }

    /// <summary>Fraction of the local day, 0 to 1, from the planet's accumulated rotation.</summary>
    public float TimeOfDayRatio { get; }

    public uint DaysPast { get; }
}

/// <summary>mod_info: the mod's identity, each method's counters since it loaded, and its reflection health.</summary>
internal sealed class ModInfoView
{
    internal ModInfoView(ModIdentity identity, List<MethodStatsView> methods, List<ReflectionMemberView> reflection,
        int missingCount, RuntimeView runtime)
    {
        ModId = identity.ModId;
        ModVersion = identity.ModVersion;
        AssemblyVersion = identity.AssemblyVersion;
        InformationalVersion = identity.InformationalVersion;
        PipeName = identity.PipeName;
        Methods = methods;
        Count = methods.Count;
        Reflection = reflection;
        MissingCount = missingCount;
        Runtime = runtime;
    }

    public string ModId { get; }

    public string ModVersion { get; }

    public string? AssemblyVersion { get; }

    public string? InformationalVersion { get; }

    /// <summary>The name of the local named pipe this game listens on (after \\.\pipe\).</summary>
    public string PipeName { get; }

    public List<MethodStatsView> Methods { get; }

    public int Count { get; }

    public List<ReflectionMemberView> Reflection { get; }

    /// <summary>Required game members not found: methods that need one answer game_changed.</summary>
    public int MissingCount { get; }

    /// <summary>What the mod costs the game: per-method timings and sizes, per-frame load, the collector and heap.</summary>
    public RuntimeView Runtime { get; }
}

internal sealed class ModIdentity
{
    internal ModIdentity(string modId, string modVersion, string? assemblyVersion, string? informationalVersion,
        string pipeName)
    {
        ModId = modId;
        ModVersion = modVersion;
        AssemblyVersion = assemblyVersion;
        InformationalVersion = informationalVersion;
        PipeName = pipeName;
    }

    internal string ModId { get; }

    internal string ModVersion { get; }

    internal string? AssemblyVersion { get; }

    internal string? InformationalVersion { get; }

    internal string PipeName { get; }
}

/// <summary>One method's calls, errors and main-thread time since the mod loaded.</summary>
internal sealed class MethodStatsView
{
    private const int Decimals = 2;

    internal MethodStatsView(string method, long calls, long errors, double totalMs, double maxMs)
    {
        Method = method;
        Calls = calls;
        Errors = errors;
        TotalMs = System.Math.Round(totalMs, Decimals);
        MeanMs = calls > 0 ? System.Math.Round(totalMs / calls, Decimals) : null;
        MaxMs = maxMs;
    }

    public string Method { get; }

    public long Calls { get; }

    public long Errors { get; }

    public double TotalMs { get; }

    /// <summary>Null before the first call.</summary>
    public double? MeanMs { get; }

    public double MaxMs { get; }
}

/// <summary>A game member reached by reflection or patched, and whether this build of the game has it.</summary>
internal sealed class ReflectionMemberView
{
    internal ReflectionMemberView(string member, bool resolved, bool optional)
    {
        Member = member;
        Resolved = resolved;
        Optional = optional;
    }

    public string Member { get; }

    public bool Resolved { get; }

    /// <summary>A member of another mod (Terraforming Reloaded), not the game.</summary>
    public bool Optional { get; }
}

/// <summary>The TCP transport's reply to a good authentication line.</summary>
internal sealed class AuthAcceptedView
{
    public bool Ok => true;
}

/// <summary>The TCP transport's reply to a bad authentication line, before it closes the connection.</summary>
internal sealed class AuthRefusedView
{
    internal AuthRefusedView(ErrorView error)
    {
        Error = error;
    }

    public bool Ok => false;

    public ErrorView Error { get; }
}
