#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

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
