#nullable enable

using System;
using System.Globalization;

namespace StationGodMCP.Pure.Subscriptions;

/// <summary>A connection as the protocol names it (welcome.client_id, e.g. "c4"); unique while the mod runs.</summary>
internal readonly struct ConnectionId : IEquatable<ConnectionId>
{
    internal ConnectionId(string value)
    {
        Value = string.IsNullOrEmpty(value)
            ? throw new ArgumentException("A connection id is never empty.", nameof(value))
            : value;
    }

    internal string Value { get; }

    public bool Equals(ConnectionId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is ConnectionId other && Equals(other);

    public override int GetHashCode() => Value == null ? 0 : StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;
}

/// <summary>
/// A subscription's id on the wire: "s" and a number, unique while the mod runs, so an id never comes back to name
/// another subscription after the first one ended.
/// </summary>
internal readonly struct SubscriptionId : IEquatable<SubscriptionId>
{
    private const string Prefix = "s";

    internal SubscriptionId(long number)
    {
        Number = number > 0
            ? number
            : throw new ArgumentOutOfRangeException(nameof(number), "Subscription ids start at s1.");
    }

    internal long Number { get; }

    /// <summary>The id a client sent ("s3"); false for anything else.</summary>
    internal static bool TryParse(string? text, out SubscriptionId id)
    {
        id = default;
        if (text == null || !text.StartsWith(Prefix, StringComparison.Ordinal) ||
            !long.TryParse(text.Substring(Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture,
                out long number) || number <= 0)
        {
            return false;
        }

        id = new SubscriptionId(number);
        return true;
    }

    public bool Equals(SubscriptionId other) => Number == other.Number;

    public override bool Equals(object? obj) => obj is SubscriptionId other && Equals(other);

    public override int GetHashCode() => Number.GetHashCode();

    public override string ToString() => Prefix + Number.ToString(CultureInfo.InvariantCulture);
}
