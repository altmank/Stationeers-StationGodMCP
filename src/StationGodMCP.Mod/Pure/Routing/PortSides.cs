#nullable enable

namespace StationGodMCP.Pure;

/// <summary>
/// Which side of an input/output device a port is. Connection is a plain [Serializable] class, so Unity deserializes
/// a device's OutputConnection field and its OpenEnds entry as separate copies: reference equality between them is
/// always false. The copies share their Unity components, so the port is the output when its Transform is the output
/// connection's Transform (the game tells its ports apart by their Collider, ElectricalInputOutput.GetPassiveTooltip);
/// without either, its ConnectionRole decides (Output or Output2).
/// </summary>
internal static class PortSides
{
    /// <param name="sameTransform">Both have a Transform: whether it is the same one; null when either lacks one.</param>
    /// <param name="sameCollider">Both have a Collider: whether it is the same one; null when either lacks one.</param>
    /// <param name="role">The port's ConnectionRole as an integer.</param>
    internal static bool IsOutput(bool? sameTransform, bool? sameCollider, int role) =>
        sameTransform ?? sameCollider ?? ChuteRoles.LetsOut(role);
}
