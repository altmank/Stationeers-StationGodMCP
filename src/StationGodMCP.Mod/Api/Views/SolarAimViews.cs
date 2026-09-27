#nullable enable

using Newtonsoft.Json;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Api.Views;

/// <summary>solar_aim: what every reply carries. A panel that turns adds its angles; one that cannot, a note.</summary>
internal abstract class SolarAimView
{
    private protected SolarAimView(ThingId referenceId, string? prefabName, bool canTurn, SunView sun,
        PanelAimView current)
    {
        ReferenceId = referenceId;
        PrefabName = prefabName;
        CanTurn = canTurn;
        Sun = sun;
        Current = current;
    }

    [JsonProperty(Order = 1)]
    public ThingId ReferenceId { get; }

    [JsonProperty(Order = 2)]
    public string? PrefabName { get; }

    [JsonProperty(Order = 3)]
    public bool CanTurn { get; }

    [JsonProperty(Order = 20)]
    public SunView Sun { get; }

    [JsonProperty(Order = 21)]
    public PanelAimView Current { get; }
}

/// <summary>A panel without yaw and pitch pivots (the Flat panel): Horizontal and Vertical do not move it.</summary>
internal sealed class SolarFixedView : SolarAimView
{
    internal SolarFixedView(ThingId referenceId, string? prefabName, SunView sun, PanelAimView current)
        : base(referenceId, prefabName, canTurn: false, sun, current)
    {
    }

    [JsonProperty(Order = 10)]
    public string Note => "this panel has no yaw and pitch pivots, so Horizontal and Vertical writes do not move it";
}

/// <summary>A panel that turns: the angles that point it at the sun, and how close they get.</summary>
internal sealed class SolarTurnView : SolarAimView
{
    internal SolarTurnView(ThingId referenceId, string? prefabName, SunView sun, PanelAimView current,
        float horizontal, float vertical, double offSunDegrees, double alignment)
        : base(referenceId, prefabName, canTurn: true, sun, current)
    {
        Horizontal = horizontal;
        Vertical = vertical;
        OffSunDeg = offSunDegrees;
        AlignmentRatio = alignment;
    }

    [JsonProperty(Order = 10)]
    public float Horizontal { get; }

    [JsonProperty(Order = 11)]
    public float Vertical { get; }

    /// <summary>Degrees the best pose is still off the sun; above 0 only with the sun out of the tilt range.</summary>
    [JsonProperty(Order = 12)]
    public double OffSunDeg { get; }

    /// <summary>1 - 2 sin(off / 2), floored at 0: the panel's Ratio at that pose before shading.</summary>
    [JsonProperty(Order = 13)]
    public double AlignmentRatio { get; }
}

/// <summary>The sun as the game has it: OrbitalSimulation.WorldSunVector, normalised.</summary>
internal sealed class SunView
{
    internal SunView(float x, float y, float z, bool eclipse)
    {
        X = x;
        Y = y;
        Z = z;
        AboveHorizon = y > 0f;
        Eclipse = eclipse;
    }

    public float X { get; }

    public float Y { get; }

    public float Z { get; }

    public bool AboveHorizon { get; }

    public bool Eclipse { get; }
}

/// <summary>Where the panel points now, from its logic, and its output ratio.</summary>
internal sealed class PanelAimView
{
    internal PanelAimView(double horizontal, double vertical, float ratio)
    {
        Horizontal = horizontal;
        Vertical = vertical;
        Ratio = ratio;
    }

    public double Horizontal { get; }

    public double Vertical { get; }

    public float Ratio { get; }
}
