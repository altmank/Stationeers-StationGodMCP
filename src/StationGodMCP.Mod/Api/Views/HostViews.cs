#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Api.Views;

/// <summary>A reply: the request's id, ok true, the tool's result, and the main-thread time it took.</summary>
internal sealed class ReplyView
{
    internal ReplyView(string? id, object result, double elapsedMs)
    {
        Id = id;
        Result = result;
        ElapsedMs = elapsedMs;
    }

    // Serialised as given: an id is the client's, and null must still be sent.
    [JsonProperty(NullValueHandling = NullValueHandling.Include)]
    public string? Id { get; }

    public bool Ok => true;

    public object Result { get; }

    public double ElapsedMs { get; }
}

/// <summary>A refused or failed request: the request's id, ok false, the error, and the time it took.</summary>
internal sealed class ErrorReplyView
{
    internal ErrorReplyView(string? id, ErrorView error, double? elapsedMs)
    {
        Id = id;
        Error = error;
        ElapsedMs = elapsedMs;
    }

    [JsonProperty(NullValueHandling = NullValueHandling.Include)]
    public string? Id { get; }

    public bool Ok => false;

    public ErrorView Error { get; }

    /// <summary>Null when the request never reached a tool (the pipe's own timeout).</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public double? ElapsedMs { get; }
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
        int missingCount)
    {
        ModId = identity.ModId;
        ModVersion = identity.ModVersion;
        AssemblyVersion = identity.AssemblyVersion;
        InformationalVersion = identity.InformationalVersion;
        Methods = methods;
        Count = methods.Count;
        Reflection = reflection;
        MissingCount = missingCount;
    }

    public string ModId { get; }

    public string ModVersion { get; }

    public string? AssemblyVersion { get; }

    public string? InformationalVersion { get; }

    public List<MethodStatsView> Methods { get; }

    public int Count { get; }

    public List<ReflectionMemberView> Reflection { get; }

    /// <summary>Required game members not found: methods that need one answer game_changed.</summary>
    public int MissingCount { get; }
}

internal sealed class ModIdentity
{
    internal ModIdentity(string modId, string modVersion, string? assemblyVersion, string? informationalVersion)
    {
        ModId = modId;
        ModVersion = modVersion;
        AssemblyVersion = assemblyVersion;
        InformationalVersion = informationalVersion;
    }

    internal string ModId { get; }

    internal string ModVersion { get; }

    internal string? AssemblyVersion { get; }

    internal string? InformationalVersion { get; }
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
