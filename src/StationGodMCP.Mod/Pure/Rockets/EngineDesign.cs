#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure.Rockets;

/// <summary>One input of an engine: its number (as its port role reads), what it is for, and the pipe it takes.</summary>
internal sealed class EngineInput
{
    internal EngineInput(int input, string role, string? pipe, double? pipeMaxKpa)
    {
        Input = input;
        Role = role;
        Pipe = pipe;
        PipeMaxKpa = pipeMaxKpa;
    }

    internal int Input { get; }

    /// <summary>What the input carries, in words.</summary>
    internal string Role { get; }

    /// <summary>gas or liquid: the content the feed takes; null for an input that takes no propellant.</summary>
    internal string? Pipe { get; }

    /// <summary>The pipe's rating, which the pressure-fed feeds scale their draw against; null where it does not.</summary>
    internal double? PipeMaxKpa { get; }
}

/// <summary>
/// The feed of one of the six engine classes as a designer needs it: the feed law's name, each input, and the class
/// limits (the most a tick moves, by moles, litres or pressure). The engine's load-time performance (MaxThrust,
/// exhaust velocity, fuel flow) is read from the live prefab, not here.
/// </summary>
internal sealed class EngineDesign
{
    internal EngineDesign(string feed, List<EngineInput> inputs, double? maxMolesPerTick = null,
        double? litresPerTick = null, double? maxPressurePerTickKpa = null, double? flowRateMinL = null,
        double? flowRateMaxL = null)
    {
        Feed = feed;
        Inputs = inputs;
        MaxMolesPerTick = maxMolesPerTick;
        LitresPerTick = litresPerTick;
        MaxPressurePerTickKpa = maxPressurePerTickKpa;
        FlowRateMinL = flowRateMinL;
        FlowRateMaxL = flowRateMaxL;
    }

    /// <summary>pumped_gas, pressure_fed_gas, pumped_liquid or pressure_fed_liquid.</summary>
    internal string Feed { get; }

    internal List<EngineInput> Inputs { get; }

    /// <summary>Pumped gas: moles a tick at full throttle.</summary>
    internal double? MaxMolesPerTick { get; }

    /// <summary>Pumped liquid: litres a tick at full throttle, both inputs together.</summary>
    internal double? LitresPerTick { get; }

    /// <summary>Pressure fed: the most pressure a tick moves (MAXPressurePerTick).</summary>
    internal double? MaxPressurePerTickKpa { get; }

    /// <summary>Pressure fed liquid: litres a tick at the lowest and highest input pressure.</summary>
    internal double? FlowRateMinL { get; }

    internal double? FlowRateMaxL { get; }

    /// <summary>The design of an engine class; null when the class is not one of the six.</summary>
    internal static EngineDesign? Of(string className)
    {
        const double gas = PressureFedFeed.GasPipeMaxKpa;
        const double liquid = PressureFedFeed.LiquidPipeMaxKpa;
        switch (className)
        {
            case "GovernedGasEngine":
                return new EngineDesign("pumped_gas",
                    new List<EngineInput> { new EngineInput(1, "propellant: fuel and oxidiser mixed, gas or liquid", "gas", null) },
                    maxMolesPerTick: 18.0);
            case "PressureFedGasEngine":
            case "PressureFedGasEngineHeavy":
                return new EngineDesign("pressure_fed_gas",
                    new List<EngineInput>
                    {
                        new EngineInput(1, "propellant, moved by its own pressure", "gas", gas),
                        new EngineInput(2, "propellant, moved by its own pressure; both inputs needed", "gas", gas)
                    },
                    maxPressurePerTickKpa: className == "PressureFedGasEngine" ? 5000.0 : 8500.0);
            case "PumpedLiquidEngine":
                return new EngineDesign("pumped_liquid",
                    new List<EngineInput>
                    {
                        new EngineInput(1, "liquid propellant: Setting % of the volume", "liquid", null),
                        new EngineInput(2, "liquid propellant: the rest; both inputs needed", "liquid", null)
                    },
                    litresPerTick: 0.55);
            case "PressureFedLiquidEngine":
            case "PressureFedLiquidEngineHeavy":
                bool heavy = className == "PressureFedLiquidEngineHeavy";
                return new EngineDesign("pressure_fed_liquid",
                    new List<EngineInput>
                    {
                        new EngineInput(1, "liquid propellant; its gas pressure sets the flow", "liquid", liquid),
                        new EngineInput(2, "heat exchange with the chamber after the burn, opened by its output setting; no propellant", null, null)
                    },
                    maxPressurePerTickKpa: heavy ? 6000.0 : 5000.0, flowRateMinL: 0.04, flowRateMaxL: heavy ? 1.5 : 0.8);
            default:
                return null;
        }
    }
}
