#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;

namespace StationGodMCP.Api.Shared.Game.Structures;

/// <summary>
/// The prefabs a swap may build: loaded (Prefab.Find), buildable from some kit (listed in a MultiConstructor's
/// Constructables), not a placement cursor, with build states, and of the family's plain class. Always the registered
/// prefab from Prefab.Find: a Constructables entry is the source asset, whose GridBounds is empty (Prefab.Register
/// computes it on the registered copy only).
/// </summary>
internal sealed class StructureTargets
{
    private readonly HashSet<int> _kitPlaced;

    private StructureTargets(HashSet<int> kitPlaced)
    {
        _kitPlaced = kitPlaced;
    }

    internal static StructureTargets Load()
    {
        HashSet<int> placed = new HashSet<int>();
        foreach (Thing prefab in Prefab.AllPrefabs)
        {
            if (!(prefab is MultiConstructor kit) || kit.Constructables == null)
            {
                continue;
            }

            foreach (Structure constructable in kit.Constructables)
            {
                if (constructable != null)
                {
                    placed.Add(constructable.PrefabHash);
                }
            }
        }

        return new StructureTargets(placed);
    }

    /// <summary>The registered prefab of this name; null when none is loaded.</summary>
    internal static Structure? Named(string name) =>
        Prefab.Find(name) is Structure named && named != null ? Registered(named.PrefabHash) : null;

    internal static Structure? Registered(int prefabHash) =>
        Prefab.Find(prefabHash) is Structure prefab && prefab != null ? prefab : null;

    /// <summary>Why the prefab cannot be built by a swap of this family; null when it can.</summary>
    internal string? IssueOf(Structure prefab, StructureFamily family)
    {
        if (!family.IsPlain(prefab))
        {
            return $"{prefab.PrefabName} is a {prefab.GetType().Name}, not {family.PlainClasses}.";
        }

        if (prefab.IsCursor)
        {
            return $"{prefab.PrefabName} is a placement cursor.";
        }

        if (prefab.BuildStates == null || prefab.BuildStates.Count == 0)
        {
            return $"{prefab.PrefabName} has no build states.";
        }

        return _kitPlaced.Contains(prefab.PrefabHash)
            ? null
            : $"No loaded kit builds {prefab.PrefabName} (no MultiConstructor lists it)." +
              Build.KitBuiltNames.HintFor(prefab, _kitPlaced);
    }
}
