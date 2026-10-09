#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Xunit;
using Xunit.Abstractions;

namespace StationGodMCP.Tests;

/// <summary>
/// network_snapshot without logic_types leaves out gas and liquid ratios reading 0 and counts them per device in
/// zero_ratios_omitted; include_zero_ratios, or naming logic_types, keeps them. One filtration unit's snapshot drops
/// from about 10 KB to about 2 KB.
/// </summary>
public sealed class ZeroRatioTests(ITestOutputHelper output)
{
    private static readonly string[] Gases =
    {
        "Oxygen", "CarbonDioxide", "Nitrogen", "Pollutant", "Methane", "Water", "NitrousOxide", "LiquidNitrogen",
        "LiquidOxygen", "LiquidMethane", "Steam", "LiquidCarbonDioxide", "LiquidPollutant", "LiquidNitrousOxide",
        "Hydrogen", "LiquidHydrogen", "PollutedWater", "Hydrazine", "LiquidHydrazine", "LiquidAlcohol", "Helium",
        "LiquidSodiumChloride", "Silanol", "LiquidSilanol", "HydrochloricAcid", "LiquidHydrochloricAcid", "Ozone",
        "LiquidOzone"
    };

    [Theory]
    [InlineData("RatioOxygen", 0.0, true)]
    [InlineData("RatioLiquidOzoneOutput2", 0.0, true)]
    [InlineData("RatioOxygen", 0.21, false)]
    [InlineData("Ratio", 0.0, false)]
    [InlineData("CompletionRatio", 0.0, false)]
    [InlineData("Pressure", 0.0, false)]
    [InlineData(null, 0.0, false)]
    public void OnlyAGasOrLiquidRatioReadingZeroIsSkipped(string? logicType, double value, bool skipped)
    {
        Assert.Equal(skipped, ZeroRatios.Skips(logicType, value));
    }

    [Fact]
    public void TheCountIsWrittenOnlyWhenSomethingWasLeftOut()
    {
        Assert.Null(Snapshot(0)["zero_ratios_omitted"]);
        Assert.Null(Snapshot(null)["zero_ratios_omitted"]);
        Assert.Equal(12, (int)Snapshot(12)["zero_ratios_omitted"]!);
    }

    [Fact]
    public void TheSidecarTakesIncludeZeroRatios()
    {
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(
            """{"reference_ids":["101"],"include_zero_ratios":true}""");
        Assert.Empty(StationGodMCP.Server.ArgumentCheck.Problems(
            StationGodMCP.Server.Program.InputSchemas["network_snapshot"], document.RootElement));
    }

    /// <summary>
    /// A filtration unit reads a ratio per gas for its input, its two outputs and its own atmosphere (28 gases, 112
    /// ratios), four of them non-zero, and 20 other logic types.
    /// </summary>
    [Fact]
    public void AFiltrationUnitSnapshotDropsToItsNonZeroValues()
    {
        List<object> all = new List<object>();
        List<object> kept = new List<object>();
        int skipped = 0;
        ushort id = 0;
        foreach (string suffix in new[] { "", "Input", "Output", "Output2" })
        {
            foreach (string gas in Gases)
            {
                double value = gas is "Oxygen" or "CarbonDioxide" && suffix is "" or "Input" ? 0.4 : 0.0;
                Add(new LogicTypeView(id++, "Ratio" + gas + suffix), value);
            }
        }

        for (int index = 0; index < 20; index++)
        {
            Add(new LogicTypeView(id++, "Setting" + index), 101.325 + index);
        }

        int before = WireCheck.New(new DeviceSnapshotView(DeviceWireTests.NewDevice(), all)).Length;
        int after = WireCheck.New(new DeviceSnapshotView(DeviceWireTests.NewDevice(), kept, skipped)).Length;
        output.WriteLine($"network_snapshot, one filtration unit: every value {before} bytes, zero ratios left out " +
                         $"{after} bytes ({skipped} left out)");

        Assert.Equal(108, skipped);
        Assert.True(after * 4 <= before, $"{after} bytes against {before}");

        void Add(LogicTypeView type, double value)
        {
            LogicValueView view = new LogicValueView(type, value);
            all.Add(view);
            if (ZeroRatios.Skips(type.Name, value))
            {
                skipped++;
            }
            else
            {
                kept.Add(view);
            }
        }
    }

    private static JObject Snapshot(int? omitted) =>
        JObject.Parse(WireCheck.New(new DeviceSnapshotView(DeviceWireTests.NewDevice(), new List<object>(), omitted)));
}
