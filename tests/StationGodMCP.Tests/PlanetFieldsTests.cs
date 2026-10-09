#nullable enable

using System.Reflection;
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

    // PlanetWireTests' loaded planet, with a storm and Terraforming Reloaded.
    private static string Planet() => WireCheck.New(typeof(PlanetWireTests)
        .GetMethod("NewLoaded", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { true })!);
}
