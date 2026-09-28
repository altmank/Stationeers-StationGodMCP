#nullable enable

using Assets.Scripts.Objects;
using StationGodMCP.Api.Shared.Game.Build;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// How a thing stands turned, in the forms place_structure takes: its transform's rotation (Thing.ThingTransformRotation,
/// what place_structure sets when it builds) as a quarter-turn rotation when it is one, else its Euler angles only.
/// </summary>
internal static class Orientations
{
    internal static OrientationView Of(Thing thing) => Of(thing.ThingTransformRotation);

    internal static OrientationView Of(Quaternion rotation)
    {
        CubeRotation? turn = CubeRotation.FromQuaternion(rotation.x, rotation.y, rotation.z, rotation.w);
        return turn != null ? OrientationView.Of(turn) : OrientationView.OffGrid(BuildReports.Euler(rotation));
    }
}
