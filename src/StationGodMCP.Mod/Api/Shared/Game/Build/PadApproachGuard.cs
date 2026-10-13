#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Objects.Electrical;
using StationGodMCP.Pure;
using Trading.Waypoints;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game.Build;

/// <summary>
/// The pad approach guard: a placement whose planned body box meets the approach corridor of a landing pad in the
/// world is refused with pad_approach, unless the call allows pad, which makes it a warning whose row the report
/// marks PASSED (PlaceLift). The game has no such check of its own: a trading shuttle descends through the corridor
/// its pad's waypoints define, and one that touches a structure is damaged and explodes after MaxNumberOfCollisions
/// hits (decompile TraderShuttle.cs:198-219). The game exempts the pad's own modular pieces from that damage - a
/// shuttle that touches a LandingPadModular returns early (:204-208), which the pad tiles, the taxi pieces, the
/// centre and a gas storage all are (LandingPadTile.cs:3, LandingPadTaxiTile.cs:8, LandingPadCenter.cs:30) - and this
/// guard exempts exactly those pieces by that same type test: a placement whose prefab is a LandingPadModular is no
/// finding, allow pad or not, so laying a runway or its taxi path on needs no allow: ["pad"]. The threshold piece is
/// a LandingPadModularDevice (LandingPadTaxiThreshold.cs:10), which is no LandingPadModular
/// (LandingPadModularDevice.cs:18), and the game does not exempt it either.
///
/// A corridor is the widest trader the pad admits (its RequiredPadSize in 2 m cells, the same CheckPadSize probe
/// find pad reports) at the longest DeorbitDistance over those traders (TraderContact.cs:91-102), over the path those
/// traders fly: a pad with no waypoint chain flies its own waypoint geometry, and a pad whose approach runs a chain (a
/// runway threshold linked through the taxi pieces) flies the chain up to the threshold and in along its taxi path
/// (PadCorridor). The pads are the ones the game registers as waypoints - a pad registers itself when it stands
/// (LandingPadCenter.cs:786-791), WaypointManager.AllWaypoints (:7) is a small list - so a world with no pad costs a
/// walk of that list, and the corridors are built only for a call that resolved a placement; a pad whose chain is a
/// runway costs one walk of its pieces on top.
/// </summary>
internal static class PadApproachGuard
{
    /// <summary>Every trader type but none, read once (Enum.GetValues allocates).</summary>
    private static readonly List<ShuttleType> Types = AllTypes();

    /// <summary>
    /// Checks the plan's resolved placements against every pad's corridor. One problem per placement however many
    /// corridors hold it, naming the first pad that does (and how many pads it is); unless the call allows pad, which
    /// lifts each to a warning the report marks PASSED.
    /// </summary>
    internal static void Check(PlacePlan plan, bool allowPad)
    {
        // A call whose every resolved placement is a piece the game exempts costs no walk of the world's pads.
        bool any = false;
        for (int index = 0; index < plan.Placements.Count && !any; index++)
        {
            any = Placeable(plan.Placements[index]) && !Exempt(plan.Placements[index]);
        }

        if (!any)
        {
            return;
        }

        List<Pad> pads = Corridors();
        for (int index = 0; index < plan.Placements.Count; index++)
        {
            PlannedPlacement placement = plan.Placements[index];
            if (!Placeable(placement))
            {
                continue;
            }

            Vector3 position = placement.Position!.Value;
            Box3 body = Bodies.RenderBox(placement.Prefab!, position, placement.Rotation);
            int held = 0;
            Pad? first = null;
            for (int pad = 0; pad < pads.Count; pad++)
            {
                if (pads[pad].Shape.Hits(body))
                {
                    held++;
                    first ??= pads[pad];
                }
            }

            PadApproachVerdict verdict = PadApproachRule.Of(held > 0, allowPad, Exempt(placement));
            if (verdict == PadApproachVerdict.Clear || first == null)
            {
                continue;
            }

            string what = $"{placement.Prefab!.PrefabName} at {PlacePlanner.Describe(position)}";
            string padName = $"{Names.Of(first.Center)} ({first.Center.PrefabName} {first.Center.ReferenceId})" +
                (held > 1 ? $", one of {held} landing pads whose approach holds it" : string.Empty);
            string corridor = first.Shape.Describe();
            if (verdict == PadApproachVerdict.Lifted)
            {
                plan.Warn(PadApproachRule.Code, PadApproachRule.Lifted(what, padName, corridor), placement.Index);
                plan.Lifted.Add(new PlaceLift(PadApproachRule.Guard, placement.Index));
            }
            else
            {
                plan.Problem(PadApproachRule.Code, PadApproachRule.Refusal(what, padName, corridor),
                    placement.Index);
            }
        }
    }

    /// <summary>A placement the cursor resolved: the prefab, the cursor, the position and the state (PlacePlanner).</summary>
    private static bool Placeable(PlannedPlacement placement) => placement.Resolved && placement.Position.HasValue;

    // The game's own exemption, the one type test its shuttle check makes: a shuttle that touches a LandingPadModular
    // returns before any damage is taken (decompile TraderShuttle.cs:204-208), so the pad's own pieces - the tiles, the
    // taxi pieces, the centre, a gas storage (LandingPadTile.cs:3, LandingPadTaxiTile.cs:8, LandingPadCenter.cs:30,
    // LandingPadGasStorage.cs:11) - never hurt a shuttle and never count as a collision, and a corridor holding one is
    // no finding however the call reads (PadApproachRule.Of). The game tests that same class and the guard tests no
    // other: the threshold piece is a LandingPadModularDevice rather than a LandingPadModular
    // (LandingPadTaxiThreshold.cs:10, LandingPadModularDevice.cs:18), and the game exempts it no more than this does.
    private static bool Exempt(PlannedPlacement placement) => placement.Prefab is LandingPadModular;

    // Every pad in the world and its corridor, read once a call: the pads are the PadCenter waypoints the game holds
    // (a pad registers itself, LandingPadCenter.cs:786-791), a small list whatever the world. A pad that admits no
    // trader has no corridor and is left out.
    private static List<Pad> Corridors()
    {
        List<Pad> pads = new List<Pad>();
        List<IWaypoint> waypoints = WaypointManager.AllWaypoints;
        for (int index = 0; index < waypoints.Count; index++)
        {
            if (!(waypoints[index] is LandingPadCenter center) || center == null || center.IsCursor ||
                center.IsBeingDestroyed)
            {
                continue;
            }

            PadCorridor? shape = Corridor(center);
            if (shape.HasValue)
            {
                pads.Add(new Pad(center, shape.Value));
            }
        }

        return pads;
    }

    // One pad's corridor: the traders the pad's own CheckPadSize admits (TraderContact.RequiredPadSize inside the
    // tiles it stands on, LandingPadCenter.cs:346-392) give the width and the distances, and the pad's approach
    // geometry gives the path (PadCorridor: the chain when the pad walks one, its own waypoint when it does not).
    // CheckPadSize leaves the centre offset a ship's size needs in lastPadOffsetValue, which WaypointPosition reads
    // (:277), so the widest admitted size is checked last - the waypoint read is that ship's own - and the field is
    // put back as it was afterwards, whatever the checks left in it (find pad keeps it too, LandingPads.Fits), so no
    // landing in progress moves.
    //
    // The probe is the pad's own size check alone, the one find pad reads news from: a plane also needs a threshold
    // piece of its own to land, which CanTraderLand refuses it without (:297-326, ThresholdChecks :328-343), so a pad
    // whose tiles would take a plane but which stands on no threshold is walked as if that plane were landing - 18 m
    // wide for a LargePlane where the widest trader that can land there is a 12 m Large (TraderContact.cs:218-238).
    // That is the safe side of the guess (the corridor holds at least everything the game flies), and it is the same
    // probe find pad reports its admitted set from.
    private static PadCorridor? Corridor(LandingPadCenter center)
    {
        PadApproach admitted = new PadApproach();
        double widest = 0.0;
        Vector2 widestSize = default;
        Vector3 saved = center.lastPadOffsetValue;
        try
        {
            for (int index = 0; index < Types.Count; index++)
            {
                TraderContact probe = ShipTable.Probe(Types[index]);
                Vector2 size = probe.RequiredPadSize();
                if (!center.CheckPadSize(size))
                {
                    continue;
                }

                Vector2 deorbit = probe.DeorbitDistance;
                admitted.Admit(size.x, size.y, deorbit.x, deorbit.y, probe.RequiredRunwayLength);
                if (Math.Max(size.x, size.y) > widest)
                {
                    widest = Math.Max(size.x, size.y);
                    widestSize = size;
                }
            }

            if (!admitted.Any)
            {
                return null;
            }

            center.CheckPadSize(widestSize);
            Vector3 waypoint = center.WaypointPosition;
            IWaypoint? first = FirstApproach(center);
            if (first == null)
            {
                return admitted.Corridor(Bodies.V(waypoint), Bodies.V(center.WaypointForward),
                    center.VirtualWaypointHeight);
            }

            List<Vec3> chain = new List<Vec3>();
            GroundPath(first, center, chain);
            return admitted.Corridor(Bodies.V(first.WaypointPosition), Bodies.V(first.WaypointForward), chain,
                Bodies.V(waypoint), center.VirtualWaypointHeight, Runway(center));
        }
        finally
        {
            center.lastPadOffsetValue = saved;
        }
    }

    // The threshold piece the pad's planes land on and roll along, at its own waypoint and forward, or none when the
    // pad stands on no threshold piece: the game's own read walks the centre's chain for it
    // (GetTaxiThresholdPiece, LandingPadCenter.cs:1026-1040), the piece LinkTaxiWaypoints ties the RunwayStop waypoint
    // to (LandingPadTaxiThreshold.cs:87-90) and Waypoint.ValidateWaypoint links a nav beacon ahead of
    // (Waypoint.cs:345-355), so a chain that a beacon heads still rolls from the threshold rather than the beacon.
    private static PadRunway Runway(LandingPadCenter center) =>
        center.GetTaxiThresholdPiece(out IWaypoint threshold) && !threshold.BeingDestroyed
            ? PadRunway.At(Bodies.V(threshold.WaypointPosition), Bodies.V(threshold.WaypointForward))
            : PadRunway.None;

    /// <summary>
    /// How many pieces one walk of a chain can pass: every waypoint the world holds registers itself as one
    /// (WaypointManager.Register, a pad LandingPadCenter.cs:789, a taxi piece LandingPadTaxiTile.cs:73, a threshold
    /// LandingPadTaxiThreshold.cs:48, a waypoint device Waypoint.cs:367), and LinkTaxiWaypoints ties the RunwayStop
    /// and RunwayStart waypoints at the threshold's own position beside them (:87-90), so that many hops cannot cut a
    /// chain short, while a chain a broken save left in a loop stops rather than walking forever.
    /// </summary>
    private static int ChainSteps => WaypointManager.AllWaypoints.Count + 2;

    // The pad's first approach waypoint, or null when it has none: the game's own read walks PreviousWaypoint to its
    // end and takes that piece as where the traders come down (GetFirstApproachWaypoint,
    // LandingPadCenter.cs:1042-1055), so a pad with a chain runs its approach to the threshold and a pad without one
    // gets the virtual waypoint VirtualWaypointHeight above the centre (:1053). A piece on its way out has lost the
    // transform its forward is read from (the deorbit point sits along it, :1091), so a chain it heads leaves the pad
    // its own approach, the corridor a pad with no chain gets.
    private static IWaypoint? FirstApproach(LandingPadCenter center)
    {
        IWaypoint? waypoint = center.PreviousWaypoint;
        for (int steps = ChainSteps; steps > 0; steps--)
        {
            if (waypoint is null || waypoint.PreviousWaypoint is null)
            {
                break;
            }

            waypoint = waypoint.PreviousWaypoint;
        }

        return waypoint is null || waypoint.BeingDestroyed ? null : waypoint;
    }

    // The chain's own ground path, from the first approach waypoint to the centre: every piece in NextWaypoint order
    // at its own WaypointPosition, the way the shuttle walks it (LandingPadCenter.cs:1099-1101, :1150, :1165-1166,
    // MoveTo a straight run between waypoint positions, TraderShuttleAnimationHandler.cs:111-141). The centre is the
    // walk's end - the pad's own waypoint, where the shuttle lands (:1189) - so a chain the walk cannot follow all the
    // way still ends over the pad.
    private static void GroundPath(IWaypoint first, LandingPadCenter center, List<Vec3> path)
    {
        IWaypoint? waypoint = first;
        for (int steps = ChainSteps; steps > 0 && waypoint is not null; steps--)
        {
            path.Add(Bodies.V(waypoint.WaypointPosition));
            if (ReferenceEquals(waypoint, center))
            {
                return;
            }

            waypoint = waypoint.NextWaypoint;
        }

        path.Add(Bodies.V(center.WaypointPosition));
    }

    private static List<ShuttleType> AllTypes()
    {
        List<ShuttleType> types = new List<ShuttleType>();
        foreach (ShuttleType type in Enum.GetValues(typeof(ShuttleType)))
        {
            if (type != ShuttleType.None)
            {
                types.Add(type);
            }
        }

        return types;
    }

    /// <summary>A pad and the corridor the traders it admits descend through, for one call's check.</summary>
    private sealed class Pad
    {
        internal Pad(LandingPadCenter center, PadCorridor shape)
        {
            Center = center;
            Shape = shape;
        }

        internal LandingPadCenter Center { get; }

        internal PadCorridor Shape { get; }
    }
}
