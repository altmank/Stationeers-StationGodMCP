#nullable enable

using System;
using System.Security.Cryptography;
using System.Text;

namespace StationGodMCP.Pure.Subscriptions;

/// <summary>
/// A loaded world's identity for clients (welcome.server.world.id): 16 lowercase hex digits, random, new each time a
/// world finishes loading. Two equal ids mean the same loaded world; reference ids are only comparable under one.
/// </summary>
internal readonly struct WorldId : IEquatable<WorldId>
{
    internal const int Length = 16;

    private WorldId(string value)
    {
        Value = value;
    }

    internal string Value { get; }

    /// <summary>An id from 8 random bytes.</summary>
    internal static WorldId FromBytes(byte[] bytes)
    {
        if (bytes == null || bytes.Length != Length / 2)
        {
            throw new ArgumentException($"A world id is made of {Length / 2} bytes.", nameof(bytes));
        }

        StringBuilder hex = new StringBuilder(Length);
        foreach (byte value in bytes)
        {
            hex.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return new WorldId(hex.ToString());
    }

    public bool Equals(WorldId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is WorldId other && Equals(other);

    public override int GetHashCode() => Value == null ? 0 : StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;
}

/// <summary>Where new world ids come from: random in the mod, scripted in tests.</summary>
internal interface IWorldIdSource
{
    WorldId Next();
}

/// <summary>World ids from the operating system's cryptographic random source.</summary>
internal sealed class RandomWorldIds : IWorldIdSource
{
    private readonly RandomNumberGenerator _random = RandomNumberGenerator.Create();

    public WorldId Next()
    {
        byte[] bytes = new byte[WorldId.Length / 2];
        _random.GetBytes(bytes);
        return WorldId.FromBytes(bytes);
    }
}

/// <summary>
/// Follows the game frame by frame and mints a new WorldId the first frame a world is running after none was (a save
/// finished loading, a new game started, or the mod loaded into a running world). Leaving a world keeps the last id
/// until the next one loads, so a client reconnecting from the menu learns that its world is gone only when another
/// one is up; Current is absent until the first world loads.
/// </summary>
internal sealed class WorldIdentity
{
    private readonly IWorldIdSource _source;
    private bool _running;
    private WorldId _current;
    private bool _hasWorld;

    internal WorldIdentity(IWorldIdSource source)
    {
        _source = source;
    }

    /// <summary>
    /// One frame's state (the same "running" WorldStores uses: GameState neither None nor Loading). True when this
    /// frame finished loading a world, which then has a new id.
    /// </summary>
    internal bool Observe(bool worldRunning)
    {
        bool loaded = worldRunning && !_running;
        _running = worldRunning;
        if (loaded)
        {
            _current = _source.Next();
            _hasWorld = true;
        }

        return loaded;
    }

    /// <summary>The loaded world's id; false before any world has loaded.</summary>
    internal bool TryGetCurrent(out WorldId world)
    {
        world = _current;
        return _hasWorld;
    }
}
