#nullable enable

using System;
using System.Globalization;

namespace StationGodMCP.Api.Shared;

/// <summary>The prefab a move out of a silo names: by its name or by its hash.</summary>
internal sealed class SiloPrefabChoice
{
    private SiloPrefabChoice(string? name, int? hash)
    {
        Name = name;
        Hash = hash;
    }

    internal string? Name { get; }

    internal int? Hash { get; }

    internal static SiloPrefabChoice Named(string name) => new SiloPrefabChoice(name.Trim(), null);

    internal static SiloPrefabChoice Hashed(int hash) => new SiloPrefabChoice(null, hash);

    /// <summary>
    /// Whether a stored entry is this prefab: its name, any case, or its hash as the silo's logic stack computes it
    /// (Animator.StringToHash of the saved PrefabName, Silo.RebuildContentsStack).
    /// </summary>
    internal bool Matches(string? prefabName, int prefabHash) =>
        Hash.HasValue ? Hash.Value == prefabHash : string.Equals(prefabName, Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>The prefab as the request named it, for a refusal.</summary>
    internal string Asked => Name ?? Hash!.Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>One thing a deposit names by id, whole (quantity null) or part of a stack.</summary>
internal sealed class SiloDepositPick
{
    internal SiloDepositPick(ThingId id, double? quantity)
    {
        Id = id;
        Quantity = quantity;
    }

    internal ThingId Id { get; }

    internal double? Quantity { get; }
}
