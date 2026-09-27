#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Networks;
using Objects.Electrical;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using UnityEngine;

namespace StationGodMCP.Api;

/// <summary>
/// landing_pads: every trader landing pad, its size as the game measures it, which ships fit, and whether each
/// contact in the sky could land now. Read only.
///
/// How the game judges a pad (CODE, Objects.Electrical.LandingPadCenter): LandingPadCenter.CanTraderLand refuses, in
/// order, when the centre is off, has an error or is not its network's one centre (LandingPadNetwork
/// .LandingPadCenter), a storm is running and the ship is not a plane, CheckPadSize fails, or ThresholdChecks fails.
/// LandingPadCenter.CheckPadSize(Vector2) swaps x and y when the centre's Forward points east or west, tries the
/// rectangle centred on the centre piece, then IsPadBigEnoughWithVirtualCenter, which shifts the centre by one tile on
/// an even side. CheckPadRectangle needs a LandingPadTile, or the centre, in every 2 m cell.
///
/// CheckPadSize has a side effect: it writes LandingPadCenter.lastPadOffsetValue, which WaypointPosition uses for a
/// landing ship. Every check here saves that field first and puts it back after, so a landing in progress never moves.
///
/// Ship sizes come from the game's own table, TraderContact.RequiredPadSize and TraderContact.RequiredRunwayLength,
/// which switch on ShuttleType alone. They are read from a probe contact made with
/// FormatterServices.GetUninitializedObject: the constructors register a new contact with the game, and this must not.
/// Planes (TraderContact.RequiresThreshold) also need exactly one switched-on runway threshold on the pad's network
/// (LandingPadNetwork.ValidApproachState). The game never compares the runway's length with RequiredRunwayLength,
/// which only sizes the approach path, so neither is that done here. LandingPadCenter.IsObstructed looks up from each
/// pad cell for a structure or a closed face; CanTraderLand does not call it, so it is reported apart.
/// </summary>
internal static class LandingPadsApi
{
    internal static LandingPadsView Handle(Args args)
    {
        List<Structure> structures = GridController.AllStructuresPool.ToList();
        List<TraderContact> contacts = new List<TraderContact>();
        foreach (TraderContact contact in TraderContact.AllStationContacts)
        {
            if (contact != null)
            {
                contacts.Add(contact);
            }
        }

        List<LandingPadView> pads = new List<LandingPadView>();
        for (int index = 0; index < structures.Count; index++)
        {
            if (structures[index] is LandingPadCenter center && !center.IsCursor && !center.IsBeingDestroyed)
            {
                pads.Add(LandingPadProbe.Describe(center, contacts));
            }
        }

        return new LandingPadsView(pads);
    }
}

/// <summary>Runs the game's pad checks on one centre, keeping its landing offset as it was.</summary>
internal static class LandingPadProbe
{
    // CheckPadSize is tried for every n x n square up to this side.
    private const int LargestSquareTried = 15;
    private const float TileMetres = 2f;

    internal static LandingPadView Describe(LandingPadCenter center, List<TraderContact> contacts)
    {
        LandingPadNetwork? network = center.LandingPadNetwork;
        PadNetworkFacts facts = network == null
            ? new PadNetworkFacts(false, 0, null, null)
            : new PadNetworkFacts(ReferenceEquals(network.LandingPadCenter, center), network.StructureList.Count,
                Extent(network), RunwayOk(network));
        PadMeasure measure = new PadMeasure(ForwardName(center.Forward), LargestSquare(center));
        return new LandingPadView(GameLookup.ViewOf(center), GameLookup.ViewOf(center.Position), facts, measure,
            FitsByShip(center), ContactViews(center, contacts));
    }

    // LandingPadCenter.CheckPadSize, with lastPadOffsetValue put back whatever the check wrote.
    private static bool Fits(LandingPadCenter center, Vector2 size)
    {
        Vector3 saved = center.lastPadOffsetValue;
        try
        {
            return center.CheckPadSize(size);
        }
        finally
        {
            center.lastPadOffsetValue = saved;
        }
    }

    // LandingPadCenter.CanTraderLand, with lastPadOffsetValue put back whatever the check wrote.
    private static bool CanLand(LandingPadCenter center, TraderContact contact, out string reason)
    {
        Vector3 saved = center.lastPadOffsetValue;
        try
        {
            string? message = null;
            bool canLand = center.CanTraderLand(contact, out message);
            reason = canLand ? string.Empty : message ?? string.Empty;
            return canLand;
        }
        finally
        {
            center.lastPadOffsetValue = saved;
        }
    }

    private static int LargestSquare(LandingPadCenter center)
    {
        int largest = 0;
        for (int side = 1; side <= LargestSquareTried; side++)
        {
            if (Fits(center, new Vector2(side, side)))
            {
                largest = side;
            }
        }

        return largest;
    }

    // ThresholdChecks for a plane: ValidApproachState, and exactly one switched-on threshold.
    private static bool RunwayOk(LandingPadNetwork network) =>
        network.ValidApproachState(out LandingPadTaxiThreshold threshold) && threshold != null;

    private static List<ShipFitView> FitsByShip(LandingPadCenter center)
    {
        List<ShipFitView> fits = new List<ShipFitView>();
        foreach (ShuttleType type in Enum.GetValues(typeof(ShuttleType)))
        {
            if (type == ShuttleType.None)
            {
                continue;
            }

            TraderContact probe = ShipTable.Probe(type);
            Vector2 size = probe.RequiredPadSize();
            fits.Add(new ShipFitView(type.ToString(), Size(size), probe.RequiredRunwayLength, probe.RequiresThreshold,
                Fits(center, size)));
        }

        return fits;
    }

    private static List<ContactFitView> ContactViews(LandingPadCenter center, List<TraderContact> contacts)
    {
        List<ContactFitView> views = new List<ContactFitView>(contacts.Count);
        foreach (TraderContact contact in contacts)
        {
            Vector2 size = contact.RequiredPadSize();
            bool canLand = CanLand(center, contact, out string reason);
            PadVerdict verdict = new PadVerdict(Fits(center, size), center.IsObstructed(contact), canLand, reason);
            string? name = contact.DataInstance != null ? contact.DataInstance.DisplayName : null;
            views.Add(new ContactFitView(new ThingId(contact.ReferenceId), name ?? contact.ContactName,
                contact.ShuttleType.ToString(), Size(size), verdict));
        }

        return views;
    }

    // The pad tiles and the centre of the network, as a box of 2 m tiles along the world axes.
    private static ExtentView? Extent(LandingPadNetwork network)
    {
        List<INetworkedStructure> members;
        lock (network.StructureList)
        {
            members = new List<INetworkedStructure>(network.StructureList);
        }

        bool any = false;
        Vector2 min = Vector2.zero;
        Vector2 max = Vector2.zero;
        foreach (INetworkedStructure member in members)
        {
            if (!(member is LandingPadTile) && !(member is LandingPadCenter))
            {
                continue;
            }

            Vector3 position = member.GetAsThing.Position;
            Vector2 flat = new Vector2(position.x, position.z);
            min = any ? Vector2.Min(min, flat) : flat;
            max = any ? Vector2.Max(max, flat) : flat;
            any = true;
        }

        return any ? new ExtentView(Tiles(max.x - min.x), Tiles(max.y - min.y)) : null;
    }

    private static int Tiles(float span) => Mathf.RoundToInt(span / TileMetres) + 1;

    // Forward to the nearest world axis; +z is north (Grid3.North).
    private static string ForwardName(Vector3 forward)
    {
        if (Math.Abs(forward.x) >= Math.Abs(forward.z))
        {
            return forward.x >= 0f ? "+x" : "-x";
        }

        return forward.z >= 0f ? "+z" : "-z";
    }

    private static int[] Size(Vector2 size) => new[] { (int)size.x, (int)size.y };
}

/// <summary>The game's per-ship table, read through a contact that is never registered with the game.</summary>
internal static class ShipTable
{
    private static readonly Dictionary<ShuttleType, TraderContact> Probes =
        new Dictionary<ShuttleType, TraderContact>();

    internal static TraderContact Probe(ShuttleType type)
    {
        if (!Probes.TryGetValue(type, out TraderContact probe))
        {
            probe = (TraderContact)FormatterServices.GetUninitializedObject(typeof(TraderContact));
            probe.ShuttleType = type;
            Probes[type] = probe;
        }

        return probe;
    }
}
