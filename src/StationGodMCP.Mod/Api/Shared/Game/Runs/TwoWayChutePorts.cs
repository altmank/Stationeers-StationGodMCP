#nullable enable

using Assets.Scripts.Objects;
using Networks;
using Objects.Rockets;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game.Runs;

/// <summary>
/// Devices whose chute port carries items both ways whatever its ConnectionRole says: the rocket chute umbilicals
/// (male, socket and socket angle; the socket angle is a RocketChuteUmbilicalFemale too). Their one chute port is
/// labelled Input, but the partner pushes items out through it (ChuteRoles.OfDevicePort has the code).
/// </summary>
internal static class TwoWayChutePorts
{
    internal static bool Carries(Thing? thing) =>
        thing is RocketChuteUmbilicalMale || thing is RocketChuteUmbilicalFemale;

    /// <summary>The role a port of the thing has for the flow checks: None for a two-way chute port.</summary>
    internal static int RoleOf(Thing? thing, int type, int role) =>
        ChuteRoles.OfDevicePort(IsChute(type) && Carries(thing), role);

    /// <summary>"in", "out" or null for a port as the reports show it: null for a two-way chute port.</summary>
    internal static string? FlowOf(Thing? thing, int type, string role) =>
        IsChute(type) && Carries(thing) ? null : PortFlow.Of(role);

    private static bool IsChute(int type) => ((NetworkType)type & NetworkType.Chute) != NetworkType.None;
}
