#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>move player's stand search, its option check, write interact's press rule and interactable lookup.</summary>
public sealed class StandTests
{
    private static readonly Box3 Battery = new(new Vec3(-0.5, 0, -0.5), new Vec3(0.5, 1, 0.5));

    private static StandFloor? Ground(Vec3 feet) => new("terrain");

    private static StandFloor? NoGround(Vec3 feet) => null;

    private static bool Clear(Vec3 eye, Vec3 point) => true;

    private static StandResult Find(Box3 target, StandOptions options, IReadOnlyList<StandObstacle> obstacles,
        System.Func<Vec3, StandFloor?>? floor = null, System.Func<Vec3, Vec3, bool>? line = null) =>
        StandSearch.Find(target, new Vec3(5, 0, 0), options, obstacles, floor ?? Ground, line ?? Clear);

    // ---- stand search ----

    [Fact]
    public void ClearStandIsOutsideEveryBodyOnAFloorAndInReach()
    {
        Box3 neighbour = new(new Vec3(0.5, 0, -0.5), new Vec3(1.5, 1, 0.5));
        List<StandObstacle> obstacles = new() { new("1", Battery), new("2", neighbour) };

        StandResult.Stand stand = Assert.IsType<StandResult.Stand>(Find(Battery, StandOptions.Default, obstacles));

        Box3 player = StandSearch.BodyWithClearance(stand.Feet, StandOptions.Default.ClearM);
        Assert.True(player.Penetration(Battery) <= 0.005);
        Assert.True(player.Penetration(neighbour) <= 0.005);
        Assert.Equal("terrain", stand.Floor);
        Assert.True(stand.EyeToTargetM <= StandOptions.Default.ReachM);
        Assert.True(stand.Tried > stand.Rejected.Count);
    }

    [Fact]
    public void TheNearestPassingPointToThePlayerWins()
    {
        StandResult.Stand stand = Assert.IsType<StandResult.Stand>(
            Find(Battery, StandOptions.Default, new List<StandObstacle> { new("1", Battery) }));

        Assert.True(stand.Feet.X > 0, "the player is at +x, so the stand is on that side");
    }

    [Fact]
    public void OnStandsOnTheTopOfTheTargetAndNamesThePadAsTheFloor()
    {
        Box3 pad = new(new Vec3(-1, 0, -1), new Vec3(1, 0.2, 1));
        StandOptions on = StandOptions.Default with { On = true };

        StandResult.Stand stand = Assert.IsType<StandResult.Stand>(StandSearch.Find(pad, new Vec3(0, 0, 0), on,
            new List<StandObstacle> { new("pad", pad) }, feet => new StandFloor("pad"), Clear));

        Assert.Equal("pad", stand.Floor);
        Assert.Equal(0.2, stand.Feet.Y, 6);
        Assert.True(pad.Contains(new Vec3(stand.Feet.X, 0.2, stand.Feet.Z)));
    }

    [Fact]
    public void ATargetBoxedInGivesNoStandWithCountsPerReason()
    {
        Box3 wall = new(new Vec3(-6, 0, -6), new Vec3(6, 2.5, 6));
        List<StandObstacle> obstacles = new() { new("target", Battery), new("wall", wall) };

        StandResult.NoStand none = Assert.IsType<StandResult.NoStand>(Find(Battery, StandOptions.Default, obstacles));

        Assert.True(none.Tried > 0);
        StandRejection first = Assert.Single(none.Rejected);
        Assert.Equal("overlaps target", first.Reason);
        Assert.Equal(none.Tried, first.Count);
        Assert.Contains(first.Reason, none.Describe());
    }

    [Fact]
    public void NoFloorIsRejectedWhenRequiredAndAllowedWithAny()
    {
        List<StandObstacle> obstacles = new() { new("1", Battery) };

        StandResult.NoStand none = Assert.IsType<StandResult.NoStand>(Find(Battery, StandOptions.Default, obstacles, NoGround));
        Assert.Contains(none.Rejected, r => r.Reason == "no_floor");

        StandOptions any = StandOptions.Default with { Floor = FloorRule.Any };
        StandResult.Stand stand = Assert.IsType<StandResult.Stand>(Find(Battery, any, obstacles, NoGround));
        Assert.Equal("none", stand.Floor);
    }

    [Fact]
    public void OutOfReachAndABlockedLineAreRejected()
    {
        List<StandObstacle> obstacles = new() { new("1", Battery) };
        StandOptions tiny = StandOptions.Default with { ReachM = 0.5 };

        StandResult.NoStand far = Assert.IsType<StandResult.NoStand>(Find(Battery, tiny, obstacles));
        Assert.Contains(far.Rejected, r => r.Reason == "out_of_reach");

        StandResult.NoStand blocked = Assert.IsType<StandResult.NoStand>(
            Find(Battery, StandOptions.Default, obstacles, line: (eye, point) => false));
        Assert.Contains(blocked.Rejected, r => r.Reason == "line_blocked");
    }

    [Fact]
    public void AFloorTileBelowTheFeetIsNotSomethingToStandClearOf()
    {
        Box3 tile = new(new Vec3(-10, -0.1, -10), new Vec3(10, 0.1, 10));
        List<StandObstacle> obstacles = new() { new("1", Battery), new("floor", tile) };

        Assert.IsType<StandResult.Stand>(Find(Battery, StandOptions.Default, obstacles));
    }

    // ---- option check ----

    private static StandOptions? Stand(string json, out List<OptionProblem> problems, string? toPlayer = null)
    {
        problems = new List<OptionProblem>();
        PlayerMoveOrder? order = PlayerMoveOrder.Of(null, null, toPlayer == null ? "123" : null, toPlayer, false, problems,
            JToken.Parse(json));
        return order?.Stand;
    }

    [Fact]
    public void ReachAboveTheGamesReachIsRefusedAtTheOptionCheck()
    {
        Assert.Null(Stand("{\"reach_m\": 4}", out List<OptionProblem> problems));

        OptionProblem problem = Assert.Single(problems);
        Assert.Equal("stand.reach_m", problem.Path);
    }

    [Fact]
    public void AnEmptyStandTakesEveryDefault()
    {
        Assert.Equal(StandOptions.Default, Stand("{}", out _));
    }

    [Fact]
    public void StandOptionsAreReadAndUnknownKeysRefused()
    {
        StandOptions? given = Stand("{\"reach_m\": 2, \"clear_m\": 0.3, \"floor\": \"any\", \"on\": true}", out _);
        Assert.Equal(new StandOptions(2, 0.3, FloorRule.Any, true), given);

        Assert.Null(Stand("{\"nope\": 1}", out List<OptionProblem> problems));
        Assert.Equal("stand.nope", Assert.Single(problems).Path);
    }

    [Fact]
    public void StandWithToPlayerIsRefused()
    {
        Assert.Null(Stand("{}", out List<OptionProblem> problems, toPlayer: "Ada"));
        Assert.Contains(problems, p => p.Path == "stand");
    }

    [Fact]
    public void AStandBoundFromRealJsonSurvivesTheGeneratedOptions()
    {
        MovePlayerOptions bound = MovePlayerOptions.Bind(new OptionReader(MovePlayerOptions.Spec,
            JObject.Parse("{\"to\": \"123\", \"stand\": {\"on\": true}}")));
        List<OptionProblem> problems = new();

        PlayerMoveOrder? order = PlayerMoveOrder.Of(bound.Player, bound.At, bound.To, bound.ToPlayer,
            bound.SafeGround == true, problems, bound.Stand);

        Assert.True(order!.Stand!.On);
        Assert.False(order.SafeGround);
        MovePlayerOptions plain = MovePlayerOptions.Bind(new OptionReader(MovePlayerOptions.Spec,
            JObject.Parse("{\"to\": \"123\"}")));
        Assert.Null(plain.Stand);
        Assert.Null(PlayerMoveOrder.Of(plain.Player, plain.At, plain.To, plain.ToPlayer, plain.SafeGround == true,
            new List<OptionProblem>(), plain.Stand)!.Stand);
    }

    // ---- press rule ----

    [Fact]
    public void ACloseAcceptedPressWouldAccept()
    {
        Assert.IsType<PressDecision.WouldAccept>(PressRule.Decide(1.0, false, 3.0, () => new DryPress(true, null)));
    }

    [Fact]
    public void FarWithoutForceIsOutOfRangeAndTheGameIsNeverAsked()
    {
        bool asked = false;

        PressDecision decision = PressRule.Decide(10.0, false, 3.0, () =>
        {
            asked = true;
            return new DryPress(true, null);
        });

        Assert.Equal(PressRefusal.OutOfRange, Assert.IsType<PressDecision.WouldRefuse>(decision).Kind);
        Assert.False(asked);
    }

    [Fact]
    public void ForceSkipsTheReachCheckButNotTheGamesAnswer()
    {
        Assert.IsType<PressDecision.WouldAccept>(PressRule.Decide(10.0, true, 3.0, () => new DryPress(true, null)));

        PressDecision.WouldRefuse refused = Assert.IsType<PressDecision.WouldRefuse>(
            PressRule.Decide(10.0, true, 3.0, () => new DryPress(false, "Device is locked\n")));
        Assert.Equal(PressRefusal.NotAccepted, refused.Kind);
        Assert.Equal("Device is locked", refused.Message);
    }

    [Fact]
    public void ARefusalWithNoTextStillSaysSo()
    {
        PressDecision.WouldRefuse refused = Assert.IsType<PressDecision.WouldRefuse>(
            PressRule.Decide(1.0, false, 3.0, () => new DryPress(false, " ")));

        Assert.Contains("no reason", refused.Message);
    }

    // ---- interactable lookup ----

    private static readonly List<InteractableRow> Rows = new()
    {
        new(0, "Power", "OnOff", 0, 2), new(1, "Mode", "Mode", 1, null), new(2, "Button 1", "Button1", 0, null),
        new(3, "Button 1", "Button1", 0, null)
    };

    [Fact]
    public void AnInteractableIsFoundByNameByIndexAndByAction()
    {
        Assert.Equal(0, Assert.IsType<InteractableChoice.Found>(InteractableChoice.Of(Rows, "power")).Index);
        Assert.Equal(1, Assert.IsType<InteractableChoice.Found>(InteractableChoice.Of(Rows, "1")).Index);
        Assert.Equal(0, Assert.IsType<InteractableChoice.Found>(InteractableChoice.Of(Rows, "OnOff")).Index);
    }

    [Fact]
    public void AnUnknownNameListsTheNamesPresent()
    {
        InteractableChoice.Missing missing = Assert.IsType<InteractableChoice.Missing>(InteractableChoice.Of(Rows, "Nope"));

        Assert.Equal(Rows.Count, missing.Names.Count);
        Assert.Contains("0: Power", missing.Names);
        Assert.IsType<InteractableChoice.Missing>(InteractableChoice.Of(Rows, "9"));
    }

    [Fact]
    public void ARepeatedNameIsAmbiguousWithItsIndexes()
    {
        InteractableChoice.Ambiguous ambiguous = Assert.IsType<InteractableChoice.Ambiguous>(
            InteractableChoice.Of(Rows, "Button 1"));

        Assert.Equal(new[] { 2, 3 }, ambiguous.Indexes);
    }

    [Fact]
    public void AWriteInteractBoundFromRealJsonKeepsItsDefaults()
    {
        WriteInteractOptions bound = WriteInteractOptions.Bind(new OptionReader(WriteInteractOptions.Spec,
            JObject.Parse("{\"id\": \"123\", \"interactable\": 2}")));

        Assert.Equal(JTokenType.Integer, bound.Interactable!.Type);
        Assert.Null(bound.State);
        Assert.Equal("empty", bound.Hand);
        Assert.False(bound.Force);
    }
}
