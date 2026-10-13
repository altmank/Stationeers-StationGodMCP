#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>move gas's options (the source and to forms, to or delete, named gases) and how a gas is split over a room's cells.</summary>
public sealed class MoveGasRoomTests
{
    private static GasPlaceOption? Place(string json, List<OptionProblem>? problems = null) =>
        GasPlaceOption.Of(JToken.Parse(json), "source", problems ?? new List<OptionProblem>());

    private static GasMoveOrder? Order(string source, string? to = null, bool delete = false, string[]? gases = null,
        double? amountMol = null, List<OptionProblem>? problems = null) =>
        GasMoveOrder.Of(JToken.Parse(source), to == null ? null : JToken.Parse(to), delete, gases, amountMol,
            allowBurst: false, joined: true, problems ?? new List<OptionProblem>());

    [Theory]
    [InlineData("\"171002\"", "171002")]
    [InlineData("\"looking_at\"", "looking_at")]
    public void AnIdIsAnAtmosphere(string json, string id)
    {
        GasPlaceOption.Atmosphere place = Assert.IsType<GasPlaceOption.Atmosphere>(Place(json));
        Assert.Equal(id, place.Id);
    }

    [Theory]
    [InlineData("\"planet\"")]
    [InlineData("\" Planet \"")]
    public void PlanetWord(string value)
    {
        Assert.IsType<GasPlaceOption.Planet>(Place(value));
    }

    [Fact]
    public void RoomById()
    {
        GasPlaceOption.Room room = Assert.IsType<GasPlaceOption.Room>(Place("{\"room\": \"77\"}"));
        Assert.Equal(77, room.RoomId.Value);
    }

    [Fact]
    public void RoomOfAThing()
    {
        GasPlaceOption.RoomOf room = Assert.IsType<GasPlaceOption.RoomOf>(Place("{\"room_of\": \"5000\"}"));
        Assert.Equal(5000, room.ThingId.Value);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"room\": \"1\", \"room_of\": \"2\"}")]
    [InlineData("{\"room_id\": \"1\"}")]
    [InlineData("{\"room\": \"abc\"}")]
    [InlineData("\"  \"")]
    [InlineData("true")]
    [InlineData("[\"1\"]")]
    public void AMalformedPlaceIsAProblemUnderItsOption(string json)
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        Assert.Null(Place(json, problems));
        OptionProblem problem = Assert.Single(problems);
        Assert.Equal("source", problem.Path);
        Assert.Contains("room_of", problem.Problem);
    }

    [Fact]
    public void AMoveNeedsToOrDeleteAndNotBoth()
    {
        Assert.NotNull(Order("\"5\"", to: "\"6\""));
        Assert.True(Order("\"5\"", delete: true)!.Deletes);

        List<OptionProblem> neither = new List<OptionProblem>();
        Assert.Null(Order("\"5\"", problems: neither));
        Assert.Equal("to", Assert.Single(neither).Path);
        List<OptionProblem> both = new List<OptionProblem>();
        Assert.Null(Order("\"5\"", to: "\"6\"", delete: true, problems: both));
        Assert.Contains("goes without delete", Assert.Single(both).Problem);
    }

    [Fact]
    public void ARoomOrThePlanetAsTheSourceNeedsTheGasesNamed()
    {
        List<OptionProblem> room = new List<OptionProblem>();
        Assert.Null(Order("{\"room\": \"7\"}", to: "\"6\"", problems: room));
        Assert.Equal("gases", Assert.Single(room).Path);
        Assert.NotNull(Order("{\"room\": \"7\"}", to: "\"6\"", gases: new[] { "Oxygen" }));

        List<OptionProblem> planet = new List<OptionProblem>();
        Assert.Null(Order("\"planet\"", delete: true, problems: planet));
        Assert.Equal("gases", Assert.Single(planet).Path);
    }

    [Fact]
    public void ThePlanetOnlyGivesGasUp()
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        Assert.Null(Order("\"planet\"", to: "\"6\"", gases: new[] { "Oxygen" }, problems: problems));
        Assert.Equal("delete", Assert.Single(problems).Path);
        Assert.NotNull(Order("\"planet\"", delete: true, gases: new[] { "Oxygen" }));
    }

    [Fact]
    public void AnAmountIsMoreThanZero()
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        Assert.Null(Order("\"5\"", delete: true, amountMol: 0.0, problems: problems));
        Assert.Equal("amount_mol", Assert.Single(problems).Path);
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
