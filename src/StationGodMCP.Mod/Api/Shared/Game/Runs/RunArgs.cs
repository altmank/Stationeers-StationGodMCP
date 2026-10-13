#nullable enable

using Assets.Scripts;
using Assets.Scripts.Objects;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game.Runs;

/// <summary>
/// The positions a build run is given: world metres ([x, y, z] or {x, y, z}) snapped to the small-grid cell they fall
/// in (Grid3(position, 0.5, 0.25), as the game places a piece).
/// </summary>
internal static class RunArgs
{
    internal const int DefaultListLimit = ReplyDefaults.RunListed;

    internal static GridCell CellOf(Vector3 position) =>
        PieceShapes.Cell(GridController.World.WorldToLocalGrid(position, SmallGrid.SmallGridSize,
            SmallGrid.SmallGridOffset));

    /// <summary>
    /// No world reaches this far from the origin (a planet's terrain is a few km across); a position beyond it would
    /// overflow the grid's cell numbers.
    /// </summary>
    internal const float MaximumCoordinate = 100000f;

    internal static Vector3 PositionOf(JToken token, string name)
    {
        Vector3? position = null;
        if (token is JArray array && array.Count == 3 && Number(array[0], out float x) &&
            Number(array[1], out float y) && Number(array[2], out float z))
        {
            position = new Vector3(x, y, z);
        }
        else if (token is JObject item && item["x"] != null && Number(item["x"]!, out float ox) &&
                 item["y"] != null && Number(item["y"]!, out float oy) && item["z"] != null &&
                 Number(item["z"]!, out float oz))
        {
            position = new Vector3(ox, oy, oz);
        }

        if (!position.HasValue)
        {
            throw ApiErrors.InvalidArgument($"{name} must be a position: [x, y, z] or {{x, y, z}} " +
                "in metres.");
        }

        Vector3 found = position.Value;
        if (Mathf.Abs(found.x) > MaximumCoordinate || Mathf.Abs(found.y) > MaximumCoordinate ||
            Mathf.Abs(found.z) > MaximumCoordinate)
        {
            throw ApiErrors.InvalidArgument(
                $"{name} ({found.x}, {found.y}, {found.z}) is outside the world: no coordinate may " +
                $"be more than {MaximumCoordinate:0} m from the origin.");
        }

        return found;
    }

    private static bool Number(JToken token, out float value)
    {
        value = 0f;
        if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
        {
            return false;
        }

        double number = token.Value<double>();
        if (double.IsNaN(number) || double.IsInfinity(number))
        {
            return false;
        }

        value = (float)number;
        return true;
    }
}
