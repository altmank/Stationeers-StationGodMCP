#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Assets.Scripts.Util;
using Objects.Structures;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Paging;
using UnityEngine;
using StationGodMCP.WorldModel;
using World = StationGodMCP.WorldModel.World;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// Outer frames: which frames (iron, steel, corner, side) have a face on a cell whose gas is the world's.
///
/// How the game places air (CODE): there is no Room atmosphere mode (AtmosphereHelper.AtmosphereMode is World,
/// Network, Thing, Global, None). A 2 m cell's atmosphere is AtmosphericsManager's World-mode instance stored under the
/// cell centre, and a cell with none reads the one shared planet atmosphere
/// (AtmosphericsController.SampleGlobalAtmosphere). Rooms are flood fills (RoomFloodFiller, at most 1200 cells) through
/// faces and cells that do not block gravity; a fill that closes becomes a Room in RoomController.RoomLookup, one that
/// hits the limit is outside and gets none. So "not in a room" is the game's own "outside": storms weather loose items
/// with no Room (DynamicThing.CanBeWeathered) and the storm screen effect is on when the camera's cell has no Room
/// (WeatherManager). Air between two cells is per face (Atmosphere.DoOpenNeighbours): closed by the neighbour's centre
/// structure when it does not let air pass, by terrain (AtmosphereHelper.CanContainAtmos, GridController
/// .IsVoxelFaceOpen), or by a face structure (a wall) that does not, looked up on both sides because a wall is
/// registered in only one of the two cells (Cell.IsOpenAir with a face).
///
/// Units (CODE): Grid3 is decimetres (the Grid3 constructor multiplies by 10). Large cells are 2 m with centres at odd
/// metres; Thing.WorldGrid is the cell centre of the thing's centre, and the six neighbours are Grid3.East, West, Up,
/// Down, North and South, 20 decimetres away. RoomLookup and GridController.GetCell are keyed by those centres.
///
/// The rule: a face is exposed when the neighbour cell across it (1) is in no Room; (2) can hold air: its centre
/// structure, if any, lets air pass and terrain does not fill it (Cell.IsOpenAir, AtmosphereHelper.CanContainAtmos),
/// so an airtight frame next door hides that face; (3) is open to the frame through the shared face: no terrain and no
/// air-blocking face structure on either cell; (4) when its centre holds a structure that blocks gravity but not air
/// (a one-sheet frame, which no room ever includes), its air passes the game's storm exposure test,
/// Structure.IsExposedToGlobal: no atmosphere instance, the global atmosphere, or a World one IsCloseToGlobal(1 kPa).
/// Limits: a sealed space over 1200 cells has no Room, so frames facing into it count as outer; rooms are walled by
/// gravity, not air, so a space closed only by one-sheet frames is a Room though it leaks; each frame is judged from
/// its one centre cell; crew modules count as not holding world air.
/// </summary>
internal static class OuterFrames
{
    /// <summary>The air rules of the loaded world; refused not_ready while its grid or rooms are not loaded.</summary>
    internal static CellAir Air()
    {
        GridController grid = GridController.World;
        RoomController rooms = RoomController.World;
        return grid != null && rooms != null
            ? new CellAir(grid, rooms)
            : throw ApiErrors.Refused("not_ready", "find frame: the world grid or the room controller is not loaded.");
    }

    /// <summary>One frame as it is now, its exposed faces measured again; null when it is gone.</summary>
    internal static FrameRow? Live(long id, CellAir air, PlayerOrigin origin) =>
        Thing.Find(id) is Frame frame && !frame.IsBeingDestroyed
            ? new FrameRow(frame, FrameCensus.ExposedFaces(frame, air), origin.ExactDistanceTo(frame.Position))
            : null;
}

/// <summary>
/// Every frame in the world with its exposed faces, and the counts: a scan spread over frames (Scan, a ScanBuild body),
/// one frame of the world index's Frame class per step.
/// </summary>
internal sealed class FrameCensus
{
    private static readonly string[] FaceNames = { "+x", "-x", "+y", "-y", "+z", "-z" };

    private readonly CellAir _air;
    private readonly PlayerOrigin _origin;
    private readonly double? _near;
    private readonly bool _includeInner;

    internal FrameCensus(CellAir air, PlayerOrigin origin, double? near, bool includeInner)
    {
        _air = air;
        _origin = origin;
        _near = near;
        _includeInner = includeInner;
    }

    /// <summary>The listed frames by reference id, as the scan has found them so far.</summary>
    internal Dictionary<long, FrameRow> Rows { get; } = new Dictionary<long, FrameRow>();

    internal int TotalFrames { get; private set; }

    internal int TotalOuter { get; private set; }

    /// <summary>
    /// The scan: the Frame class's candidates (World.Candidates), each checked, measured and keyed as its turn comes,
    /// one per step. A frame gone meanwhile (no longer in the structures' list) is passed over.
    /// </summary>
    internal IEnumerable<object?> Scan(ScanKeys keys)
    {
        List<Thing> candidates = World.Candidates(WorldClass.Frame);
        yield return null;
        for (int index = 0; index < candidates.Count; index++)
        {
            keys.Candidates++;
            if (World.Holds(WorldClass.Frame, candidates[index]) && candidates[index] is Frame frame &&
                !frame.IsCursor && !frame.IsBeingDestroyed)
            {
                FrameRow? row = Add(frame);
                if (row != null)
                {
                    keys.Add(row.Key);
                }
            }

            yield return null;
        }
    }

    private FrameRow? Add(Frame frame)
    {
        double? distance = _origin.ExactDistanceTo(frame.Position);
        if (_near.HasValue && !(distance <= _near.Value))
        {
            return null;
        }

        TotalFrames++;
        List<string> exposed = ExposedFaces(frame, _air);
        TotalOuter += exposed.Count > 0 ? 1 : 0;
        if (exposed.Count == 0 && !_includeInner)
        {
            return null;
        }

        FrameRow row = new FrameRow(frame, exposed, distance);
        Rows[frame.ReferenceId] = row;
        return row;
    }

    /// <summary>The frame's exposed faces, by name: the followed masks (FrameFaces), rule 4's air read now.</summary>
    internal static List<string> ExposedFaces(Frame frame, CellAir air)
    {
        List<string> exposed = new List<string>(FaceNames.Length);
        if (frame.WorldGrid == WorldGrid.INVALID)
        {
            return exposed;
        }

        Grid3 cell = frame.WorldGrid.Value;
        int faces = FrameFaces.Exposed(cell, air);
        for (int face = 0; face < FaceNames.Length; face++)
        {
            if ((faces & (1 << face)) != 0)
            {
                exposed.Add(FaceNames[face]);
            }
        }

        return exposed;
    }
}

/// <summary>A listed frame, for sorting and then for its view.</summary>
internal sealed class FrameRow
{
    internal FrameRow(Frame frame, List<string> exposed, double? distance)
    {
        Frame = frame;
        Exposed = exposed;
        Distance = distance;
    }

    internal Frame Frame { get; }

    internal List<string> Exposed { get; }

    internal double? Distance { get; }

    /// <summary>Sorted by distance, then reference id; or by reference id.</summary>
    internal ListKey Key => new ListKey(Distance, null, 0, Frame.ReferenceId);

    internal FrameView ToView()
    {
        int colorIndex = GameManager.GetColorIndex(Frame.CustomColor);
        string? colorName = Frame.CustomColor != null ? Frame.CustomColor.DisplayName : null;
        ColorView color = new ColorView(colorIndex >= 0 ? colorIndex : (int?)null, colorName);
        double? distance = Distance.HasValue ? System.Math.Round(Distance.Value, PositionView.Decimals) : (double?)null;
        return new FrameView(GameLookup.ViewOf(Frame), GameLookup.ViewOf(Frame.Position), distance, Exposed,
            !Frame.CanAirPass, Frame.CurrentBuildStateIndex, color);
    }
}

/// <summary>The game's per-face air rules between a cell and its neighbour.</summary>
internal sealed class CellAir
{
    private const float DensitySolid = 0.49803922f;
    private const double CloseToGlobalKpa = 1.0;

    private readonly GridController _grid;
    private readonly RoomController _rooms;

    internal CellAir(GridController grid, RoomController rooms)
    {
        _grid = grid;
        _rooms = rooms;
    }

    /// <summary>The six faces' steps, in FaceNames' order (+x, -x, +y, -y, +z, -z): bit n of a face mask is face n.</summary>
    internal static readonly Grid3[] Steps = { Grid3.East, Grid3.West, Grid3.Up, Grid3.Down, Grid3.North, Grid3.South };

    /// <summary>
    /// The cell's faces by rules (1) to (3), which read only structures, rooms and terrain (followed: FrameFaces):
    /// open, the faces that hold the world's air; worldAirOnly, those that do when the neighbour's air passes rule (4),
    /// read live (NeighbourAirIsWorld).
    /// </summary>
    internal void Measure(Grid3 cell, out int open, out int worldAirOnly)
    {
        open = 0;
        worldAirOnly = 0;
        for (int face = 0; face < Steps.Length; face++)
        {
            Grid3 neighbour = cell + Steps[face];
            if (_rooms.GetRoom(neighbour) != null)
            {
                continue;
            }

            Cell other = _grid.GetCell(neighbour);
            if ((other != null && !other.IsOpenAir()) || !AtmosphereHelper.CanContainAtmos(neighbour) ||
                !FaceIsOpen(cell, neighbour, other))
            {
                continue;
            }

            if (BlocksGravityNotAir(other))
            {
                worldAirOnly |= 1 << face;
            }
            else
            {
                open |= 1 << face;
            }
        }
    }

    /// <summary>Rule (4) for the faces in worldAirOnly: those whose neighbour's air is the world's now.</summary>
    internal static int WithWorldAir(Grid3 cell, int worldAirOnly)
    {
        int exposed = 0;
        for (int face = 0; face < Steps.Length; face++)
        {
            if ((worldAirOnly & (1 << face)) != 0 && NeighbourAirIsWorld(cell + Steps[face]))
            {
                exposed |= 1 << face;
            }
        }

        return exposed;
    }

    // No terrain across the face (both sides) and no air-blocking face structure registered on either cell.
    private bool FaceIsOpen(Grid3 cell, Grid3 neighbour, Cell? other)
    {
        Vector3 direction = (neighbour - cell).ToVector3().normalized;
        if (!VoxelFaceOpen(cell.ToVector3(), direction) || !VoxelFaceOpen(neighbour.ToVector3(), -direction))
        {
            return false;
        }

        Grid3 face = cell.Middle(neighbour);
        Cell own = _grid.GetCell(cell);
        return (own == null || own.IsOpenAir(face)) && (other == null || other.IsOpenAir(face));
    }

    // Rule 4 applies when the neighbour's centre structure blocks gravity but not air (a one-sheet frame): it keeps the
    // cell out of every room, so its air must pass Structure.IsExposedToGlobal's test instead.
    private static bool BlocksGravityNotAir(Cell? other)
    {
        Structure? centre = other != null ? other.Lookup[StructureElement.Center] : null;
        return centre != null && !centre.CanGravityPass;
    }

    private static bool NeighbourAirIsWorld(Grid3 neighbour)
    {
        Atmosphere? atmosphere = AtmosphericsController.World != null
            ? AtmosphericsController.World.GetAtmosphereLocal(new WorldGrid(neighbour))
            : null;
        return atmosphere == null || atmosphere.IsGlobalAtmosphere ||
               atmosphere.IsCloseToGlobal(new PressurekPa(CloseToGlobalKpa));
    }

    // GridController.IsVoxelFaceOpen, which takes a Span the mod cannot reference: a cell's face is open when any of
    // its four corner voxels (GridController.GetFaceVoxels: the cell centre plus RocketGrid.GridVoxel, 0.5 m on each
    // axis) has terrain density below the solid threshold.
    private static bool VoxelFaceOpen(Vector3 centre, Vector3 direction)
    {
        Vector3 across1 = Mathf.Abs(direction.x) > 0.5f ? Vector3.up : Vector3.right;
        Vector3 across2 = Vector3.Cross(direction, across1).normalized;
        Vector3 faceCentre = centre + direction * 0.5f;
        for (int a = -1; a <= 1; a += 2)
        {
            for (int b = -1; b <= 1; b += 2)
            {
                Vector3 corner = faceCentre + across1 * (0.5f * a) + across2 * (0.5f * b);
                Vector3 voxel = new Vector3(Mathf.Floor(corner.x), Mathf.Floor(corner.y), Mathf.Floor(corner.z));
                if (TerrainSystem.VoxelTerrain.GetDensityAtSize(voxel, 1) < DensitySolid)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
