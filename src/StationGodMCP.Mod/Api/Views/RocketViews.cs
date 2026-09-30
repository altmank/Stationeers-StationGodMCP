#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Views;

/// <summary>
/// An umbilical's pairing (describe_device, grid_survey devices): its role, the partner the game holds for it and the
/// PartnerDistance it set, and the game's partner search replayed as it stands now (UmbilicalSearch), which names why
/// each column stopped when nothing pairs.
/// </summary>
internal sealed class UmbilicalView
{
    internal UmbilicalView(string role, bool open, ThingView? partner, int partnerDistance, UmbilicalSearchView search)
    {
        Role = role;
        Open = open;
        Partner = partner;
        PartnerDistance = partnerDistance;
        Search = search;
    }

    /// <summary>umbilical (the male on a tower: searches three columns) or socket (the rocket's side: one column).</summary>
    public string Role { get; }

    /// <summary>IUmbilical.IsOpen: extended and passing its gas, power or items.</summary>
    public bool Open { get; }

    /// <summary>The partner the game holds now; null for none.</summary>
    public ThingView? Partner { get; }

    /// <summary>IUmbilical.PartnerDistance: small cells to the partner plus one; 0 unpaired.</summary>
    public int PartnerDistance { get; }

    public UmbilicalSearchView Search { get; }
}

/// <summary>RocketUmbilicalHelper.FindAndSetOtherUmbilical replayed without setting anything.</summary>
internal sealed class UmbilicalSearchView
{
    internal UmbilicalSearchView(UmbilicalSearchResult result, string how)
    {
        Found = result.Partner == null ? (ThingId?)null : new ThingId(result.Partner.Id);
        PartnerDistance = result.PartnerDistance;
        How = how;
        Stops = result.Stops.Count == 0 ? null : result.Stops.ConvertAll(static stop => new UmbilicalStopView(stop));
    }

    /// <summary>The umbilical the search pairs it with now; null for none.</summary>
    public ThingId? Found { get; }

    public int PartnerDistance { get; }

    /// <summary>Where and how it searched.</summary>
    public string How { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<UmbilicalStopView>? Stops { get; }
}

internal sealed class UmbilicalStopView
{
    internal UmbilicalStopView(UmbilicalStop stop)
    {
        Column = stop.Column;
        Step = stop.Step;
        Reason = stop.Reason;
    }

    public int Column { get; }

    public int Step { get; }

    public string Reason { get; }
}

/// <summary>
/// The rocket a device or piece is part of (describe_device): the rocket network, the rocket's name and state, and what
/// the game counts for it (RocketNetwork: DryMass from IRocketMassContributor, its hull pieces and internals).
/// </summary>
internal sealed class RocketPartView
{
    internal RocketPartView(ThingId networkId, string name, string state, double dryMassKg, int hullPieces,
        int internals)
    {
        NetworkId = networkId;
        Name = name;
        State = state;
        DryMassKg = dryMassKg;
        HullPieces = hullPieces;
        Internals = internals;
    }

    public ThingId NetworkId { get; }

    public string Name { get; }

    public string State { get; }

    public double DryMassKg { get; }

    public int HullPieces { get; }

    public int Internals { get; }
}
