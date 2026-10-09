#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared;

/// <summary>A part of a grid_survey reply that sections can name.</summary>
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
/// The parts of a grid_survey reply a caller asked for (sections, each part once by its reply name); every part when
/// sections is left out, so the reply stays as it was. A part left out is absent from the reply, not empty.
/// </summary>
internal sealed class SurveySections
{
    internal const string Argument = "sections";

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

    internal static SurveySections Parse(Args args)
    {
        if (!args.Has(Argument))
        {
            return All;
        }

        JArray array = args.Array(Argument, Words.Count);
        SurveySection parts = SurveySection.None;
        foreach (JToken entry in array)
        {
            string? word = entry.Type == JTokenType.String ? entry.Value<string>()?.Trim() : null;
            if (word == null || !Words.TryGetValue(word, out SurveySection part))
            {
                throw ApiErrors.InvalidArgument(
                    $"Argument '{Argument}' takes {string.Join(", ", Words.Keys)}; '{entry}' is not one of them.");
            }

            parts |= part;
        }

        return new SurveySections(parts);
    }

    internal bool Includes(SurveySection part) => (_parts & part) == part;

    /// <summary>The list when its part was asked for, else null (left out of the reply).</summary>
    internal List<T>? Pick<T>(SurveySection part, List<T> list) => Includes(part) ? list : null;
}

/// <summary>
/// grid_survey network_ids: the networks whose pieces (and devices with a port on them) a reply keeps; every network
/// when none is named.
/// </summary>
internal sealed class SurveyNetworkFilter
{
    internal const string Argument = "network_ids";
    internal const int MaximumNetworks = 64;

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
/// grid_survey kinds: which kinds of piece (cable, pipe, chute) a reply lists, with their networks, and which devices:
/// those with a port of a kind named. Every kind when kinds is left out.
/// </summary>
internal sealed class SurveyKinds
{
    internal const string KindsArgument = "kinds";

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

    internal static SurveyKinds Parse(Args args)
    {
        if (!args.Has(KindsArgument))
        {
            return Every;
        }

        JArray array = args.Array(KindsArgument, Words.Count);
        int kinds = 0;
        foreach (JToken entry in array)
        {
            string? word = entry.Type == JTokenType.String ? entry.Value<string>()?.Trim() : null;
            if (word == null || !Words.TryGetValue(word, out int kind))
            {
                throw ApiErrors.InvalidArgument(
                    $"Argument '{KindsArgument}' takes {string.Join(", ", Words.Keys)}; '{entry}' is not one of them.");
            }

            kinds |= kind;
        }

        return new SurveyKinds(kinds);
    }

    /// <summary>A piece of this kind (cable, pipe or chute) is listed.</summary>
    internal bool AdmitsPiece(string kind) => Words.TryGetValue(kind, out int bit) && (_kinds & bit) != 0;

    /// <summary>
    /// A device is listed when kinds is left out, or when one of its ports is of a kind named: a NetworkType name
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
/// network_ids, kinds and prefab, prefabs or prefab_contains together: what a grid_survey page's pieces and devices
/// must pass. A piece passes on its network, kind and prefab; a device on a port on a named network, a port of a named
/// kind, and its own prefab.
/// </summary>
internal sealed class SurveyFilter
{
    internal SurveyFilter(SurveyNetworkFilter networks, SurveyKinds kinds, PrefabMatch prefabs)
    {
        Networks = networks;
        Kinds = kinds;
        Prefabs = prefabs;
    }

    internal static SurveyFilter Every { get; } =
        new SurveyFilter(SurveyNetworkFilter.Every, SurveyKinds.Every, PrefabMatch.Any);

    internal SurveyNetworkFilter Networks { get; }

    internal SurveyKinds Kinds { get; }

    internal PrefabMatch Prefabs { get; }

    internal bool AdmitsPiece(ThingId? network, string kind, string? prefab) =>
        Networks.Admits(network) && Kinds.AdmitsPiece(kind) && Prefabs.Keeps(prefab);

    internal bool AdmitsDevice(string? prefab, IEnumerable<ThingId?> networks, IEnumerable<string?> portTypes) =>
        Prefabs.Keeps(prefab) && Networks.AdmitsAny(networks) && Kinds.AdmitsDevice(portTypes);
}

/// <summary>grid_survey cell_detail: occupancy (the default) or full.</summary>
internal static class SurveyCellDetails
{
    internal const string CellDetailArgument = "cell_detail";

    private static readonly Dictionary<string, SurveyCellDetail> Words =
        new Dictionary<string, SurveyCellDetail>(StringComparer.OrdinalIgnoreCase)
        {
            ["occupancy"] = SurveyCellDetail.Occupancy,
            ["full"] = SurveyCellDetail.Full
        };

    internal static SurveyCellDetail Parse(Args args)
    {
        string? word = args.OptionalString(CellDetailArgument)?.Trim();
        if (word == null)
        {
            return SurveyCellDetail.Occupancy;
        }

        return Words.TryGetValue(word, out SurveyCellDetail detail)
            ? detail
            : throw ApiErrors.InvalidArgument($"Argument '{CellDetailArgument}' takes occupancy or full; '{word}' is neither.");
    }
}
