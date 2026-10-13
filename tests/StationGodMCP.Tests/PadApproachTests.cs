#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// The pad approach guard's pure half: one landing pad's corridor (the path its traders fly, every leg swept by the
/// widest admitted trader), the measures the widest admitted trader, the longest DeorbitDistance and the longest
/// RequiredRunwayLength over them give, and the allow word that lifts a corridor placement from a problem to a
/// warning the report marks PASSED. The game side (PadApproachGuard) only finds the pads, probes them with the game's
/// own CheckPadSize, walks their waypoint chains and builds these pieces from the game's objects.
///
/// The path is the pad's own waypoint geometry when the pad walks no chain, and the chain plus the planes' roll on
/// along the threshold when it does: the threshold piece's own waypoint, the taxi pieces, and the centre's own drop
/// column.
/// </summary>
public sealed class PadApproachTests
{
    private static readonly Vec3 Waypoint = new Vec3(0.0, 200.0, 0.0);

    private static readonly Vec3 Forward = new Vec3(0.0, 0.0, 1.0);

    // A pad with a runway: the threshold's waypoint 32 m from the centre's, facing it (a plane flies that forward,
    // comes down on the threshold and rolls on along it), and one taxi tile of the chain between the two.
    private static readonly Vec3 Threshold = new Vec3(0.0, 200.0, 32.0);

    private static readonly Vec3 Tile = new Vec3(0.0, 200.0, 22.0);

    private static readonly Vec3 Centre = new Vec3(0.0, 200.0, 12.0);

    private static readonly Vec3 RunwayForward = new Vec3(0.0, 0.0, -1.0);

    // A pad that admits only Small traders: 3 pad cells (6 m) wide, 100 m out and 75 m up, a 20 m drop.
    private static PadCorridor Small()
    {
        PadApproach admitted = new PadApproach();
        admitted.Admit(3, 3, 100, 75, 0);
        return admitted.Corridor(Waypoint, Forward, 20.0)!.Value;
    }

    // The chain a pad with a runway walks: the threshold, then the RunwayStop and RunwayStart waypoints
    // LinkTaxiWaypoints ties at the threshold's own position (LandingPadTaxiThreshold.cs:87-90), then the taxi tile,
    // then the centre's own waypoint, each at its own WaypointPosition.
    private static List<Vec3> Chain() => new List<Vec3> { Threshold, Threshold, Threshold, Tile, Centre };

    // The runway a plane lands on and rolls along: the threshold piece's own waypoint, which in a plain chain heads
    // the chain (a nav beacon linked ahead of it leaves it further in, Waypoint.ValidateWaypoint, Waypoint.cs:345-355).
    private static PadRunway ThresholdRunway() => PadRunway.At(Threshold, RunwayForward);

    // The corridor of a pad whose approach runs that chain, over the traders it admits.
    private static PadCorridor RunwayCorridor(double cells, double back, double up, double runway)
    {
        PadApproach admitted = new PadApproach();
        admitted.Admit(cells, cells, back, up, runway);
        return admitted.Corridor(Threshold, RunwayForward, Chain(), Centre, 20.0, ThresholdRunway())!.Value;
    }

    // A pad that admits a MediumPlane: 7 cells (14 m) wide, 250 m back and 90 m up, 15 m of runway past the threshold.
    private static PadCorridor Plane() => RunwayCorridor(7, 250, 90, 15);

    // The same pad admitting no plane: Small traders only, 3 cells (6 m) wide, 100 m back and 75 m up, no runway.
    private static PadCorridor NoPlane() => RunwayCorridor(3, 100, 75, 0);

    // ---- the corridor's shape ----

    [Fact]
    public void BothTheDropColumnAndTheApproachLegAreHeld()
    {
        PadCorridor corridor = Small();

        Assert.Equal(6.0, corridor.HalfWidthMetres * 2.0, 6);
        Assert.Equal(new Vec3(0.0, 220.0, 0.0), corridor.High);
        Assert.Equal(new Vec3(0.0, 295.0, -100.0), corridor.Deorbit);

        Assert.True(corridor.Hits(new Vec3(0.0, 210.0, 0.0)), "the drop column above the pad's waypoint");
        Assert.True(corridor.Hits(new Vec3(2.9, 208.0, 2.9)), "inside the column's width");
        Assert.True(corridor.Hits(new Vec3(0.0, 257.5, -50.0)), "the leg's own midline");
        Assert.True(corridor.Hits(new Vec3(2.0, 276.0, -75.0)), "the leg, 2 m off its axis");
    }

    [Fact]
    public void APointClearOfTheCorridorIsNotHeld()
    {
        PadCorridor corridor = Small();

        Assert.False(corridor.Hits(new Vec3(3.1, 210.0, 0.0)), "past the column's width");
        Assert.False(corridor.Hits(new Vec3(0.0, 190.0, 0.0)), "under the pad's own waypoint");
        Assert.False(corridor.Hits(new Vec3(0.0, 240.0, 40.0)), "behind the pad, off the leg");
        Assert.False(corridor.Hits(new Vec3(0.0, 300.0, -105.0)), "past the deorbit point");
    }

    [Fact]
    public void ABoxCrossingTheLegIsHeldAndOneBesideItIsNot()
    {
        PadCorridor corridor = Small();

        Box3 crossing = new Box3(new Vec3(-0.2, 240.0, -31.0), new Vec3(0.2, 245.0, -29.0));
        Box3 beside = new Box3(new Vec3(3.4, 240.0, -31.0), new Vec3(4.0, 245.0, -29.0));

        Assert.True(corridor.Hits(crossing));
        Assert.False(corridor.Hits(beside));
    }

    [Fact]
    public void ACorridorWithNoDropIsTheLegAlone()
    {
        PadApproach admitted = new PadApproach();
        admitted.Admit(3, 3, 100, 75, 0);
        PadCorridor corridor = admitted.Corridor(Waypoint, Forward, 0.0)!.Value;

        Assert.Equal(corridor.Waypoint, corridor.High);
        Assert.Equal(new Vec3(0.0, 275.0, -100.0), corridor.Deorbit);
        Assert.True(corridor.Hits(new Vec3(0.0, 200.0, 0.0)), "the pad's own waypoint");
        Assert.True(corridor.Hits(new Vec3(0.0, 240.0, -53.333)), "the leg's midline");
        Assert.False(corridor.Hits(new Vec3(0.0, 280.0, 0.0)), "above the leg's own height there");
    }

    // ---- the corridor with a chain ----

    [Fact]
    public void TheChainsDescentEndsOnTheThresholdAndItsRolloutRunsOnFromThere()
    {
        PadCorridor corridor = Plane();

        Assert.Equal(new Vec3(0.0, 290.0, 282.0), corridor.Deorbit);
        Assert.Equal(new Vec3(0.0, 200.0, 32.0), corridor.Legs[0].To);
        Assert.Equal(30.0, corridor.RolloutMetres, 6);
        Assert.Equal(new Vec3(0.0, 200.0, 2.0), corridor.Legs[1].To);

        Assert.True(corridor.Hits(new Vec3(0.0, 245.0, 157.0)), "on the descent, above the threshold");
        Assert.False(corridor.Hits(new Vec3(8.0, 245.0, 157.0)), "beside the descent, past its width");
        Assert.False(corridor.Hits(new Vec3(0.0, 290.0, 292.0)), "past the deorbit point");
    }

    [Fact]
    public void TheRolloutIsSweptPastTheChainsOwnEnd()
    {
        // The plane rolls 30 m past a threshold 20 m from the centre, so the last 10 m of its run is ground the
        // chain's own pieces never reach.
        Assert.True(Plane().Hits(new Vec3(0.0, 200.0, 6.0)), "a structure on the runway past the centre");
        Assert.False(NoPlane().Hits(new Vec3(0.0, 200.0, 6.0)), "the same spot where only Small traders land");

        Assert.Equal(5, Plane().Legs.Count);
        Assert.Equal(4, NoPlane().Legs.Count);
    }

    [Fact]
    public void TheRolloutRunsFromTheThresholdEvenWhenABeaconHeadsTheChain()
    {
        // A nav beacon linked ahead of the threshold: the shuttle comes down on the beacon (the chain's own first
        // waypoint, GetFirstApproachWaypoint) and then rolls along the runway from the threshold piece.
        Vec3 beacon = new Vec3(0.0, 240.0, 82.0);
        PadApproach admitted = new PadApproach();
        admitted.Admit(7, 7, 250, 90, 15);
        List<Vec3> chain = new List<Vec3> { beacon, Threshold, Threshold, Threshold, Tile, Centre };
        PadCorridor corridor = admitted.Corridor(beacon, RunwayForward, chain, Centre, 20.0, ThresholdRunway())!.Value;

        Assert.Equal(beacon, corridor.Legs[0].To);
        Assert.Equal(new Vec3(0.0, 330.0, 332.0), corridor.Deorbit);
        Assert.Equal(30.0, corridor.RolloutMetres, 6);
        Assert.Equal(new Vec3(0.0, 200.0, 2.0), corridor.Legs[1].To);
        Assert.True(corridor.Hits(new Vec3(0.0, 200.0, 6.0)));
    }

    [Fact]
    public void AChainOverNoThresholdHasNoRolloutAndIsWordedWithoutTheRunway()
    {
        PadApproach admitted = new PadApproach();
        admitted.Admit(7, 7, 250, 90, 15);
        PadCorridor corridor =
            admitted.Corridor(Threshold, RunwayForward, Chain(), Centre, 20.0, PadRunway.None)!.Value;

        Assert.Equal(0.0, corridor.RolloutMetres, 6);
        Assert.Equal(4, corridor.Legs.Count);
        Assert.False(corridor.Hits(new Vec3(0.0, 200.0, 3.0)), "past the chain's own end, with no threshold to roll on");
        Assert.Equal("14 m wide, 250 m back and 90 m up to the deorbit point, then down onto the chain's first " +
                     "waypoint and in along the chain to the pad centre", corridor.Describe());
    }

    [Fact]
    public void TheChainsGroundPathAndTheCentresColumnAreHeldAndTheGroundBesideThemIsNot()
    {
        PadCorridor corridor = Plane();

        Box3 onTheTaxiPath = new Box3(new Vec3(-0.2, 201.0, 16.0), new Vec3(0.2, 206.0, 18.0));
        Box3 besideTheTaxiPath = new Box3(new Vec3(8.0, 201.0, 16.0), new Vec3(9.0, 206.0, 18.0));

        Assert.True(corridor.Hits(onTheTaxiPath), "a structure on the taxi path between the tile and the centre");
        Assert.False(corridor.Hits(besideTheTaxiPath), "past the width of the path, on the ground");
        Assert.True(corridor.Hits(new Vec3(0.0, 214.0, 12.0)), "the centre's own drop column");
        Assert.False(corridor.Hits(new Vec3(0.0, 228.0, 12.0)), "above that column");
    }

    [Fact]
    public void TheChainsPathIsItsOwnPiecesInOrderAndThenTheCentresColumn()
    {
        PadCorridor corridor = Plane();

        Assert.Equal(new Vec3(0.0, 200.0, 32.0), corridor.Legs[2].From);
        Assert.Equal(Tile, corridor.Legs[2].To);
        Assert.Equal(Tile, corridor.Legs[3].From);
        Assert.Equal(Centre, corridor.Legs[3].To);
        Assert.Equal(new Vec3(0.0, 220.0, 12.0), corridor.Legs[4].From);
        Assert.Equal(Centre, corridor.Waypoint);
        Assert.Equal(new Vec3(0.0, 220.0, 12.0), corridor.High);
        Assert.Equal(20.0, corridor.DropMetres, 6);
    }

    [Fact]
    public void AChainCorridorIsWordedWithTheRunwayAndWithoutIt()
    {
        Assert.Equal("14 m wide, 250 m back and 90 m up to the deorbit point, then down onto the runway threshold, " +
                     "30 m along the runway and the taxi path to the pad centre", Plane().Describe());
        Assert.Equal("6 m wide, 100 m back and 75 m up to the deorbit point, then down onto the runway threshold " +
                     "and the taxi path to the pad centre", NoPlane().Describe());

        string refused = PadApproachRule.Refusal("StructureWallIron at (716, 203, 668)",
            "Landing Pad (LandingPadCenter 42)", Plane().Describe());

        Assert.Contains("then down onto the runway threshold, 30 m along the runway and the taxi path to the pad " +
                        "centre", refused);
        Assert.Contains("allow: [\"pad\"]", refused);
    }

    // ---- the width and the distances over the admitted traders ----

    [Fact]
    public void TheMeasuresAreTheWidestSizeAndTheLongestDistancesOverThoseAdmitted()
    {
        PadApproach admitted = new PadApproach();
        admitted.Admit(3, 3, 100, 75, 0);

        Assert.Equal(6.0, admitted.WidthMetres, 6);

        admitted.Admit(9, 9, 300, 100, 0);
        admitted.Admit(6, 6, 300, 260, 0);
        PadCorridor corridor = admitted.Corridor(Waypoint, Forward, 20.0)!.Value;

        Assert.Equal(18.0, corridor.HalfWidthMetres * 2.0, 6);
        Assert.Equal(300.0, corridor.BackMetres, 6);
        Assert.Equal(260.0, corridor.UpMetres, 6);
        Assert.Equal(20.0, corridor.DropMetres, 6);
    }

    [Fact]
    public void TheLongestAdmittedRunwayIsTheOneThePlanesRollOutOver()
    {
        PadApproach admitted = new PadApproach();
        admitted.Admit(7, 7, 250, 90, 15);

        Assert.Equal(15.0, admitted.RunwayMetres, 6);
        Assert.Equal(30.0,
            admitted.Corridor(Threshold, RunwayForward, Chain(), Centre, 20.0, ThresholdRunway())!.Value.RolloutMetres, 6);

        admitted.Admit(9, 9, 300, 100, 20);

        Assert.Equal(20.0, admitted.RunwayMetres, 6);
        Assert.Equal(40.0,
            admitted.Corridor(Threshold, RunwayForward, Chain(), Centre, 20.0, ThresholdRunway())!.Value.RolloutMetres, 6);
    }

    [Fact]
    public void TheWiderOfATradersTwoSidesSetsTheWidth()
    {
        PadApproach admitted = new PadApproach();
        admitted.Admit(3, 5, 100, 75, 0);

        Assert.Equal(10.0, admitted.WidthMetres, 6);
    }

    [Fact]
    public void APadThatAdmitsOnlySmallGuardsLessThanOneThatAdmitsLarge()
    {
        PadApproach small = new PadApproach();
        small.Admit(3, 3, 100, 75, 0);
        PadApproach large = new PadApproach();
        large.Admit(6, 6, 300, 260, 0);

        Box3 offAxis = new Box3(new Vec3(4.4, 205.0, -0.1), new Vec3(4.6, 212.0, 0.1));

        Assert.False(small.Corridor(Waypoint, Forward, 20.0)!.Value.Hits(offAxis));
        Assert.True(large.Corridor(Waypoint, Forward, 20.0)!.Value.Hits(offAxis));
        Assert.True(large.Corridor(Waypoint, Forward, 20.0)!.Value.Hits(new Vec3(4.5, 210.0, 0.0)));
    }

    [Fact]
    public void APadThatAdmitsNoTraderHasNoCorridor()
    {
        PadApproach none = new PadApproach();

        Assert.False(none.Any);
        Assert.Null(none.Corridor(Waypoint, Forward, 20.0));
        Assert.Null(none.Corridor(Threshold, RunwayForward, Chain(), Centre, 20.0, ThresholdRunway()));
    }

    // ---- the allow word ----

    [Fact]
    public void AllowPadLiftsACorridorPlacementFromAProblemToAWarningTheReportMarksPassed()
    {
        Assert.Equal("pad", PadApproachRule.Guard);
        Assert.Equal("pad_approach", PadApproachRule.Code);
        Assert.Equal(PadApproachVerdict.Clear, PadApproachRule.Of(held: false, allowPad: false, exempt: false));
        Assert.Equal(PadApproachVerdict.Clear, PadApproachRule.Of(held: false, allowPad: true, exempt: false));
        Assert.Equal(PadApproachVerdict.Refused, PadApproachRule.Of(held: true, allowPad: false, exempt: false));
        Assert.Equal(PadApproachVerdict.Lifted, PadApproachRule.Of(held: true, allowPad: true, exempt: false));
    }

    [Fact]
    public void APieceTheGameExemptsIsClearInTheSameCorridorThatRefusesAStructure()
    {
        // The game's own shuttle check returns before any damage for a structure that is a LandingPadModular
        // (decompile TraderShuttle.cs:204-208), so a pad tile, a taxi piece or the centre is never a collision. The
        // exemption is the game's own type test, which only the game side can make (the type is no part of this
        // assembly): the guard reads it and hands the answer in here, so the same corridor, the same held placement,
        // is a problem for a structure and clear - neither refused nor lifted - for a piece the game lets a shuttle
        // through. The threshold piece is no LandingPadModular, so it is refused like any other structure.
        PadCorridor corridor = Plane();
        Vec3 onTheRunway = new Vec3(0.0, 200.0, 6.0);

        bool held = corridor.Hits(onTheRunway);
        Assert.True(held, "the corridor the pad's planes roll out along holds a structure there");

        Assert.Equal(PadApproachVerdict.Refused, PadApproachRule.Of(held, allowPad: false, exempt: false));
        Assert.Equal(PadApproachVerdict.Lifted, PadApproachRule.Of(held, allowPad: true, exempt: false));
        Assert.Equal(PadApproachVerdict.Clear, PadApproachRule.Of(held, allowPad: false, exempt: true));
        Assert.Equal(PadApproachVerdict.Clear, PadApproachRule.Of(held, allowPad: true, exempt: true));
    }

    [Fact]
    public void TheRefusalNamesThePadItsCorridorAndHowToAllowIt()
    {
        string refused = PadApproachRule.Refusal("StructureWallIron at (716, 203, 668)",
            "Landing Pad (LandingPadCenter 42)", Small().Describe());

        Assert.Contains("is in the approach of the landing pad Landing Pad (LandingPadCenter 42)", refused);
        Assert.Contains("descend a corridor 6 m wide, 100 m back and 75 m up to the deorbit point, " +
                        "then 20 m down to the pad", refused);
        Assert.Contains("allow: [\"pad\"]", refused);
        Assert.DoesNotContain("PASSED", refused);
    }

    [Fact]
    public void TheWarningSaysItWasLetThroughAndThatItsRowSaysPassed()
    {
        string lifted = PadApproachRule.Lifted("StructureWallIron at (716, 203, 668)",
            "Landing Pad (LandingPadCenter 42)", Small().Describe());

        Assert.Contains("is in the approach of the landing pad Landing Pad (LandingPadCenter 42)", lifted);
        Assert.Contains("6 m wide, 100 m back and 75 m up to the deorbit point, then 20 m down to the pad", lifted);
        Assert.Contains("let it through by allow: [\"pad\"]", lifted);
        Assert.Contains("marks it PASSED", lifted);
    }
}
