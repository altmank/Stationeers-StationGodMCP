#nullable enable

using Assets.Scripts.Objects;
using StationGodMCP.Api.Shared.Game.Build;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// How a thing stands turned, in the forms build structure takes: its transform's rotation (Thing.ThingTransformRotation,
/// what build structure sets when it builds) as a quarter-turn rotation when it is one, else its Euler angles only; and
/// any rotation as the rotation_deg the reports give, in the same convention.
/// </summary>
internal static class Orientations
{
    internal static OrientationView Of(Thing thing) => Of(thing.ThingTransformRotation);

    internal static OrientationView Of(Quaternion rotation)
    {
        CubeRotation? turn = CubeRotation.FromQuaternion(rotation.x, rotation.y, rotation.z, rotation.w);
        return turn != null ? OrientationView.Of(turn) : OrientationView.OffGrid(BuildReports.Euler(rotation));
    }

    /// <summary>
    /// A rotation as rotation_deg: on the grid, the Euler quarter turns build structure's rotation reproduces exactly
    /// (CubeRotation.EulerDegreesOf); off it, the Euler angles as the game holds them.
    /// </summary>
    internal static RotationView Degrees(Quaternion rotation) =>
        CubeRotation.EulerDegreesOf(rotation.x, rotation.y, rotation.z, rotation.w) is { } turns
            ? new RotationView(turns.X, turns.Y, turns.Z)
            : BuildReports.Euler(rotation);
}
