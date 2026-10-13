#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Views;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace StationGodMCP.Tests;

/// <summary>
/// planet goes through the shared shaping like every tool: a fields name with near keys answers fields_closest alone,
/// without the sixty-odd keys of fields_valid; a name with nothing near still gets fields_valid.
/// </summary>
public sealed class PlanetFieldsTests(ITestOutputHelper output)
{
    [Fact]
    public void ANearNameGetsFieldsClosestWithoutFieldsValid()
    {
        string shaped = ShapingChecks.ModFields(Planet(), new[] { "temperature_part" });
        JObject reply = JObject.Parse(shaped);
        output.WriteLine($"planet fields [temperature_part]: {shaped.Length} bytes");

        Assert.Equal(new[] { "temperature_part" }, reply["fields_unmatched"]!.ToObject<string[]>());
        Assert.Contains("temperature_parts", reply["fields_closest"]!["temperature_part"]!.ToObject<string[]>()!);
        Assert.Null(reply["fields_valid"]);
        Assert.True(shaped.Length <= 600, $"{shaped.Length} bytes");
    }

    [Fact]
    public void ANameWithNothingNearStillGetsFieldsValid()
    {
        string shaped = ShapingChecks.ModFields(Planet(), new[] { "co2" });
        JObject reply = JObject.Parse(shaped);
        output.WriteLine($"planet fields [co2]: {shaped.Length} bytes");

        Assert.Null(reply["fields_closest"]);
        Assert.Contains("carbon_dioxide_ratio", reply["fields_valid"]!.ToObject<string[]>()!);
    }

    // A loaded planet, with a storm and Terraforming Reloaded.
    private static string Planet() => WireCheck.New(NewLoaded(true));

    private static PlanetView NewLoaded(bool stormAndMod)
    {
        List<PlanetGasView> gases = new List<PlanetGasView>
        {
            new PlanetGasView(
                new PlanetGasAmount("CarbonDioxide", "Carbon Dioxide", "gas", 1234.5, 0.5, 3.25),
                new PlanetGasPhases(195.5, 517.0, null, 180.25)),
            new PlanetGasView(
                new PlanetGasAmount("Water", null, "liquid", 10.0, 0.0, null),
                new PlanetGasPhases(273.15, null, null, null))
        };
        ReservoirsView reservoirs = new ReservoirsView(
            new ReservoirView(100.0, 2.5, 12.0, new List<ReservoirGasView> { new ReservoirGasView("Water", 12.0) }),
            null,
            null);
        PlanetTank tank = new PlanetTank(
            8.0e9, 7.5e9, 1.0e6, stormAndMod ? 0.5 : (double?)null, 3.0e7, 12.0, 0.2, new SeaView(5.0, 1000.0, false));
        PlanetWeather weather = new PlanetWeather(
            9.5, 9.25, 260.5,
            new TemperaturePartsView(1.0, 2.0, 3.0, 4.0, stormAndMod ? 5.0 : 0.0, 6.0, 7.0),
            42.5f, 88.25f,
            new TemperatureRangeView(new TemperatureSweepView(200.0, 180.0, 300.0, 0.0), 190.0, 310.0),
            stormAndMod ? new StormView("StormDust") : null);
        return new PlanetView(
            new PlanetClock(123456.5, 1200.0),
            tank,
            weather,
            new PlanetAir(0.01, 0.0, 3.5, 4.5, 0.95),
            gases,
            reservoirs,
            new StartingAirView(30.0, 3.0e7, new List<ReservoirGasView> { new ReservoirGasView("CarbonDioxide", 3.0e7) }),
            stormAndMod ? new TerraformingModView(null, "not live", null) : null);
    }
}
