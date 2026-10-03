#nullable enable

using System;
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

    /// <summary>The id WorldScope gave the loaded world (WorldStores.WorldId).</summary>
    internal static WorldId Of(string value) =>
        string.IsNullOrEmpty(value) ? throw new ArgumentException("A world id is never empty.", nameof(value)) : new WorldId(value);

    /// <summary>An id from 8 bytes.</summary>
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
