#nullable enable

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.RemoteView;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>move player's options, where it lands a player, whom a name means, the move order's bytes, its reply and report.</summary>
public sealed class MovePlayerTests
{
    private static PlayerMoveOrder? Order(JToken? at, string? to = null, string? toPlayer = null,
        string? player = null, bool safeGround = false, List<OptionProblem>? problems = null) =>
        PlayerMoveOrder.Of(player, at, to, toPlayer, safeGround, problems ?? new List<OptionProblem>());

    private static JArray At(string json) => JArray.Parse(json);

    private static void AssertProblem(PlayerMoveOrder? order, List<OptionProblem> problems, string path, string reason)
    {
        Assert.Null(order);
        OptionProblem problem = Assert.Single(problems);
        Assert.Equal(path, problem.Path);
        Assert.Contains(reason, problem.Problem);
    }

    // ---- options ----

    [Fact]
    public void APointMovesThePlayerWithNothingElseAsked()
    {
        PlayerMoveOrder order = Order(At("[10, 2.5, -40]"))!;

        MoveDestination.Point point = Assert.IsType<MoveDestination.Point>(order.Destination);
        Assert.Equal(new Vec3(10, 2.5, -40), point.At);
        Assert.Equal("at", order.Destination.Form);
        Assert.Null(order.Player);
        Assert.False(order.SafeGround);
    }

    [Fact]
    public void ToAndToPlayerNameWhatToLandBeside()
    {
        PlayerMoveOrder thing = Order(null, to: "123456", player: "Ada", safeGround: true)!;
        Assert.Equal("123456", Assert.IsType<MoveDestination.NextToThing>(thing.Destination).Id);
        Assert.Equal("to", thing.Destination.Form);
        Assert.Equal("Ada", thing.Player);
        Assert.True(thing.SafeGround);

        PlayerMoveOrder player = Order(null, toPlayer: "Bob")!;
        Assert.Equal("Bob", Assert.IsType<MoveDestination.NextToPlayer>(player.Destination).Player);
        Assert.Equal("to_player", player.Destination.Form);
    }

    [Fact]
    public void ExactlyOneDestinationIsNeeded()
    {
        List<OptionProblem> none = new List<OptionProblem>();
        AssertProblem(Order(null, problems: none), none, "at", "is needed");
        List<OptionProblem> two = new List<OptionProblem>();
        AssertProblem(Order(At("[0, 0, 0]"), to: "5", problems: two), two, "at", "give one destination");
        List<OptionProblem> others = new List<OptionProblem>();
        AssertProblem(Order(null, to: "5", toPlayer: "Bob", problems: others), others, "at", "give one destination");
    }

    [Theory]
    [InlineData("[1, 2]")]
    [InlineData("[1, 2, \"3\"]")]
    public void APointThatIsNotThreeNumbersIsAProblem(string at)
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        AssertProblem(Order(At(at), problems: problems), problems, "at", "is a position");
    }

    [Fact]
    public void APointFarBeyondAnyWorldIsAProblem()
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        AssertProblem(Order(At("[0, 0, 250000]"), problems: problems), problems, "at", "beyond");
    }

    [Fact]
    public void ABlankNameIsAProblemUnderItsOption()
    {
        List<OptionProblem> toPlayer = new List<OptionProblem>();
        AssertProblem(Order(null, toPlayer: "  ", problems: toPlayer), toPlayer, "to_player", "not empty");
        List<OptionProblem> to = new List<OptionProblem>();
        AssertProblem(Order(null, to: " ", problems: to), to, "to", "not empty");
        List<OptionProblem> player = new List<OptionProblem>();
        AssertProblem(Order(At("[0, 0, 0]"), player: "", problems: player), player, "player", "not empty");
    }

    // ---- landing beside a body ----

    private static readonly Box3 Locker = new Box3(new Vec3(9.5, 0, 19.75), new Vec3(10.5, 2, 20.25));

    [Fact]
    public void APlayerLandsOnTheSideOfTheBodyFacingThem()
    {
        Vec3 east = Landing.Beside(Locker, new Vec3(30, 7, 20));
        Assert.Equal(10.5 + Landing.ClearanceM, east.X, 6);
        Assert.Equal(20.0, east.Z, 6);
        Assert.Equal(0.0, east.Y, 6);

        Vec3 south = Landing.Beside(Locker, new Vec3(10, 0, -100));
        Assert.Equal(10.0, south.X, 6);
        Assert.Equal(19.75 - Landing.ClearanceM, south.Z, 6);
    }

    [Fact]
    public void ADiagonalApproachClearsTheNearerSide()
    {
        Vec3 landed = Landing.Beside(Locker, new Vec3(20, 0, 30));

        // Along the diagonal, the thin z side is reached first: 0.25 / sin 45 from the centre, then the clearance.
        double reach = 0.25 / System.Math.Sqrt(0.5) + Landing.ClearanceM;
        Assert.Equal(10 + reach * System.Math.Sqrt(0.5), landed.X, 6);
        Assert.Equal(20 + reach * System.Math.Sqrt(0.5), landed.Z, 6);
        Assert.False(Locker.Contains(landed));
    }

    [Fact]
    public void FromStraightAboveThePlayerLandsOnPlusX()
    {
        Vec3 landed = Landing.Beside(Locker, new Vec3(10, 50, 20));

        Assert.Equal(10.5 + Landing.ClearanceM, landed.X, 6);
        Assert.Equal(20.0, landed.Z, 6);
    }

    [Fact]
    public void BesideAnotherPlayerIsBesideTheirBodyAtTheirFeet()
    {
        Box3 body = Landing.PlayerAt(new Vec3(0, 5, 0));
        Vec3 landed = Landing.Beside(body, new Vec3(0, 5, -10));

        Assert.Equal(5.0, body.Min.Y, 6);
        Assert.Equal(5.0 + Landing.PlayerHeightM, body.Max.Y, 6);
        Assert.Equal(new Vec3(0, 5, -(Landing.PlayerHalfWidthM + Landing.ClearanceM)), landed);
    }

    // ---- whom a name means ----

    private static readonly List<NamedPlayer<string>> Players = new List<NamedPlayer<string>>
    {
        new NamedPlayer<string>("ada", 1001, "Ada"),
        new NamedPlayer<string>("adam", 1002, "Adam"),
        new NamedPlayer<string>("bob", 2001, "Bob the Builder")
    };

    [Theory]
    [InlineData("1002", "adam")]
    [InlineData("ada", "ada")]
    [InlineData("ADAM", "adam")]
    [InlineData("builder", "bob")]
    [InlineData(" Bob the Builder ", "bob")]
    public void ANameOrIdMeansOnePlayer(string wanted, string body)
    {
        PlayerFound<string>.One one = Assert.IsType<PlayerFound<string>.One>(PlayerMatch.Find(Players, wanted));
        Assert.Equal(body, one.Player.Body);
    }

    [Fact]
    public void APartOfSeveralNamesIsAmbiguousAndListsThem()
    {
        PlayerFound<string>.Several several = Assert.IsType<PlayerFound<string>.Several>(PlayerMatch.Find(Players, "da"));

        Assert.Contains("Ada (1001)", several.Reason);
        Assert.Contains("Adam (1002)", several.Reason);
        Assert.DoesNotContain("Bob", several.Reason);
    }

    [Theory]
    [InlineData("Carol")]
    [InlineData("9999")]
    public void NoMatchListsEveryPlayer(string wanted)
    {
        PlayerFound<string>.None none = Assert.IsType<PlayerFound<string>.None>(PlayerMatch.Find(Players, wanted));

        Assert.Contains($"'{wanted}'", none.Reason);
        Assert.Contains("Ada (1001), Adam (1002), Bob the Builder (2001)", none.Reason);
    }

    [Fact]
    public void AWorldWithoutPlayersSaysSo() =>
        Assert.Contains("Players: none", Assert.IsType<PlayerFound<string>.None>(
            PlayerMatch.Find(new List<NamedPlayer<string>>(), "Ada")).Reason);

    // ---- the move order between games ----

    [Fact]
    public void AMoveOrderRoundTrips()
    {
        byte[] bytes = MoveWire.Encode(new MoveCommand(987654321012L, new Vec3(-120.5, 33.25, 4096)));

        Assert.Equal(MoveWire.Length, bytes.Length);
        Assert.Equal(ViewProtocol.Current, bytes[0]);
        MoveCommand read = Assert.IsType<WireRead<MoveCommand>.Read>(MoveWire.Decode(bytes)).Value;
        Assert.Equal(987654321012L, read.HumanId);
        Assert.Equal(new Vec3(-120.5, 33.25, 4096), read.To);
    }

    [Fact]
    public void MovesNeedTheProtocolThatCarriesThem() => Assert.True(ViewProtocol.Current >= 2);

    [Fact]
    public void AMoveOrderOfAnotherProtocolIsNotMade()
    {
        byte[] bytes = MoveWire.Encode(new MoveCommand(1, Vec3.Zero));
        bytes[0] = ViewProtocol.Current + 1;

        Assert.Equal(ViewProtocol.Current + 1,
            Assert.IsType<WireRead<MoveCommand>.OtherProtocol>(MoveWire.Decode(bytes)).Protocol);
    }

    [Fact]
    public void ACutLongOrNonFiniteMoveOrderIsRefused()
    {
        byte[] bytes = MoveWire.Encode(new MoveCommand(1, new Vec3(1, 2, 3)));
        for (int length = 0; length < bytes.Length; length++)
        {
            Assert.IsType<WireRead<MoveCommand>.Malformed>(MoveWire.Decode(bytes[..length]));
        }

        byte[] longer = new byte[bytes.Length + 1];
        bytes.CopyTo(longer, 0);
        Assert.IsType<WireRead<MoveCommand>.Malformed>(MoveWire.Decode(longer));

        byte[] nan = MoveWire.Encode(new MoveCommand(1, new Vec3(double.NaN, 0, 0)));
        Assert.IsType<WireRead<MoveCommand>.Malformed>(MoveWire.Decode(nan));
    }

    // ---- the reply ----

    [Fact]
    public void TheReplyGivesBothPlacesWithTheirRooms()
    {
        MovePlayerView view = new MovePlayerView(new ThingView(new ThingId(1001), "Character", "Ada"), "client",
            new PlaceView(new PositionView(1.04, 2, 3), "77", "Room"), new PlaceView(new PositionView(40, 2, 3), null, null),
            new MoveDestinationView("to", new ThingView(new ThingId(5), "StructureLocker", "Locker"), true), 39.04,
            "Sent.");

        JObject reply = Replies.Tree(PlayerMoveReplies.Acted(view));

        Assert.Equal(new[] { "player", "moved_by", "before", "after", "destination", "distance_m", "note" },
            reply.Properties().Select(key => key.Name));
        Assert.Equal("client", (string?)reply["moved_by"]);
        Assert.Equal("1001", (string?)reply["player"]!["reference_id"]);
        Assert.Equal(1.0, (double)reply["before"]!["position"]!["x"]!);
        Assert.Equal("77", (string?)reply["before"]!["room_id"]);
        Assert.Null(reply["before"]!["outside"]);
        Assert.True((bool)reply["after"]!["outside"]!);
        Assert.Null(reply["after"]!["room_id"]);
        Assert.Equal("to", (string?)reply["destination"]!["form"]);
        Assert.Equal("5", (string?)reply["destination"]!["next_to"]!["reference_id"]);
        Assert.True((bool)reply["destination"]!["safe_ground"]!);
        Assert.Equal(39.0, (double)reply["distance_m"]!);
        Assert.Equal("Sent.", (string?)reply["note"]);
    }

    [Fact]
    public void AReportGivesTheMoveAndThePlayerAsItsTarget()
    {
        MovePlayerView view = new MovePlayerView(new ThingView(new ThingId(1), null, "Ada"), "server",
            new PlaceView(new PositionView(0, 0, 0), null, null), new PlaceView(new PositionView(1, 0, 0), null, null),
            new MoveDestinationView("at", null, false), 1.0, null);

        JObject report = Replies.Tree(PlayerMoveReplies.Reported(view, "[cannot undo: player move]"));

        Assert.Null(report["note"]);
        Assert.Null(report["dry_run"]);
        Assert.Null(report["destination"]!["next_to"]);
        Assert.Null(report["destination"]!["safe_ground"]);
        Assert.True((bool)report["report_only"]!);
        Assert.Empty((JArray)report["problems"]!);
        JToken target = Assert.Single((JArray)report["targets"]!);
        Assert.Equal("1", (string?)target["id"]);
        Assert.Equal("[cannot undo: player move]", (string?)target["undo"]);
        Assert.Equal(1.0, (double)target["new"]!["x"]!);
    }
}
