#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>place_structure orient (1.4.3): scoring turns against an intent.</summary>
public sealed class OrientSearchTests
{
    private static GridStep S(string name) => RunModels.Step(name);

    private static readonly Vec3 At = new Vec3(719, 200.5, 668);

    // The room is on the +z side of the wall z = 668.
    private static readonly Func<Vec3, GridStep, bool> RoomOnPlusZ = (from, direction) => direction.Name == "+z";

    private static OrientCandidate Wall(string facing, string? refusal = null, int penalty = 0,
        List<OrientPort>? ports = null)
    {
        CubeRotation turn = CubeRotation.FromFacing(S(facing), S("+y"))!;
        return new OrientCandidate(turn, turn.Forward.Opposite, turn.Forward, At, ports ?? new List<OrientPort>(),
            refusal, penalty, null);
    }

    [Fact]
    public void AllTwentyFourTurnsStartWithTheIdentity()
    {
        Assert.Equal(24, CubeRotation.All.Count);
        Assert.Equal(CubeRotation.Identity, CubeRotation.All[0]);
        Assert.Equal(24, new HashSet<CubeRotation>(CubeRotation.All).Count);
    }

    [Fact]
    public void ControlsTowardTheRoomPicksTheTurnFacingIntoIt()
    {
        OrientIntent intent = new OrientIntent(MountIntent.Wall, true, OrientTarget.IntoRoom.Instance,
            new List<PortIntent>(), null, null);
        List<OrientScore> scores = new List<OrientScore>();
        foreach (string facing in new[] { "-z", "+x", "+z", "-x" })
        {
            scores.Add(OrientSearch.Score(Wall(facing), intent, RoomOnPlusZ));
        }

        List<OrientScore> ranked = OrientSearch.Rank(scores);
        Assert.Equal("+z", ranked[0].Candidate.Front.Name);
        Assert.Equal(0.0, ranked[0].Score);
        Assert.Equal("-z", ranked[3].Candidate.Front.Name);
    }

    [Fact]
    public void ARefusedTurnOrOneOffTheMountIsExcluded()
    {
        OrientIntent intent = new OrientIntent(MountIntent.Floor, true, null, new List<PortIntent>(), null, null);
        OrientScore wall = OrientSearch.Score(Wall("+z"), intent, RoomOnPlusZ);
        Assert.True(wall.Excluded);
        OrientScore refused = OrientSearch.Score(Wall("+z", "the face is taken"),
            new OrientIntent(null, true, null, new List<PortIntent>(), null, null), RoomOnPlusZ);
        Assert.True(refused.Excluded);
        Assert.Contains("cursor: the face is taken", refused.Reasons);
    }

    [Fact]
    public void LayoutConflictsWeighAgainstATurn()
    {
        OrientIntent intent = new OrientIntent(null, true, null, new List<PortIntent>(), null, null);
        List<OrientScore> ranked = OrientSearch.Rank(new List<OrientScore>
        {
            OrientSearch.Score(Wall("+z", penalty: 10), intent, RoomOnPlusZ),
            OrientSearch.Score(Wall("+x"), intent, RoomOnPlusZ)
        });
        Assert.Equal("+x", ranked[0].Candidate.Front.Name);
    }

    [Fact]
    public void APortIsPointedAtItsTarget()
    {
        OrientIntent intent = new OrientIntent(null, true, null,
            new List<PortIntent> { new PortIntent("Output", null, null, new OrientTarget.Along(S("+x"))) }, null, null);
        OrientPort outX = new OrientPort(1, "Pipe", "Output", "out", At, S("+x"));
        OrientPort outMinusX = new OrientPort(1, "Pipe", "Output", "out", At, S("-x"));
        OrientScore good = OrientSearch.Score(Wall("+z", ports: new List<OrientPort> { outX }), intent, RoomOnPlusZ);
        OrientScore bad = OrientSearch.Score(Wall("+z", ports: new List<OrientPort> { outMinusX }), intent,
            RoomOnPlusZ);
        Assert.Equal(0.0, good.Score);
        Assert.Equal(2 * OrientSearch.MissWeight, bad.Score);
    }

    [Fact]
    public void AReversiblePumpScoresBetterReversedWhenItsPortsPointTheOtherWay()
    {
        // Gas should come from +x and go to -x; the pump's input faces -x and its output +x.
        OrientIntent intent = new OrientIntent(null, true, null, new List<PortIntent>(),
            new OrientTarget.Along(S("+x")), new OrientTarget.Along(S("-x")));
        List<OrientPort> ports = new List<OrientPort>
        {
            new OrientPort(0, "Pipe", "Input", "in", At, S("-x")),
            new OrientPort(1, "Pipe", "Output", "out", At, S("+x"))
        };
        OrientCandidate pump = Wall("+z", ports: ports);
        OrientScore plain = OrientSearch.Score(pump, intent, RoomOnPlusZ);
        OrientScore reversed = OrientSearch.Score(pump, intent, RoomOnPlusZ, true);
        Assert.True(reversed.Score < plain.Score);
        Assert.Equal(reversed, OrientSearch.Rank(new List<OrientScore> { plain, reversed })[0]);
        Assert.True(reversed.ReversedFlow);
    }

    [Fact]
    public void APointTargetAlignsWithTheDirectionToIt()
    {
        OrientTarget player = new OrientTarget.At(new Vec3(719, 200.5, 672), "the player");
        Assert.Equal(1.0, player.Alignment(At, S("+z"), RoomOnPlusZ), 6);
        Assert.Equal(-1.0, player.Alignment(At, S("-z"), RoomOnPlusZ), 6);
        Assert.Equal(0.0, player.Alignment(At, S("+x"), RoomOnPlusZ), 6);
    }

    [Fact]
    public void MountIntentsAcceptTheRightBacks()
    {
        Assert.True(MountIntent.Wall.Accepts(S("-z")));
        Assert.False(MountIntent.Wall.Accepts(S("-y")));
        Assert.True(MountIntent.Floor.Accepts(S("-y")));
        Assert.True(MountIntent.Ceiling.Accepts(S("+y")));
        Assert.True(MountIntent.Side(S("-z")).Accepts(S("-z")));
        Assert.False(MountIntent.Side(S("-z")).Accepts(S("+z")));
    }
}
