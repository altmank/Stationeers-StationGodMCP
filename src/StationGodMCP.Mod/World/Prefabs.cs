#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using Reagents;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Build;
using StationGodMCP.Api.Shared.Game.Structures;
using StationGodMCP.Api.Shared.Game.Upgrades;

namespace StationGodMCP.WorldModel;

/// <summary>
/// The static data of the loaded prefabs (cost design A1), built once per load and kept: the build catalogue, the
/// swap targets, each family's kits, prefabs by name ignoring case, the ingot each reagent makes. Built on first use
/// after a load and again only when the world changes (WorldStores.Epoch), the prefab list grows (the mod registers its
/// own prefabs after the game's) or the game's placement cursors are made (InventoryManager sets them up once a world
/// runs); a call never walks the prefab list. Generation counts the rebuilds, so derived data kept elsewhere
/// (CarriedIgnition's ignition list) can follow. Main thread.
/// </summary>
internal static class Prefabs
{
    private static readonly Dictionary<Type, KitCatalogue> Kits = new Dictionary<Type, KitCatalogue>();
    private static long _epoch = -1;
    private static int _prefabCount = -1;
    private static int _cursorCount = -1;
    private static BuildCatalogue? _build;
    private static StructureTargets? _targets;
    private static Dictionary<string, Thing>? _byName;
    private static Dictionary<Reagent, Ingot>? _ingots;

    /// <summary>Rebuilds so far: data derived from the prefabs is current while this is unchanged.</summary>
    internal static long Generation
    {
        get
        {
            Fresh();
            return _generation;
        }
    }

    private static long _generation;

    /// <summary>Every loaded prefab, for data derived once per Generation.</summary>
    internal static IReadOnlyList<Thing> All => Prefab.AllPrefabs;

    /// <summary>The prefabs build structure may build (BuildCatalogue).</summary>
    internal static BuildCatalogue Build
    {
        get
        {
            Fresh();
            return _build ??= BuildCatalogue.Load(Prefab.AllPrefabs, Cursors());
        }
    }

    /// <summary>The prefabs a structure swap may build (StructureTargets).</summary>
    internal static StructureTargets Targets
    {
        get
        {
            Fresh();
            return _targets ??= StructureTargets.Load(Prefab.AllPrefabs);
        }
    }

    /// <summary>The family's kits (KitCatalogue), one catalogue per family class.</summary>
    internal static KitCatalogue KitsOf(UpgradeFamily family)
    {
        Fresh();
        Type key = family.GetType();
        if (!Kits.TryGetValue(key, out KitCatalogue kits))
        {
            kits = KitCatalogue.Of(family, Prefab.AllPrefabs);
            Kits.Add(key, kits);
        }

        return kits;
    }

    /// <summary>A loaded prefab whose PrefabName is name in any case; null when none is.</summary>
    internal static Thing? NamedIgnoringCase(string name)
    {
        Fresh();
        if (_byName == null)
        {
            _byName = new Dictionary<string, Thing>(StringComparer.OrdinalIgnoreCase);
            foreach (Thing prefab in Prefab.AllPrefabs)
            {
                // The first of two names differing only in case, as the walk this replaces found it.
                if (prefab != null && prefab.PrefabName != null && !_byName.ContainsKey(prefab.PrefabName))
                {
                    _byName.Add(prefab.PrefabName, prefab);
                }
            }
        }

        return _byName.TryGetValue(name, out Thing found) ? found : null;
    }

    /// <summary>The names of the loaded prefabs whose hash is in hashes (an error's hint: no call path walks it).</summary>
    internal static List<string> NamesOf(HashSet<int> hashes)
    {
        List<string> names = new List<string>();
        foreach (Thing prefab in Prefab.AllPrefabs)
        {
            if (prefab != null && hashes.Contains(prefab.PrefabHash) && !string.IsNullOrEmpty(prefab.PrefabName))
            {
                names.Add(prefab.PrefabName);
            }
        }

        return names;
    }

    /// <summary>The first loaded prefab of a class; null when none is.</summary>
    internal static TPrefab? FirstOf<TPrefab>() where TPrefab : Thing
    {
        foreach (Thing prefab in Prefab.AllPrefabs)
        {
            if (prefab is TPrefab found && found != null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// SimpleFabricatorBase.GetPrefabHashFromReagentHash: the first ingot prefab whose CreatedReagentMixture contains
    /// each reagent, for every reagent some ingot makes.
    /// </summary>
    internal static IReadOnlyDictionary<Reagent, Ingot> IngotsByReagent
    {
        get
        {
            Fresh();
            if (_ingots == null)
            {
                _ingots = new Dictionary<Reagent, Ingot>();
                foreach (Reagent reagent in Reagent.AllReagents)
                {
                    foreach (Ingot ingot in Ingot.AllIngotPrefabs)
                    {
                        if (ingot != null && ingot.CreatedReagentMixture != null &&
                            ingot.CreatedReagentMixture.Contains(reagent))
                        {
                            _ingots[reagent] = ingot;
                            break;
                        }
                    }
                }
            }

            return _ingots;
        }
    }

    /// <summary>Whether the game's placement cursors are made (once a world runs): the build catalogue needs them.</summary>
    internal static bool CursorsMade => (Cursors()?.Count ?? 0) > 0;

    // The game's placement cursors by name (InventoryManager._constructionCursors), made once a world runs.
    private static Dictionary<string, Structure>? Cursors() =>
        GameMembers.ConstructionCursors.TryResolve()
            ? GameMembers.ConstructionCursors.GetValue(null) as Dictionary<string, Structure>
            : null;

    private static void Fresh()
    {
        long epoch = WorldStores.Epoch;
        int prefabs = Prefab.AllPrefabs.Count;
        int cursors = Cursors()?.Count ?? 0;
        if (epoch == _epoch && prefabs == _prefabCount && cursors == _cursorCount)
        {
            return;
        }

        _epoch = epoch;
        _prefabCount = prefabs;
        _cursorCount = cursors;
        _build = null;
        _targets = null;
        _byName = null;
        _ingots = null;
        Kits.Clear();
        _generation++;
    }
}
