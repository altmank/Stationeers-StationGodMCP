#nullable enable

using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>move_gas rooms: the from/to argument forms and how a gas is split over a room's cells.</summary>
public sealed class MoveGasRoomTests
{
    private static GasPlaceArg? Place(string json) => GasPlaceArg.Optional(new Args(JObject.Parse(json)), "from");

    [Fact]
    public void ReferenceIdIsAnAtmosphere()
    {
        GasPlaceArg.Atmosphere place = Assert.IsType<GasPlaceArg.Atmosphere>(Place("{\"from\": \"171002\"}"));
        Assert.Equal(171002, place.Id.Value);
    }

    [Theory]
    [InlineData("\"planet\"")]
    [InlineData("\" Planet \"")]
    public void PlanetWord(string value)
    {
        Assert.IsType<GasPlaceArg.Planet>(Place("{\"from\": " + value + "}"));
    }

    [Fact]
    public void RoomById()
    {
        GasPlaceArg.Room room = Assert.IsType<GasPlaceArg.Room>(Place("{\"from\": {\"room_id\": \"77\"}}"));
        Assert.Equal(77, room.RoomId.Value);
    }

    [Fact]
    public void RoomOfAThing()
    {
        GasPlaceArg.RoomOf room =
            Assert.IsType<GasPlaceArg.RoomOf>(Place("{\"from\": {\"room_of\": \"5000\"}}"));
        Assert.Equal(5000, room.ThingId.Value);
    }

    [Fact]
    public void AbsentIsNull()
    {
        Assert.Null(Place("{}"));
        Assert.Null(Place("{\"from\": null}"));
    }

    [Theory]
    [InlineData("{\"from\": {}}")]
    [InlineData("{\"from\": {\"room_id\": \"1\", \"room_of\": \"2\"}}")]
    [InlineData("{\"from\": {\"room\": \"1\"}}")]
    [InlineData("{\"from\": {\"room_id\": \"1\", \"extra\": true}}")]
    [InlineData("{\"from\": {\"room_id\": \"abc\"}}")]
    [InlineData("{\"from\": \"room\"}")]
    [InlineData("{\"from\": true}")]
    [InlineData("{\"from\": [\"1\"]}")]
    public void MalformedIsInvalidArgument(string json)
    {
        ApiException error = Assert.Throws<ApiException>(() => Place(json));
        Assert.Equal(ApiErrors.InvalidArgumentCode, error.Code);
        Assert.Contains("room_of", error.Message);
    }

    [Fact]
    public void RequiredRefusesAbsent()
    {
        ApiException error =
            Assert.Throws<ApiException>(() => GasPlaceArg.Required(new Args(new JObject()), "to"));
        Assert.Equal(ApiErrors.InvalidArgumentCode, error.Code);
    }

    [Fact]
    public void TakenInProportionToWhatEachCellHolds()
    {
        double[] taken = GasShares.Proportional(new[] { 10.0, 30.0, 0.0, 60.0 }, 50.0);
        Assert.Equal(new[] { 5.0, 15.0, 0.0, 30.0 }, taken);
    }

    [Fact]
    public void AllOfItWithoutAnAmount()
    {
        Assert.Equal(new[] { 2.0, 3.0 }, GasShares.Proportional(new[] { 2.0, 3.0 }, null));
    }

    [Fact]
    public void AmountCappedAtTheTotal()
    {
        Assert.Equal(new[] { 2.0, 3.0 }, GasShares.Proportional(new[] { 2.0, 3.0 }, 1000.0));
    }

    [Fact]
    public void NothingHeldTakesNothing()
    {
        Assert.Equal(new[] { 0.0, 0.0 }, GasShares.Proportional(new[] { 0.0, -1.0 }, 5.0));
        Assert.Empty(GasShares.Proportional(new double[0], 5.0));
    }

    [Fact]
    public void SpreadByVolume()
    {
        double[] shares = GasShares.ByVolume(new[] { 8000.0, 6000.0, 2000.0 });
        Assert.Equal(new[] { 0.5, 0.375, 0.125 }, shares);
    }

    [Fact]
    public void NoVolumeSpreadsEvenly()
    {
        Assert.Equal(new[] { 0.5, 0.5 }, GasShares.ByVolume(new[] { 0.0, 0.0 }));
        Assert.Empty(GasShares.ByVolume(new double[0]));
    }
}
