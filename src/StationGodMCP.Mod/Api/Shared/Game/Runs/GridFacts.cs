#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Objects.Structures;
using StationGodMCP.Api.Shared.Game.Structures;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game.Runs;

/// <summary>
/// The grid as the route search and grid_survey read it. A 2 m cell (GridController.GetCell at its centre): the
/// structure in its Center slot when that is a frame (Frame), its room (RoomController.GetRoom; none is outside),
/// and on each of its six faces the face structures registered there (GridController.GetFaceStructures at the face
/// point, the midpoint between the two centres). A small cell (GridController.GetSmallCell): its slots, and for one
/// kind, whether a piece of the kind may stand there (PlacementCheck.CellBlocked), along which axes another kind's
/// piece lies (PlacementCheck.BlockedAxes), the kind's networks next to it, and what supports it (CellSupports,
/// from the 2 m cells it touches). Both are cached for one request.
/// </summary>
internal sealed class GridFacts
{
    private readonly GridController _grid;
    private readonly RoomController? _rooms;
    private readonly RunKind _kind;
    private readonly SmallGridBlock _mask;
    private readonly HashSet<long> _ignore;
    private readonly Dictionary<GridCell, LargeCellFacts> _large = new Dictionary<GridCell, LargeCellFacts>();
    private readonly Dictionary<GridCell, SmallCellFacts> _small = new Dictionary<GridCell, SmallCellFacts>();
    private readonly Dictionary<GridCell, IReadOnlyList<FaceOpening>> _faces =
        new Dictionary<GridCell, IReadOnlyList<FaceOpening>>();
    private readonly Dictionary<GridCell, OpeningZone> _zones = new Dictionary<GridCell, OpeningZone>();
    private readonly Dictionary<long, HashSet<GridCell>> _doorPorts = new Dictionary<long, HashSet<GridCell>>();
    private readonly DoorBand _band = LayoutSettings.DoorBand;

    internal GridFacts(RunKind kind, SmallGridBlock mask, HashSet<long> ignore)
    {
        _grid = GridController.World;
        _rooms = RoomController.World;
        _kind = kind;
        _mask = mask;
        _ignore = ignore;
    }

    internal static Grid3 Grid(GridCell cell) => PieceShapes.Grid(cell);

    internal Frame? FrameAt(GridCell large)
    {
        Cell? cell = _grid.GetCell(Grid(large));
        return cell?.Lookup[StructureElement.Center] as Frame;
    }

    internal Room? RoomAt(GridCell large) => _rooms?.GetRoom(Grid(large));

    /// <summary>The face structures on one face of a large cell (walls, windows), each once.</summary>
    internal List<Structure> FaceStructures(GridCell large, GridStep face)
    {
        GridCell point = new GridCell(large.X + face.Dx * SmallCellCode.Large / 2,
            large.Y + face.Dy * SmallCellCode.Large / 2, large.Z + face.Dz * SmallCellCode.Large / 2);
        List<Structure> structures = new List<Structure>();
        foreach (Structure structure in new List<Structure>(_grid.GetFaceStructures(Grid(point))))
        {
            if (structure != null && !structure.IsBeingDestroyed && !structures.Contains(structure))
            {
                structures.Add(structure);
            }
        }

        return structures;
    }

    /// <summary>The face structures registered at a face point, each once.</summary>
    internal List<Structure> FaceStructuresAt(GridCell point)
    {
        List<Structure> structures = new List<Structure>();
        foreach (Structure structure in new List<Structure>(_grid.GetFaceStructures(Grid(point))))
        {
            if (structure != null && !structure.IsBeingDestroyed && !structures.Contains(structure))
            {
                structures.Add(structure);
            }
        }

        return structures;
    }

    internal LargeCellFacts Large(GridCell large)
    {
        if (_large.TryGetValue(large, out LargeCellFacts facts))
        {
            return facts;
        }

        // A door's face holds a doorway, not a wall: it supports nothing, and routes keep out of it (Opening).
        int walls = 0;
        foreach (GridStep face in GridStep.All)
        {
            if (FaceStructures(large, face).Exists(structure => !Openings.IsDoor(structure)))
            {
                walls |= 1 << face.Index;
            }
        }

        facts = new LargeCellFacts(FrameAt(large) != null, RoomAt(large) != null, walls);
        _large[large] = facts;
        return facts;
    }

    internal SmallCell? SmallAt(GridCell cell) => _grid.GetSmallCell(Grid(cell));

    internal SmallOccupancy Occupancy(GridCell cell)
    {
        SmallCell? small = SmallAt(cell);
        if (small == null)
        {
            return new SmallOccupancy(false, false, false, false, false, false);
        }

        return new SmallOccupancy(Live(small.Cable), Live(small.Pipe), Live(small.Chute), Live(small.Device),
            Live(small.Other) || Live(small.Rail as SmallGrid), small.Owner != null);
    }

    private static bool Live(SmallGrid? thing) => thing != null && !thing.IsBeingDestroyed;

    internal SmallCellFacts Small(GridCell cell)
    {
        if (_small.TryGetValue(cell, out SmallCellFacts facts))
        {
            return facts;
        }

        SmallCell? small = SmallAt(cell);
        SmallGrid? piece = small != null ? _kind.SlotOf(small) : null;
        bool own = piece != null && !piece.IsBeingDestroyed && !_ignore.Contains(piece.ReferenceId);
        facts = new SmallCellFacts(PlacementCheck.CellBlocked(small, _mask, _kind, _ignore),
            PlacementCheck.BlockedAxes(small, _mask, _kind, _ignore),
            own ? _kind.Family.NetworkOf(piece!)?.ReferenceId : null, own, Large(SmallCellCode.LargeOf(cell)),
            SmallCellCode.IndexOnAxis(cell.X), SmallCellCode.IndexOnAxis(cell.Y), SmallCellCode.IndexOnAxis(cell.Z),
            NeighbourNetworks(cell), Support(cell), Visibility(cell));
        _small[cell] = facts;
        return facts;
    }

    /// <summary>The door keep-out band this request uses ([Layout] DoorKeepOutBand).</summary>
    internal DoorBand Band => _band;

    /// <summary>
    /// Where the small cell stands against doors and windows (OpeningZones): in a door's keep-out (not when hidden
    /// inside a frame's body, nor in a cell a piece joining one of that door's ports stands in), on a window, or clear.
    /// </summary>
    internal OpeningZone Opening(GridCell small)
    {
        if (_zones.TryGetValue(small, out OpeningZone zone))
        {
            return zone;
        }

        zone = OpeningZones.At(small, _band, FaceOpenings, Visibility(small) == CellVisibility.Inside);
        if (zone.IsDoor && DoorPorts(zone.Id).Contains(small))
        {
            zone = OpeningZone.Clear;
        }

        _zones[small] = zone;
        return zone;
    }

    /// <summary>The doors, windows and walls registered at a face point, each once.</summary>
    internal IReadOnlyList<FaceOpening> FaceOpenings(GridCell point)
    {
        if (_faces.TryGetValue(point, out IReadOnlyList<FaceOpening> openings))
        {
            return openings;
        }

        List<FaceOpening> list = new List<FaceOpening>(1);
        foreach (Structure structure in new List<Structure>(_grid.GetFaceStructures(Grid(point))))
        {
            if (structure != null && !structure.IsBeingDestroyed)
            {
                list.Add(new FaceOpening(Openings.KindOf(structure), structure.ReferenceId));
            }
        }

        _faces[point] = list;
        return list;
    }

    private HashSet<GridCell> DoorPorts(long door)
    {
        if (!_doorPorts.TryGetValue(door, out HashSet<GridCell> cells))
        {
            cells = GameLookup.TryFindThing(new ThingId(door), out Thing thing) && thing is Structure structure
                ? Openings.PortCellsOf(structure)
                : new HashSet<GridCell>();
            _doorPorts[door] = cells;
        }

        return cells;
    }

    /// <summary>What holds a piece in the small cell up (CellSupports): read from the 2 m cells it touches only.</summary>
    internal CellSupport Support(GridCell small) => CellSupports.Of(small, Large);

    /// <summary>How visible a piece in the small cell is (CellSupports.VisibilityOf).</summary>
    internal CellVisibility Visibility(GridCell small) => CellSupports.VisibilityOf(small, Large);

    /// <summary>Whether a 2 m cell can support small cells at all: a frame in it or a face structure on it.</summary>
    internal bool Anchors(GridCell large) => CellSupports.Anchors(Large(large));

    private List<long> NeighbourNetworks(GridCell cell)
    {
        List<long> networks = new List<long>(2);
        foreach (GridStep step in GridStep.All)
        {
            SmallCell? small = SmallAt(step.From(cell));
            SmallGrid? piece = small != null ? _kind.SlotOf(small) : null;
            if (piece == null || piece.IsBeingDestroyed || _ignore.Contains(piece.ReferenceId))
            {
                continue;
            }

            long? network = _kind.Family.NetworkOf(piece)?.ReferenceId;
            if (network.HasValue && !networks.Contains(network.Value))
            {
                networks.Add(network.Value);
            }
        }

        return networks;
    }
}
