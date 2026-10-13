#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;

namespace StationGodMCP.Pure;

/// <summary>
/// Whether a placement in the approach of a landing pad is refused or let through, and the words either way. The
/// game has no build check for a pad's approach: its traders descend through a corridor its pads define, and a
/// shuttle that touches a structure is damaged and explodes after MaxNumberOfCollisions hits (decompile
/// TraderShuttle.cs:198-219). StationGod makes a placement in that corridor a problem of its own, which the call's
/// allow names the guard word for, to have as a warning the report marks PASSED.
/// </summary>
internal enum PadApproachVerdict
{
    /// <summary>No corridor holds the placement: nothing is said about it.</summary>
    Clear,

    /// <summary>A corridor holds it and the call does not allow pad: a problem.</summary>
    Refused,

    /// <summary>A corridor holds it and the call allows pad: a warning the report marks PASSED.</summary>
    Lifted
}

/// <summary>The pad approach guard: its word in allow, the code it reports under, and the words it refuses with.</summary>
internal static class PadApproachRule
{
    /// <summary>The allow word that lifts the guard: a corridor placement becomes a warning the report marks PASSED.</summary>
    internal const string Guard = "pad";

    /// <summary>The code a corridor placement is reported under, refused or lifted.</summary>
    internal const string Code = "pad_approach";

    /// <summary>What a corridor holding a placement does to it.</summary>
    internal static PadApproachVerdict Of(bool held, bool allowPad) =>
        !held ? PadApproachVerdict.Clear
        : allowPad ? PadApproachVerdict.Lifted
        : PadApproachVerdict.Refused;

    /// <summary>The refusal: what stands there, the pad and its corridor, why it is refused, and how to allow it.</summary>
    internal static string Refusal(string what, string pad, string corridor) =>
        Approach(what, pad, corridor) + ". Build it elsewhere, or pass allow: [\"pad\"] to build there anyway.";

    /// <summary>The same finding allow pad let through, as the report's warning words it.</summary>
    internal static string Lifted(string what, string pad, string corridor) =>
        Approach(what, pad, corridor) + ". The pad approach guard let it through by allow: [\"pad\"]; the report " +
        "marks it PASSED.";

    private static string Approach(string what, string pad, string corridor) =>
        $"{what} is in the approach of the landing pad {pad}: its traders descend a corridor {corridor}, and a " +
        "shuttle that touches a structure is damaged and explodes after enough hits";
}

/// <summary>
/// The measures the traders a landing pad admits give its approach corridor: the widest RequiredPadSize, the longest
/// DeorbitDistance and the longest RequiredRunwayLength over them (the admitted set the pad's own size check decides,
/// the same probe find pad runs). A pad that admits no trader has no corridor.
/// </summary>
internal sealed class PadApproach
{
    /// <summary>
    /// One pad cell is 2 m: LandingPadCenter.CheckPadRectangle walks a trader's RequiredPadSize at 2 m a cell
    /// (decompile Objects.Electrical/LandingPadCenter.cs:412-459), so the size is a count of the pad tiles the
    /// trader needs, not metres (TraderContact.cs:218-238: 3 tiles Small, 5 Medium, 6 Large, 7 MediumPlane,
    /// 9 LargePlane).
    /// </summary>
    internal const double MetresPerCell = 2.0;

    private double _widthMetres;
    private double _backMetres;
    private double _upMetres;
    private double _runwayMetres;

    /// <summary>Whether any trader was admitted: without one there is no corridor to hold a placement.</summary>
    internal bool Any => _widthMetres > 0.0;

    /// <summary>The widest admitted trader's own width, in metres (its RequiredPadSize square).</summary>
    internal double WidthMetres => _widthMetres;

    /// <summary>The longest admitted plane's own landing run, in metres (its RequiredRunwayLength; 0 for the rest).</summary>
    internal double RunwayMetres => _runwayMetres;

    /// <summary>
    /// Takes one admitted trader: its RequiredPadSize in 2 m cells, its DeorbitDistance, and its RequiredRunwayLength
    /// (TraderContact.cs:84-102: 15 m a MediumPlane, 20 m a LargePlane, none for the rest). The corridor keeps the
    /// widest size and the longest of each distance over everything admitted - they move together per shuttle type, so
    /// this is the one volume every trader the pad admits descends through and rolls out over.
    /// </summary>
    internal void Admit(double padSizeCellsX, double padSizeCellsY, double backMetres, double upMetres,
        double runwayMetres)
    {
        _widthMetres = Math.Max(_widthMetres, Math.Max(padSizeCellsX, padSizeCellsY) * MetresPerCell);
        _backMetres = Math.Max(_backMetres, backMetres);
        _upMetres = Math.Max(_upMetres, upMetres);
        _runwayMetres = Math.Max(_runwayMetres, runwayMetres);
    }

    /// <summary>
    /// The admitted traders' corridor over a pad with no waypoint chain, or null when the pad admits none: the pad's
    /// own waypoint, its forward, and the drop to its high waypoint (PadCorridor.WithoutChain).
    /// </summary>
    internal PadCorridor? Corridor(Vec3 waypoint, Vec3 forward, double dropMetres) =>
        Any ? PadCorridor.WithoutChain(waypoint, forward, dropMetres, Sweep) : (PadCorridor?)null;

    /// <summary>
    /// The admitted traders' corridor over a pad whose approach runs a waypoint chain, or null when the pad admits
    /// none: the chain's first approach waypoint, the ground points walked from it to the centre, the threshold's own
    /// runway, the centre's own waypoint, and the drop to its high waypoint (PadCorridor.WithChain).
    /// </summary>
    internal PadCorridor? Corridor(Vec3 first, Vec3 forward, IReadOnlyList<Vec3> chain, Vec3 centre,
        double dropMetres, PadRunway runway) =>
        Any ? PadCorridor.WithChain(first, forward, chain, centre, dropMetres, Sweep, runway) : (PadCorridor?)null;

    private PadSweep Sweep => new PadSweep(_widthMetres * 0.5, _backMetres, _upMetres, _runwayMetres);
}

/// <summary>
/// What every leg of one corridor is swept by: the admitted traders' own measures, each the widest or the longest
/// over them. The corridor's width is half the widest admitted trader's square, its deorbit point sits the longest
/// DeorbitDistance back and up (TraderContact.cs:91-102), and only the planes it admits roll out (RequiredRunwayLength).
/// </summary>
internal readonly struct PadSweep
{
    internal PadSweep(double halfWidthMetres, double backMetres, double upMetres, double runwayMetres)
    {
        HalfWidthMetres = halfWidthMetres;
        BackMetres = backMetres;
        UpMetres = upMetres;
        RunwayMetres = runwayMetres;
    }

    /// <summary>Half the widest admitted trader's square: how far the sweep reaches off every leg.</summary>
    internal double HalfWidthMetres { get; }

    /// <summary>The longest DeorbitDistance back from the first approach waypoint (DeorbitDistance.x).</summary>
    internal double BackMetres { get; }

    /// <summary>The longest DeorbitDistance above it (DeorbitDistance.y).</summary>
    internal double UpMetres { get; }

    /// <summary>The longest admitted plane's RequiredRunwayLength (0 when the pad admits none).</summary>
    internal double RunwayMetres { get; }
}

/// <summary>One leg of a corridor's path: the stretch between two world points, swept by the corridor's half width.</summary>
internal readonly struct PadLeg
{
    internal PadLeg(Vec3 from, Vec3 to)
    {
        From = from;
        To = to;
    }

    internal Vec3 From { get; }

    internal Vec3 To { get; }
}

/// <summary>
/// The runway of a pad whose approach runs a chain: the threshold piece the planes come down on and roll along, at
/// its own waypoint and forward - the piece LinkTaxiWaypoints ties the RunwayStop waypoint to
/// (LandingPadTaxiThreshold.cs:87-90, WaypointPosition :20, WaypointForward :22) - or None when the pad has no
/// threshold piece, where no plane lands at all (LandingPadCenter.CanTraderLand:297-343), so nothing rolls on.
/// </summary>
internal readonly struct PadRunway
{
    private readonly Vec3 _waypoint;
    private readonly Vec3 _forward;
    private readonly bool _any;

    private PadRunway(Vec3 waypoint, Vec3 forward)
    {
        _waypoint = waypoint;
        _forward = forward;
        _any = true;
    }

    /// <summary>A pad standing on no threshold piece: no plane lands there, and no runway stretch is swept.</summary>
    internal static PadRunway None => default;

    /// <summary>The threshold piece's own waypoint and forward, where its planes come down and roll on from.</summary>
    internal static PadRunway At(Vec3 waypoint, Vec3 forward) => new PadRunway(waypoint, forward);

    /// <summary>Whether the pad has a threshold piece at all.</summary>
    internal bool Any => _any;

    /// <summary>The threshold's own waypoint (LandingPadTaxiThreshold.WaypointPosition).</summary>
    internal Vec3 Waypoint => _waypoint;

    /// <summary>The way its runway runs, the way its planes land and roll along (WaypointForward).</summary>
    internal Vec3 Forward => _forward;
}

/// <summary>
/// One landing pad's approach corridor: the path the game's trader shuttles fly, every leg swept by the same half
/// width, in world metres. The path runs the way the traders come in - the approach leg first, the pad's own drop
/// column last.
///
/// A pad with no waypoint chain flies the pad's own geometry (decompile Objects.Electrical/LandingPadCenter.cs): from
/// the deorbit point - the first approach waypoint, which here is the virtual waypoint VirtualWaypointHeight above the
/// pad's own, DeorbitDistance back along its forward and up from there (:1053, :1091) - onto that high waypoint, then
/// straight down onto the pad's waypoint, where it lands (:1189). WaypointPosition is the pad's origin, the size
/// check's centre offset and 0.754 m down (:277); the forward it lands facing is -Transform.forward (:279). The high
/// waypoint is the one the departure rises to as well (:1263), which is why the drop column stays on the path.
///
/// A pad whose approach runs a chain (a runway threshold linked through the taxi pieces,
/// LandingPadTaxiThreshold.LinkTaxiWaypoints:83-106, LandingPadNetwork.LinkTaxiWaypoints:155-173) flies the chain
/// instead: the first approach waypoint is the chain's own first piece rather than a virtual waypoint above the centre
/// (GetFirstApproachWaypoint, :1042-1055), so the deorbit point sits DeorbitDistance back along that piece's forward
/// (:1091) and the descent ends there - on the threshold, unless a nav beacon is linked ahead of it
/// (Waypoint.ValidateWaypoint, Waypoint.cs:345-355). From there the path is the chain, walked NextWaypoint at every
/// piece's own WaypointPosition (:1099-1101, :1150, :1165-1166); the plane in between rolls on from the threshold -
/// 2 x its RequiredRunwayLength (15 m a MediumPlane, 20 m a LargePlane, TraderContact.cs:84-89) along the threshold's
/// forward, the RunwayStop waypoint the walk itself moves that far (:1119-1121) - before taxiing in. The centre's own
/// drop column stays on the path too: the game puts a high waypoint above the centre for a shuttle that arrives from a
/// beacon (:1140) and for one that rises from there on departure (:1229).
///
/// The sweep's cross-section is the widest admitted trader's own square (PadApproach), the same half extent on every
/// leg: a TraderShuttle holds no collider or bounds member in code, so the pad size it needs is the only dimension of
/// a shuttle there is to sweep.
/// </summary>
internal readonly struct PadCorridor
{
    private readonly PadLeg[] _path;
    private readonly PadSweep _sweep;
    private readonly double _dropMetres;
    private readonly bool _chain;
    private readonly bool _runway;
    private readonly double _rolloutMetres;

    private PadCorridor(PadLeg[] path, PadSweep sweep, double dropMetres, bool chain, bool runway,
        double rolloutMetres)
    {
        _path = path;
        _sweep = sweep;
        _dropMetres = dropMetres;
        _chain = chain;
        _runway = runway;
        _rolloutMetres = rolloutMetres;
    }

    /// <summary>
    /// The corridor of a pad with no chain: the descent from the deorbit point to the virtual waypoint
    /// VirtualWaypointHeight above the pad's own waypoint, then the drop column onto it (:1053, :1091, :1189).
    /// </summary>
    internal static PadCorridor WithoutChain(Vec3 waypoint, Vec3 forward, double dropMetres, PadSweep sweep)
    {
        Vec3 high = waypoint + new Vec3(0.0, dropMetres, 0.0);
        Vec3 deorbit = DeorbitOf(high, forward, sweep);

        return new PadCorridor(new[] { new PadLeg(deorbit, high), new PadLeg(high, waypoint) }, sweep, dropMetres,
            chain: false, runway: false, rolloutMetres: 0.0);
    }

    /// <summary>
    /// The corridor of a pad whose approach runs a chain: the descent from the deorbit point onto the chain's first
    /// waypoint (:1091), the planes' roll on past the threshold (2 x RequiredRunwayLength, :1119-1121), the chain's
    /// ground path from that waypoint through every piece it walks to the centre (:1099-1101, :1150, :1165-1166), and
    /// the centre's drop column (:1140, :1229). The chain holds the pieces' own world points, the first approach
    /// waypoint first and the centre last; the runway is the threshold piece's own waypoint, which a nav beacon linked
    /// ahead of it leaves in the chain rather than at its head (Waypoint.cs:345-355).
    /// </summary>
    internal static PadCorridor WithChain(Vec3 first, Vec3 forward, IReadOnlyList<Vec3> chain, Vec3 centre,
        double dropMetres, PadSweep sweep, PadRunway runway)
    {
        Vec3 deorbit = DeorbitOf(first, forward, sweep);
        double rollout = runway.Any ? sweep.RunwayMetres * 2.0 : 0.0;
        List<PadLeg> path = new List<PadLeg>(chain.Count + 3) { new PadLeg(deorbit, first) };
        if (rollout > 0.0)
        {
            path.Add(new PadLeg(runway.Waypoint, runway.Waypoint + runway.Forward.Normalized * rollout));
        }

        Vec3 at = first;
        for (int index = 0; index < chain.Count; index++)
        {
            // A leg of no length carries nothing its neighbours do not (LinkTaxiWaypoints ties RunwayStop and
            // RunwayStart at the threshold's own position), so the path keeps the stretches that go somewhere.
            if (!Same(at, chain[index]))
            {
                path.Add(new PadLeg(at, chain[index]));
            }

            at = chain[index];
        }

        path.Add(new PadLeg(centre + new Vec3(0.0, dropMetres, 0.0), centre));
        return new PadCorridor(path.ToArray(), sweep, dropMetres, chain: true, runway: runway.Any,
            rolloutMetres: rollout);
    }

    /// <summary>The path's legs: the approach the traders come down first, the pad's own drop column last.</summary>
    internal IReadOnlyList<PadLeg> Legs => _path;

    /// <summary>The pad's own waypoint, where the shuttle lands (:1189): the end of the path's last leg.</summary>
    internal Vec3 Waypoint => _path[_path.Length - 1].To;

    /// <summary>The waypoint VirtualWaypointHeight above the pad's own, where the drop starts (:1053): the last leg's start.</summary>
    internal Vec3 High => _path[_path.Length - 1].From;

    /// <summary>The deorbit point the ship enters the atmosphere at (:1091): the start of the path's first leg.</summary>
    internal Vec3 Deorbit => _path[0].From;

    /// <summary>How far the drop column runs: VirtualWaypointHeight.</summary>
    internal double DropMetres => _dropMetres;

    /// <summary>Half the widest admitted trader's width: how far the sweep reaches off every leg.</summary>
    internal double HalfWidthMetres => _sweep.HalfWidthMetres;

    /// <summary>How far back from the first approach waypoint the deorbit point is (DeorbitDistance.x).</summary>
    internal double BackMetres => _sweep.BackMetres;

    /// <summary>How far above it the deorbit point is (DeorbitDistance.y).</summary>
    internal double UpMetres => _sweep.UpMetres;

    /// <summary>How far past the threshold the admitted planes roll on: 2 x RequiredRunwayLength (:1119-1121), 0 when none do.</summary>
    internal double RolloutMetres => _rolloutMetres;

    /// <summary>Whether a planned body's box the corridor sweeps holds a placement: any leg of the path meets it.</summary>
    internal bool Hits(Box3 body)
    {
        for (int index = 0; index < _path.Length; index++)
        {
            if (Meets(_path[index], body))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a point is inside the corridor.</summary>
    internal bool Hits(Vec3 point) => Hits(new Box3(point, point));

    /// <summary>The corridor in words, for a problem or warning: its width, its deorbit point, and how it reaches the pad.</summary>
    internal string Describe() => string.Format(CultureInfo.InvariantCulture,
        "{0:0.#} m wide, {1:0.#} m back and {2:0.#} m up to the deorbit point, {3}",
        HalfWidthMetres * 2.0, BackMetres, UpMetres, Reach());

    // How the path reaches the pad: down the pad's own column; or, on a pad whose chain carries its runway, down onto
    // the runway threshold and in along its taxi path - with the admitted planes' roll on named when they have one; or,
    // on a pad whose chain stands on no threshold, down onto that chain's first waypoint and in along the chain.
    private string Reach()
    {
        if (!_chain)
        {
            return string.Format(CultureInfo.InvariantCulture, "then {0:0.#} m down to the pad", _dropMetres);
        }

        if (!_runway)
        {
            return "then down onto the chain's first waypoint and in along the chain to the pad centre";
        }

        return _rolloutMetres > 0.0
            ? string.Format(CultureInfo.InvariantCulture,
                "then down onto the runway threshold, {0:0.#} m along the runway and the taxi path to the pad centre",
                _rolloutMetres)
            : "then down onto the runway threshold and the taxi path to the pad centre";
    }

    // The deorbit point of a first approach waypoint (:1091): that waypoint's own position, DeorbitDistance.x back
    // along its forward, and DeorbitDistance.y above it.
    private static Vec3 DeorbitOf(Vec3 waypoint, Vec3 forward, PadSweep sweep) =>
        waypoint - forward.Normalized * sweep.BackMetres + new Vec3(0.0, sweep.UpMetres, 0.0);

    private static bool Same(Vec3 a, Vec3 b) =>
        Math.Abs(a.X - b.X) < 1e-9 && Math.Abs(a.Y - b.Y) < 1e-9 && Math.Abs(a.Z - b.Z) < 1e-9;

    // One leg against a body's box grown by the sweep's half extent on every axis: the corridor is the leg plus the
    // widest trader's square, and a box meets that sweep where the leg meets the box grown by it (the slab test, the
    // leg's parameter range 0 to 1; a leg along an axis is a plane test on it).
    private bool Meets(PadLeg leg, Box3 body)
    {
        double near = 0.0;
        double far = 1.0;
        for (int axis = 0; axis < 3; axis++)
        {
            double low = body.Min[axis] - HalfWidthMetres;
            double high = body.Max[axis] + HalfWidthMetres;
            double start = leg.From[axis];
            double travel = leg.To[axis] - start;
            if (Math.Abs(travel) < 1e-9)
            {
                if (start < low || start > high)
                {
                    return false;
                }

                continue;
            }

            double enters = (low - start) / travel;
            double leaves = (high - start) / travel;
            if (enters > leaves)
            {
                double swap = enters;
                enters = leaves;
                leaves = swap;
            }

            if (enters > near)
            {
                near = enters;
            }

            if (leaves < far)
            {
                far = leaves;
            }

            if (near > far)
            {
                return false;
            }
        }

        return true;
    }
}
