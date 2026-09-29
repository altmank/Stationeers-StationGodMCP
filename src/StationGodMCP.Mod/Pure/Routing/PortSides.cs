#nullable enable

namespace StationGodMCP.Pure;

/// <summary>What a device port is to the power tick: the whole device's load, or one side of an input/output device.</summary>
internal enum PowerSide
{
    /// <summary>A device with no input/output sides: PowerTick counts its load once on its power cable's network.</summary>
    Device,

    /// <summary>An input/output device's InputConnection: GetUsedPower answers for its InputNetwork only.</summary>
    Input,

    /// <summary>An input/output device's OutputConnection: GetGeneratedPower answers for its OutputNetwork only.</summary>
    Output
}

/// <summary>
/// Which side of an input/output device a port is. Connection is a plain [Serializable] class, so Unity deserializes
/// a device's OutputConnection field and its OpenEnds entry as separate copies: reference equality between them is
/// always false. The copies share their Unity components, so the port is the output when its Transform is the output
/// connection's Transform (the game tells its ports apart by their Collider, ElectricalInputOutput.GetPassiveTooltip);
/// without either, its ConnectionRole decides (Output or Output2). The input is told the same way from InputConnection.
/// </summary>
internal static class PortSides
{
    /// <summary>NetworkType.Power: set on Power and PowerAndData ports, clear on Data-only ones.</summary>
    internal const int PowerFlag = 2;

    /// <param name="sameTransform">Both have a Transform: whether it is the same one; null when either lacks one.</param>
    /// <param name="sameCollider">Both have a Collider: whether it is the same one; null when either lacks one.</param>
    /// <param name="role">The port's ConnectionRole as an integer.</param>
    internal static bool IsOutput(bool? sameTransform, bool? sameCollider, int role) =>
        sameTransform ?? sameCollider ?? ChuteRoles.LetsOut(role);

    /// <summary>The same test against the device's InputConnection (Input or Input2 without shared components).</summary>
    internal static bool IsInput(bool? sameTransform, bool? sameCollider, int role) =>
        sameTransform ?? sameCollider ?? ChuteRoles.TakesIn(role);

    /// <summary>
    /// The side a port moves power on, by the game's own connection roles; null for a port that moves none. A
    /// Data-only port carries no power (Device finds its PowerCable among Power-flagged ends only). An input/output
    /// device moves power only through its InputConnection and OutputConnection (InputNetwork and OutputNetwork are
    /// their cables' networks), so any other port of it moves none; the output wins when a port is both.
    /// </summary>
    /// <param name="connectionType">The port's NetworkType as an integer.</param>
    /// <param name="inputOutput">The device is an ElectricalInputOutput.</param>
    /// <param name="output">The port is its OutputConnection (IsOutput).</param>
    /// <param name="input">The port is its InputConnection (IsInput).</param>
    internal static PowerSide? PowerOf(int connectionType, bool inputOutput, bool output, bool input) =>
        (connectionType & PowerFlag) == 0 ? null
        : !inputOutput ? PowerSide.Device
        : output ? PowerSide.Output
        : input ? PowerSide.Input
        : null;
}
