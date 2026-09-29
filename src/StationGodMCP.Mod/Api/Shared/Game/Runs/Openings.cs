#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Pipes;
using Assets.Scripts.Objects.Structures;
using BepInEx.Configuration;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game.Runs;

/// <summary>
/// What a face structure is to a layout. Doors are the game's Door class and its subclasses (doors, airlocks, blast and
/// hangar doors, hatches, roll covers, the Force-Field Door mod's doors) and the robot arm door; a window shutter is a
/// Door device but sits over a window, so it counts as a window. Windows are WallTransparent (composite, reinforced,
/// padded and shuttered windows) except the floor gratings, which are floors people walk on.
/// </summary>
internal static class Openings
{
    internal static OpeningKind KindOf(Structure structure)
    {
        if (structure is WindowShutter || (structure is WallTransparent && !IsFloorGrating(structure)))
        {
            return OpeningKind.Window;
        }

        return structure.IsDoor || structure is RoboticArmDoor ? OpeningKind.Door : OpeningKind.Wall;
    }

    internal static bool IsDoor(Structure structure) => KindOf(structure) == OpeningKind.Door;

    private static bool IsFloorGrating(Structure structure) =>
        structure.PrefabName != null && structure.PrefabName.IndexOf("FloorGrating", StringComparison.Ordinal) >= 0;

    /// <summary>The small cells a piece joining one of the door's ports stands in: released from its keep-out.</summary>
    internal static HashSet<GridCell> PortCellsOf(Structure door)
    {
        HashSet<GridCell> cells = new HashSet<GridCell>();
        if (door is SmallGrid grid && grid.OpenEnds != null)
        {
            foreach (Connection end in grid.OpenEnds)
            {
                if (end?.Transform != null && end.ConnectionType != NetworkType.None)
                {
                    cells.Add(PieceShapes.Cell(end.GetLocalGrid()));
                }
            }
        }

        return cells;
    }

    /// <summary>
    /// The 2 m faces a door covers: its registered face points (Structure.BlockingGrids, the face lookup's keys), those
    /// that lie on a face (a multiple of 20 on one axis, odd metres on the other two).
    /// </summary>
    internal static List<GridCell> FacesOf(Structure door)
    {
        List<GridCell> faces = new List<GridCell>();
        Grid3[]? points = door.BlockingGrids;
        if (points == null)
        {
            return faces;
        }

        foreach (Grid3 point in points)
        {
            GridCell cell = PieceShapes.Cell(point);
            if (FacePoints.IsFace(cell) && !faces.Contains(cell))
            {
                faces.Add(cell);
            }
        }

        return faces;
    }
}

/// <summary>
/// The layout settings of the mod's configuration, section Layout: the door keep-out band. Read at load; each request
/// reads the current value.
/// </summary>
internal static class LayoutSettings
{
    private static ConfigEntry<double>? _doorBand;

    internal static DoorBand DoorBand
    {
        get
        {
            if (_doorBand == null)
            {
                return DoorBand.Default;
            }

            return Pure.DoorBand.FromMetres(_doorBand.Value, out _) ?? DoorBand.Default;
        }
    }

    internal static void Load(ConfigFile configuration)
    {
        _doorBand = configuration.Bind("Layout", "DoorKeepOutBand", Pure.DoorBand.DefaultMetres,
            new ConfigDescription(
                "Metres either side of a door's face plane that the route planners keep free and the place tools " +
                "flag (in_door_keepout), inside the door's own rectangle. 0 to 2 in 0.5 steps; 0 keeps only the " +
                "face plane (jambs, top edge, threshold). Applies to the next request.",
                new AcceptableValueRange<double>(0.0, Pure.DoorBand.MaximumMetres)));
        if (Pure.DoorBand.FromMetres(_doorBand.Value, out string? error) == null)
        {
            StationGodMod.LogWarning($"[Layout] DoorKeepOutBand: {error}; using {Pure.DoorBand.DefaultMetres} m.");
        }
    }
}
