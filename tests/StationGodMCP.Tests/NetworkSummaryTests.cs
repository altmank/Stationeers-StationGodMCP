#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Verbs;
using StationGodMCP.Tests.Verbs;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// survey network summarize on a pipe network: its summary carries the network's volume_l, pressure_kpa, temperature_k
/// and total_mol (what sizing a fill needs), and the option's help says where they are.
/// </summary>
public sealed class NetworkSummaryTests
{
    [Fact]
    public void APipeNetworksSummaryCarriesItsVolumePressureTemperatureAndMoles()
    {
        PipeSummaryView summary = new PipeSummaryView("Gas", 120.0, 101.3, 293.15, 4.99, 0.0,
            new List<NetworkGasView> { new NetworkGasView("Oxygen", "gas", 4.99) });
        JObject reply = JObject.FromObject(ReplyFields.Of(("network", null), ("summary", summary)),
            StationGodMCP.Api.Shared.ApiJson.Serializer);

        JObject written = (JObject)reply["summary"]!;
        Assert.Equal(120.0, (double)written["volume_l"]!);
        Assert.Equal(101.3, (double)written["pressure_kpa"]!);
        Assert.Equal(293.15, (double)written["temperature_k"]!);
        Assert.Equal(4.99, (double)written["total_mol"]!);
    }

    [Fact]
    public void SummarizesHelpNamesThePipeFacts()
    {
        string text = (string)VerbCatalogue.Verb("survey")["whats"]!["network"]!["options"]!["summarize"]!["description"]!;
        foreach (string key in new[] { "volume_l", "pressure_kpa", "temperature_k", "total_mol" })
        {
            Assert.Contains(key, text);
        }
    }
}
