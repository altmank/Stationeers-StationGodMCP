#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StationGodMCP.Pure;
using StationGodMCP.Server;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// Fixes from round 5 of the headless live test (2026-09-29), devices: an enum argument names exactly one member
/// ("Error,PressureInternal" wrote Setting); a numeric logic_type string out of range gets the range message; the
/// gateway codes and statuses are documented; mod_info no longer promises elapsed_ms in MCP results.
/// </summary>
public sealed class DevicesRound5Tests
{
    public enum Sample
    {
        None = 0,
        Error = 4,
        PressureInternal = 8,
        Setting = 12,
    }

    [Theory]
    [InlineData("Setting", Sample.Setting)]
    [InlineData("setting", Sample.Setting)]
    [InlineData(" Error ", Sample.Error)]
    public void OneNameIgnoringCaseAndSpacesParses(string text, Sample expected)
    {
        Assert.True(EnumName.TryParse(text, out Sample parsed));
        Assert.Equal(expected, parsed);
    }

    [Theory]
    [InlineData("Error,PressureInternal")]
    [InlineData("Error, PressureInternal")]
    [InlineData("Setting,Setting")]
    [InlineData("12")]
    [InlineData("4")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("Bogus")]
    public void CombinationsNumbersAndUnknownNamesAreNotANameOfTheEnum(string? text)
    {
        Assert.False(EnumName.TryParse(text, out Sample _));
    }

    [Theory]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("1e19")]
    [InlineData("1e400")]
    [InlineData("12.5")]
    [InlineData(" 70000 ")]
    [InlineData("+65536")]
    public void NumericTextIsNumberText(string text)
    {
        Assert.True(LogicTypeNumber.IsNumberText(text));
    }

    [Theory]
    [InlineData("Setting")]
    [InlineData("0x0C")]
    [InlineData("Infinity")]
    [InlineData("NaN")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Error,PressureInternal")]
    public void NamesAndGarbageAreNotNumberText(string? text)
    {
        Assert.False(LogicTypeNumber.IsNumberText(text));
    }

    [Fact]
    public void EveryGatewayIdNamesBothGatewayCodes()
    {
        List<string> tools = Program.InputSchemas
            .Where(tool => tool.Value.GetProperty("properties").TryGetProperty("gateway_id", out _))
            .Select(tool => tool.Key)
            .ToList();

        Assert.True(tools.Count >= 17, $"only {tools.Count} tools take gateway_id");
        foreach (string tool in tools)
        {
            // The gateway refusals are documented once, in the shared gateways topic every such tool leads to.
            string docs = CatalogueChecks.CatalogueTextTests.DocsOf(tool);
            Assert.Contains("gateway_not_found", docs);
            Assert.Contains("gateway_unavailable", docs);
            Assert.Contains("no_data_network", docs);
        }
    }

    [Fact]
    public void ListGatewaysNamesEveryStatus()
    {
        string description = ToolDescription("list_gateways");

        foreach (string status in new[] { "bypass", "ready", "incomplete", "no_data_network" })
        {
            Assert.Contains(status, description);
        }
    }

    [Fact]
    public void ModInfoPromisesElapsedMsOnlyInThePipeEnvelope()
    {
        string description = ToolDescription("mod_info");

        Assert.DoesNotContain("Every reply envelope also carries elapsed_ms", description);
        Assert.Contains("MCP tool results do not carry it", description);
    }

    // A tool's whole documentation: its description and everything tool_info leads to from it.
    private static string ToolDescription(string name) => CatalogueChecks.CatalogueTextTests.DocsOf(name);
}
