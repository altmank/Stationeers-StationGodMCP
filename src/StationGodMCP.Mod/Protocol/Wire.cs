#nullable enable

using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Protocol;

/// <summary>
/// What the server says about itself in welcome: identity, the world it serves and the game's state. The main thread
/// publishes a new snapshot when anything in it changes; connection threads only read the current one, so no game
/// object is touched off the main thread.
/// </summary>
internal sealed class ServerFacts
{
    private static ServerFacts _current = new ServerFacts(string.Empty, string.Empty, false, WorldFacts.None, "Unknown");

    internal ServerFacts(string modVersion, string pipeName, bool dedicated, WorldFacts world, string gameState)
    {
        ModVersion = modVersion;
        PipeName = pipeName;
        Dedicated = dedicated;
        World = world;
        GameState = gameState;
    }

    /// <summary>Random, new each time the mod loads.</summary>
    internal static string InstanceId { get; } = RandomHex(8);

    internal static ServerFacts Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }

    internal string ModVersion { get; }

    internal string PipeName { get; }

    internal bool Dedicated { get; }

    internal WorldFacts World { get; }

    internal string GameState { get; }

    /// <summary>Lowercase hex of that many random bytes.</summary>
    internal static string RandomHex(int bytes)
    {
        byte[] random = new byte[bytes];
        using (RandomNumberGenerator generator = RandomNumberGenerator.Create())
        {
            generator.GetBytes(random);
        }

        StringBuilder hex = new StringBuilder(bytes * 2);
        foreach (byte value in random)
        {
            hex.Append(value.ToString("x2"));
        }

        return hex.ToString();
    }
}

/// <summary>The world a connection reached: a random id new on every world load, the save's name, the epoch.</summary>
internal sealed class WorldFacts
{
    internal static readonly WorldFacts None = new WorldFacts(string.Empty, null, 0);

    internal WorldFacts(string id, string? save, long epoch)
    {
        Id = id;
        Save = save;
        Epoch = epoch;
    }

    public string Id { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Include)]
    public string? Save { get; }

    public long Epoch { get; }
}

/// <summary>The welcome message: who the client is now, which server and world it reached, and the limits.</summary>
internal sealed class WelcomeView
{
    internal WelcomeView(string clientId, string client, WelcomeServerView server, WelcomeCatalogueView catalogue,
        WelcomeLimitsView limits, IReadOnlyList<string> features)
    {
        ClientId = clientId;
        Client = client;
        Server = server;
        Catalogue = catalogue;
        Limits = limits;
        Features = features;
    }

    public string Type => "welcome";

    public int Protocol => 2;

    public string ClientId { get; }

    public string Client { get; }

    public WelcomeServerView Server { get; }

    public WelcomeCatalogueView Catalogue { get; }

    public WelcomeLimitsView Limits { get; }

    public IReadOnlyList<string> Features { get; }
}

internal sealed class WelcomeServerView
{
    internal WelcomeServerView(ServerFacts facts, string transport)
    {
        ModVersion = facts.ModVersion;
        InstanceId = ServerFacts.InstanceId;
        PipeName = facts.PipeName;
        Transport = transport;
        Dedicated = facts.Dedicated;
        World = facts.World;
        GameState = facts.GameState;
    }

    public string ModVersion { get; }

    public string InstanceId { get; }

    public string PipeName { get; }

    public string Transport { get; }

    public string Role => "host";

    public bool Dedicated { get; }

    public WorldFacts World { get; }

    public string GameState { get; }
}

internal sealed class WelcomeCatalogueView
{
    internal WelcomeCatalogueView(string hash, int methods, int protocolMethods)
    {
        Hash = hash;
        Methods = methods;
        ProtocolMethods = protocolMethods;
    }

    public string Hash { get; }

    public int Methods { get; }

    public int ProtocolMethods { get; }
}

internal sealed class WelcomeLimitsView
{
    internal WelcomeLimitsView(int maxInFlight, int maxRequestBytes, int maxReplyBytes,
        StationGodMCP.Pure.Subscriptions.SubscriptionLimits? subscriptions = null)
    {
        MaxInFlight = maxInFlight;
        MaxRequestBytes = maxRequestBytes;
        MaxReplyBytes = maxReplyBytes;
        MaxSubscriptions = subscriptions?.MaxSubscriptions;
        MaxSubscriptionValues = subscriptions?.MaxConnectionValues;
        MaxValuesPerSubscription = subscriptions?.MaxValuesPerSubscription;
        MinSubscriptionIntervalS = subscriptions != null
            ? StationGodMCP.Pure.Subscriptions.SamplingInterval.MinimumSeconds
            : null;
    }

    public int MaxInFlight { get; }

    public int MaxRequestBytes { get; }

    public int MaxReplyBytes { get; }

    /// <summary>Subscriptions one connection may hold; absent when the server has none.</summary>
    [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public int? MaxSubscriptions { get; }

    /// <summary>Values across one connection's subscriptions.</summary>
    [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public int? MaxSubscriptionValues { get; }

    /// <summary>Values one subscription may read (one read_devices call's).</summary>
    [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public int? MaxValuesPerSubscription { get; }

    [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public double? MinSubscriptionIntervalS { get; }
}

/// <summary>An event with no keys of its own (ping).</summary>
internal sealed class EventView
{
    internal EventView(string name) => Event = name;

    public string Type => "event";

    public string Event { get; }
}

/// <summary>world_changed: a world finished loading; every reference id may now name something else.</summary>
internal sealed class WorldChangedView
{
    internal WorldChangedView(WorldFacts world) => World = world;

    public string Type => "event";

    public string Event => "world_changed";

    public WorldFacts World { get; }
}

/// <summary>goodbye: the server closes the connection on purpose, and why.</summary>
internal sealed class GoodbyeView
{
    internal GoodbyeView(string reason) => Reason = reason;

    public string Type => "goodbye";

    public string Reason { get; }
}

/// <summary>The server's version-2 messages as lines, written with the mod's serialiser settings on any thread.</summary>
internal static class Wire
{
    internal static string Line(object message) => ApiJson.WriteFresh(message);

    internal static string Refusal(string? id, string code, string message, object? data = null) =>
        ApiJson.WriteFresh(CallReplyView.Refused(id, new ErrorView(code, message, data)));
}
