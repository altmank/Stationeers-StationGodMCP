#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Api.Shared;

/// <summary>A part of a survey area reply that sections can name.</summary>
[Flags]
internal enum SurveySection
{
    None = 0,
    Cells = 1,
    Pieces = 2,
    Devices = 4,
    Networks = 8,
    NetworkVisibility = 16,
    Doors = 32,
    All = Cells | Pieces | Devices | Networks | NetworkVisibility | Doors
}

/// <summary>
/// The parts of a survey area reply a call asked for (sections, each part once by its reply name); every part when
/// sections is left out. A part left out is absent from the reply, not empty.
/// </summary>
internal sealed class SurveySections
{
    private static readonly Dictionary<string, SurveySection> Words =
        new Dictionary<string, SurveySection>(StringComparer.OrdinalIgnoreCase)
        {
            ["cells"] = SurveySection.Cells,
            ["pieces"] = SurveySection.Pieces,
            ["devices"] = SurveySection.Devices,
            ["networks"] = SurveySection.Networks,
            ["network_visibility"] = SurveySection.NetworkVisibility,
            ["doors"] = SurveySection.Doors
        };

    private readonly SurveySection _parts;

    private SurveySections(SurveySection parts)
    {
        _parts = parts;
    }

    internal static SurveySections All { get; } = new SurveySections(SurveySection.All);

    /// <summary>The words sections takes, as the schema lists them.</summary>
    internal static IEnumerable<string> Names => Words.Keys;

    /// <summary>The parts the words name, in any case; every part when none is given.</summary>
    internal static SurveySections Of(IReadOnlyList<string>? words)
    {
        if (words == null)
        {
            return All;
        }

        SurveySection parts = SurveySection.None;
        foreach (string word in words)
        {
            if (Words.TryGetValue(word.Trim(), out SurveySection part))
            {
                parts |= part;
            }
        }

        return new SurveySections(parts);
    }

    internal bool Includes(SurveySection part) => (_parts & part) == part;

    /// <summary>
    /// How many cells one page reads: the call's limit while the reply lists the cells (the page they are on), and every
    /// cell of the area when it leaves them out, so pieces and devices come from the whole area in one reply, never
    /// from a first page of cells a caller cannot see or page on.
    /// </summary>
    internal int PageLimit(int? asked) => Includes(SurveySection.Cells) ? asked ?? int.MaxValue : int.MaxValue;

    /// <summary>The list when its part was asked for, else null (left out of the reply).</summary>
    internal List<T>? Pick<T>(SurveySection part, List<T> list) => Includes(part) ? list : null;
}

/// <summary>
/// survey area network: the networks whose pieces (and devices with a port on them) a reply keeps; every network when
/// none is named.
/// </summary>
internal sealed class SurveyNetworkFilter
{
    private readonly HashSet<long>? _networks;

    private SurveyNetworkFilter(HashSet<long>? networks)
    {
        _networks = networks;
    }

    internal static SurveyNetworkFilter Every { get; } = new SurveyNetworkFilter(null);

    internal static SurveyNetworkFilter Only(HashSet<long> networks) => new SurveyNetworkFilter(networks);

    /// <summary>A piece on no network passes only when no network is named.</summary>
    internal bool Admits(ThingId? network) =>
        _networks == null || (network.HasValue && _networks.Contains(network.Value.Value));

    /// <summary>Whether any of a device's port networks is named (always, when none is).</summary>
    internal bool AdmitsAny(IEnumerable<ThingId?> networks)
    {
        if (_networks == null)
        {
            return true;
        }

        foreach (ThingId? network in networks)
        {
            if (Admits(network))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// survey area kind: which kinds of piece (cable, pipe, chute) a reply lists, with their networks, and which devices:
/// those with a port of a kind named. Every kind when kind is left out.
/// </summary>
internal sealed class SurveyKinds
{
    private static readonly Dictionary<string, int> Words = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["cable"] = 1,
        ["pipe"] = 2,
        ["chute"] = 4
    };

    private readonly int _kinds;

    private SurveyKinds(int kinds)
    {
        _kinds = kinds;
    }

    internal static SurveyKinds Every { get; } = new SurveyKinds(7);

    /// <summary>The kinds the words name, in any case; every kind when none is given.</summary>
    internal static SurveyKinds Of(IReadOnlyList<string>? words)
    {
        if (words == null)
        {
            return Every;
        }

        int kinds = 0;
        foreach (string word in words)
        {
            if (Words.TryGetValue(word.Trim(), out int kind))
            {
                kinds |= kind;
            }
        }

        return new SurveyKinds(kinds);
    }

    /// <summary>A piece of this kind (cable, pipe or chute) is listed.</summary>
    internal bool AdmitsPiece(string kind) => Words.TryGetValue(kind, out int bit) && (_kinds & bit) != 0;

    /// <summary>
    /// A device is listed when kind is left out, or when one of its ports is of a kind named: a NetworkType name
    /// with Pipe is a pipe's, with Chute a chute's, with Power or Data a cable's.
    /// </summary>
    internal bool AdmitsDevice(IEnumerable<string?> portTypes)
    {
        if (_kinds == 7)
        {
            return true;
        }

        foreach (string? type in portTypes)
        {
            if (type == null)
            {
                continue;
            }

            string? kind = type.IndexOf("Pipe", StringComparison.Ordinal) >= 0 ? "pipe"
                : type.IndexOf("Chute", StringComparison.Ordinal) >= 0 ? "chute"
                : type.IndexOf("Power", StringComparison.Ordinal) >= 0 || type.IndexOf("Data", StringComparison.Ordinal) >= 0
                    ? "cable"
                    : null;
            if (kind != null && AdmitsPiece(kind))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// network, kind and prefab together: what a survey area page's pieces and devices must pass. A piece passes on its network, kind and prefab; a device on a port on a named network, a port of a named
/// kind, and its own prefab.
/// </summary>
internal sealed class SurveyFilter
{
    internal SurveyFilter(SurveyNetworkFilter networks, SurveyKinds kinds, PrefabFilter? prefabs)
    {
        Networks = networks;
        Kinds = kinds;
        Prefabs = prefabs;
    }

    internal static SurveyFilter Every { get; } =
        new SurveyFilter(SurveyNetworkFilter.Every, SurveyKinds.Every, null);

    internal SurveyNetworkFilter Networks { get; }

    internal SurveyKinds Kinds { get; }

    /// <summary>The prefabs kept; null keeps every prefab.</summary>
    internal PrefabFilter? Prefabs { get; }

    internal bool AdmitsPiece(ThingId? network, string kind, string? prefab, int prefabHash) =>
        Networks.Admits(network) && Kinds.AdmitsPiece(kind) && KeepsPrefab(prefab, prefabHash);

    internal bool AdmitsDevice(string? prefab, int prefabHash, IEnumerable<ThingId?> networks,
        IEnumerable<string?> portTypes) =>
        KeepsPrefab(prefab, prefabHash) && Networks.AdmitsAny(networks) && Kinds.AdmitsDevice(portTypes);

    private bool KeepsPrefab(string? prefab, int prefabHash) => Prefabs == null || Prefabs.Keeps(prefab, prefabHash);
}

/// <summary>survey area cell_detail: occupancy (the default) or full.</summary>
internal static class SurveyCellDetails
{
    internal static SurveyCellDetail Of(string? word) =>
        string.Equals(word?.Trim(), "full", StringComparison.OrdinalIgnoreCase)
            ? SurveyCellDetail.Full
            : SurveyCellDetail.Occupancy;
}
