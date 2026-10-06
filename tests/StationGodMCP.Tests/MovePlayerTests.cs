#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.RemoteView;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>move_player's arguments, where it lands a player, whom a name means, the move order's bytes, its reply.</summary>
public sealed class MovePlayerTests
{
    private static Args Of(string json) => new Args(JObject.Parse(json));

    private static void AssertInvalid(string json, string reason)
    {
        ApiException error = Assert.Throws<ApiException>(() => MoveRequest.Of(Of(json)));
        Assert.Equal(ApiErrors.InvalidArgumentCode, error.Code);
        Assert.Contains(reason, error.Message);
    }

    // ---- arguments ----

    [Fact]
    public void APointMovesThePlayerAtOnceWithNothingElseAsked()
    {
        MoveRequest request = MoveRequest.Of(Of("""{"at": [10, 2.5, -40]}"""));

        MoveDestination.Point point = Assert.IsType<MoveDestination.Point>(request.Destination);
        Assert.Equal(new Vec3(10, 2.5, -40), point.At);
        Assert.Equal("at", request.Destination.Form);
        Assert.Null(request.Player);
        Assert.False(request.SafeGround);
        Assert.False(request.DryRun);
    }

    [Fact]
    public void APointMayBeAnObject()
    {
        MoveRequest request = MoveRequest.Of(Of("""{"at": {"x": 1, "y": 2, "z": 3}, "safe_ground": true, "dry_run": true}"""));

        Assert.Equal(new Vec3(1, 2, 3), Assert.IsType<MoveDestination.Point>(request.Destination).At);
        Assert.True(request.SafeGround);
        Assert.True(request.DryRun);
    }

    [Fact]
    public void ToIdAndNearPlayerNameWhatToLandBeside()
    {
        MoveRequest thing = MoveRequest.Of(Of("""{"to_id": "123456", "player": "Ada"}"""));
        Assert.Equal(new ThingId(123456), Assert.IsType<MoveDestination.NextToThing>(thing.Destination).Thing);
        Assert.Equal("to_id", thing.Destination.Form);
        Assert.Equal("Ada", thing.Player);

        MoveRequest player = MoveRequest.Of(Of("""{"near_player": "Bob"}"""));
        Assert.Equal("Bob", Assert.IsType<MoveDestination.NextToPlayer>(player.Destination).Player);
        Assert.Equal("near_player", player.Destination.Form);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"at": [0, 0, 0], "to_id": "5"}""")]
    [InlineData("""{"to_id": "5", "near_player": "Bob"}""")]
    [InlineData("""{"at": null}""")]
    public void ExactlyOneDestinationIsNeeded(string json) => AssertInvalid(json, "exactly one destination");

    [Theory]
    [InlineData("""{"at": [1, 2]}""")]
    [InlineData("""{"at": [1, 2, "3"]}""")]
    [InlineData("""{"at": {"x": 1, "y": 2}}""")]
    [InlineData("""{"at": "1 2 3"}""")]
    public void APointThatIsNotThreeNumbersIsRefused(string json) => AssertInvalid(json, "must be a position");

    [Fact]
    public void APointFarBeyondAnyWorldIsRefused() => AssertInvalid("""{"at": [0, 0, 250000]}""", "beyond");

    [Theory]
    [InlineData("""{"to_id": "abc"}""", "to_id")]
    [InlineData("""{"near_player": "  "}""", "near_player")]
    [InlineData("""{"at": [0, 0, 0], "player": ""}""", "player")]
    [InlineData("""{"at": [0, 0, 0], "player": 5}""", "player")]
    [InlineData("""{"at": [0, 0, 0], "safe_ground": "yes"}""", "safe_ground")]
    public void ABadArgumentIsNamed(string json, string name) => AssertInvalid(json, name);

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
        MovePlayerView view = new MovePlayerView(false, new ThingView(new ThingId(1001), "Character", "Ada"), "client",
            new PlaceView(new PositionView(1.04, 2, 3), "77", "Room"), new PlaceView(new PositionView(40, 2, 3), null, null),
            new MoveDestinationView("to_id", new ThingView(new ThingId(5), "StructureLocker", "Locker"), true), 39.04,
            "Sent.");

        JObject reply = JObject.Parse(WireCheck.New(view));

        Assert.Equal("client", (string?)reply["moved_by"]);
        Assert.Equal("1001", (string?)reply["player"]!["reference_id"]);
        Assert.Equal(1.0, (double)reply["before"]!["position"]!["x"]!);
        Assert.Equal("77", (string?)reply["before"]!["room_id"]);
        Assert.Null(reply["before"]!["outside"]);
        Assert.True((bool)reply["after"]!["outside"]!);
        Assert.Null(reply["after"]!["room_id"]);
        Assert.Equal("5", (string?)reply["destination"]!["next_to"]!["reference_id"]);
        Assert.True((bool)reply["destination"]!["safe_ground"]!);
        Assert.Equal(39.0, (double)reply["distance_m"]!);
        Assert.Equal("Sent.", (string?)reply["note"]);
    }

    [Fact]
    public void APlainPointLeavesOutWhatItDoesNotHave()
    {
        MovePlayerView view = new MovePlayerView(true, new ThingView(new ThingId(1), null, "Ada"), "server",
            new PlaceView(new PositionView(0, 0, 0), null, null), new PlaceView(new PositionView(1, 0, 0), null, null),
            new MoveDestinationView("at", null, false), 1.0, null);

        JObject reply = JObject.Parse(WireCheck.New(view));

        Assert.Null(reply["note"]);
        Assert.Null(reply["destination"]!["next_to"]);
        Assert.Null(reply["destination"]!["safe_ground"]);
        Assert.True((bool)reply["dry_run"]!);
    }
}
