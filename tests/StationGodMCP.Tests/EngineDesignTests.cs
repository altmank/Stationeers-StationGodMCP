#nullable enable

using StationGodMCP.Pure.Rockets;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>describe_prefab's engine block: each of the six engine classes has its feed, inputs and class limits.</summary>
public sealed class EngineDesignTests
{
    [Theory]
    [InlineData("GovernedGasEngine", "pumped_gas", 1)]
    [InlineData("PressureFedGasEngine", "pressure_fed_gas", 2)]
    [InlineData("PressureFedGasEngineHeavy", "pressure_fed_gas", 2)]
    [InlineData("PumpedLiquidEngine", "pumped_liquid", 2)]
    [InlineData("PressureFedLiquidEngine", "pressure_fed_liquid", 2)]
    [InlineData("PressureFedLiquidEngineHeavy", "pressure_fed_liquid", 2)]
    public void EveryEngineClassHasItsFeed(string className, string feed, int inputs)
    {
        EngineDesign design = EngineDesign.Of(className)!;

        Assert.Equal(feed, design.Feed);
        Assert.Equal(inputs, design.Inputs.Count);
    }

    [Fact]
    public void TheClassLimitsMatchTheFeedsTheForecastFlies()
    {
        Assert.Equal(18.0, EngineDesign.Of("GovernedGasEngine")!.MaxMolesPerTick);
        Assert.Equal(8500.0, EngineDesign.Of("PressureFedGasEngineHeavy")!.MaxPressurePerTickKpa);
        Assert.Equal(1.5, EngineDesign.Of("PressureFedLiquidEngineHeavy")!.FlowRateMaxL);
        Assert.Equal(0.55, EngineDesign.Of("PumpedLiquidEngine")!.LitresPerTick);
        Assert.Null(EngineDesign.Of("PressureFedLiquidEngine")!.Inputs[1].Pipe);
        Assert.Equal(PressureFedFeed.LiquidPipeMaxKpa, EngineDesign.Of("PressureFedLiquidEngine")!.Inputs[0].PipeMaxKpa);
        Assert.Null(EngineDesign.Of("NotAnEngine"));
    }
}
