#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// The structures fixes from round 5 of the headless live test (2026-09-29): the frame note is read from the snapped
/// placement (structures-36), a burst pipe or burnt cable is broken for remove_structure (structures-37), and a paste
/// is refused while another still places (structures-38).
/// </summary>
public sealed class StructuresRound5Tests
{
    private static readonly Vec3 Up = new Vec3(0, 1, 0);
    private static readonly GridStep UpStep = GridStep.All[2];

    // The cursor's small-grid snap of one coordinate (GridCenter(0.5, 0.25) with Mathf.Round, which rounds a half
    // to even): 229.9 and 230 land on 230, 230.25 on 230.5.
    private static double Snapped(double metres) =>
        Math.Round((metres - 0.5) / 0.5, MidpointRounding.ToEven) * 0.5 + 0.5;

    // structures-36: frame 1626 at y 231 (bottom plane 230). A battery aimed at 229.9, 230 or 230.25 stands in the
    // frame's cell, so each gets the note, and the note is read from the snapped placement.
    [Theory]
    [InlineData(229.9, 230.0)]
    [InlineData(230.0, 230.0)]
    [InlineData(230.25, 230.5)]
    public void EveryPointOnTheFramesBottomPlaneStandsInTheFrame(double given, double snapped)
    {
        Assert.Equal(snapped, Snapped(given));
        Assert.Equal(new GridCell(-10630, 2310, -7150),
            LargeCells.StoodIn(new Vec3(-1063, Snapped(given), -715), Up));
    }

    // structures-36: the set-down is tried from the snapped placement, so 229.9 answers as 230 does (already on the
    // plane: nothing to set down) instead of going down to the plane at 228.
    [Theory]
    [InlineData(229.9)]
    [InlineData(230.0)]
    public void TwoPointsSnappedToOnePlacementSetDownAlike(double given)
    {
        (_, double y, _) = CursorAim.OntoFloor(-1063, Snapped(given), -715, UpStep);
        Assert.Equal(230.0, y);
    }

    [Fact]
    public void AnOriginInsideTheCellStandsInIt()
    {
        Assert.Equal(2310, LargeCells.StoodIn(new Vec3(-1063, 231.5, -715), Up).Y);
        Assert.Equal(2290, LargeCells.StoodIn(new Vec3(-1063, 230, -715), new Vec3(0, -1, 0)).Y);
    }

    // structures-37: a burst pipe and a burnt cable are wrecks (Wrecks.IsBroken), and remove_structure judges a wreck
    // as broken: refused without allow_broken, a warning with it.
    [Theory]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    public void AWreckIsRefusedAsBrokenUnlessAllowed(bool gameBroken, bool pipeBurst, bool cableBurnt)
    {
        RemovalFacts facts = new RemovalFacts { Broken = HealthCondition.IsWreck(gameBroken, pipeBurst, cableBurnt) };
        GuardFinding refused = Assert.Single(RemovalRule.Judge(facts, new RemovalAllowance(false, false)));
        Assert.Equal("broken", refused.Code);
        Assert.Equal(GuardLevel.Refusal, refused.Level);
        List<GuardFinding> allowed = RemovalRule.Judge(facts, new RemovalAllowance(false, false, broken: true));
        Assert.Equal("broken_removed", Assert.Single(allowed).Code);
    }

    // structures-38: a paste still placing refuses a new one; a finished or cancelled one, or none, does not.
    [Fact]
    public void APasteStillPlacingRefusesANewOne()
    {
        string? busy = PasteGate.Busy(placing: true, complete: false, cancelled: false, created: 4);
        Assert.NotNull(busy);
        Assert.Contains("still placing (4 pieces", busy);
        Assert.Null(PasteGate.Busy(placing: true, complete: true, cancelled: false, created: 9));
        Assert.Null(PasteGate.Busy(placing: true, complete: false, cancelled: true, created: 2));
        Assert.Null(PasteGate.Busy(placing: false, complete: false, cancelled: false, created: 0));
    }
}
