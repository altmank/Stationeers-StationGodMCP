#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Assets.Scripts.Util;
using Objects.Structures;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using UnityEngine;

namespace StationGodMCP.Api;

/// <summary>
/// outer_frames: which frames (iron, steel, corner, side) have a face on a cell whose gas is the world's. Read only.
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
internal static class OuterFramesApi
{
    private const int DefaultLimit = ReplyDefaults.OuterFrames;
    private const int MaximumLimit = 1000;

    internal static OuterFramesView Handle(Args args)
    {
        double? near = args.OptionalPositiveDouble("near_player_m");
        PlayerOrigin origin = PlayerOrigin.Current().RequireIf(near.HasValue);
        PageRequest page = PageRequest.From(args, DefaultLimit, MaximumLimit);
        bool includeInner = args.OptionalBool("include_inner") ?? false;
        GridController grid = GridController.World;
        RoomController rooms = RoomController.World;
        if (grid == null || rooms == null)
        {
            throw ApiErrors.Refused("not_ready", "The world grid or the room controller is not loaded.");
        }

        FrameCensus census = FrameCensus.Take(new CellAir(grid, rooms), origin, near, includeInner);
        census.Rows.Sort(static (a, b) => FrameRow.NearestFirst(a, b));
        Slice<FrameRow> rows = Slice<FrameRow>.Of(census.Rows, page);
        List<FrameView> views = new List<FrameView>(rows.Items.Count);
        foreach (FrameRow row in rows.Items)
        {
            views.Add(row.ToView());
        }

        page.Note("frames", views.Count, rows.Total);
        return new OuterFramesView(Slice<FrameView>.Page(views, page, rows.Total), census.TotalFrames,
            census.TotalOuter, includeInner, origin.View);
    }
}

/// <summary>Every frame in the world with its exposed faces, and the counts.</summary>
internal sealed class FrameCensus
{
    private static readonly string[] FaceNames = { "+x", "-x", "+y", "-y", "+z", "-z" };

    private FrameCensus()
    {
    }

    internal List<FrameRow> Rows { get; } = new List<FrameRow>();

    internal int TotalFrames { get; private set; }

    internal int TotalOuter { get; private set; }

    // GridController.AllStructuresPool holds every registered structure; frames are the Frame ones.
    internal static FrameCensus Take(CellAir air, PlayerOrigin origin, double? near, bool includeInner)
    {
        FrameCensus census = new FrameCensus();
        List<Structure> structures = GridController.AllStructuresPool.ToList();
        for (int index = 0; index < structures.Count; index++)
        {
            if (structures[index] is Frame frame && !frame.IsCursor && !frame.IsBeingDestroyed)
            {
                census.Add(frame, air, origin, near, includeInner);
            }
        }

        return census;
    }

    private void Add(Frame frame, CellAir air, PlayerOrigin origin, double? near, bool includeInner)
    {
        double? distance = origin.ExactDistanceTo(frame.Position);
        if (near.HasValue && !(distance <= near.Value))
        {
            return;
        }

        TotalFrames++;
        List<string> exposed = ExposedFaces(frame, air);
        TotalOuter += exposed.Count > 0 ? 1 : 0;
        if (exposed.Count > 0 || includeInner)
        {
            Rows.Add(new FrameRow(frame, exposed, distance));
        }
    }

    private static List<string> ExposedFaces(Frame frame, CellAir air)
    {
        List<string> exposed = new List<string>(FaceNames.Length);
        if (frame.WorldGrid == WorldGrid.INVALID)
        {
            return exposed;
        }

        Grid3 cell = frame.WorldGrid.Value;
        Grid3[] steps = { Grid3.East, Grid3.West, Grid3.Up, Grid3.Down, Grid3.North, Grid3.South };
        for (int face = 0; face < steps.Length; face++)
        {
            if (air.FaceHoldsWorldAir(cell, steps[face]))
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

    // Nearest first (no player: all at 0), then by reference id.
    internal static int NearestFirst(FrameRow a, FrameRow b)
    {
        int byDistance = (a.Distance ?? 0.0).CompareTo(b.Distance ?? 0.0);
        return byDistance != 0 ? byDistance : a.Frame.ReferenceId.CompareTo(b.Frame.ReferenceId);
    }

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

    internal bool FaceHoldsWorldAir(Grid3 cell, Grid3 step)
    {
        Grid3 neighbour = cell + step;
        if (_rooms.GetRoom(neighbour) != null)
        {
            return false;
        }

        Cell other = _grid.GetCell(neighbour);
        if ((other != null && !other.IsOpenAir()) || !AtmosphereHelper.CanContainAtmos(neighbour))
        {
            return false;
        }

        return FaceIsOpen(cell, neighbour, other) && NeighbourAirIsWorld(neighbour, other);
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

    // Rule 4: a centre structure that blocks gravity but not air (a one-sheet frame) keeps the cell out of every room,
    // so its air must pass Structure.IsExposedToGlobal's test instead.
    private static bool NeighbourAirIsWorld(Grid3 neighbour, Cell? other)
    {
        Structure? centre = other != null ? other.Lookup[StructureElement.Center] : null;
        if (centre == null || centre.CanGravityPass)
        {
            return true;
        }

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
