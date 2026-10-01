#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Lint;

namespace StationGodMCP.Api.Shared.Game.Lint;

/// <summary>
/// A cell of the lint model: a 0.5 m small cell, or a 2 m cell (large). Support, doors and windows come from the
/// grid as the route planners read it (GridFacts); a large cell's support is inside_frame with a frame, else air.
/// </summary>
internal sealed class CellSubject : LintSubject
{
    private readonly GameLintWorld _world;

    internal CellSubject(GameLintWorld world, GridCell cell, bool large)
        : base(LintModel.Cell)
    {
        _world = world;
        Cell = cell;
        IsLarge = large;
        Key = KeyOf(cell, large);
    }

    internal GridCell Cell { get; }

    internal bool IsLarge { get; }

    internal GridCell Large => IsLarge ? Cell : SmallCellCode.LargeOf(Cell);

    public override string Key { get; }

    public override Vec3? Position => Bodies.V(PieceShapes.CentreOf(Cell));

    public override string Describe => $"{(IsLarge ? "2 m cell" : "cell")} {Cell}";

    internal static string KeyOf(GridCell cell, bool large) => (large ? "large:" : "cell:") + cell;

    protected override LintValue Compute(string field)
    {
        GridFacts facts = _world.Facts;
        switch (field)
        {
            case "position":
                return LintValue.Of(Bodies.V(PieceShapes.CentreOf(Cell)));
            case "size":
                return LintValue.Of(IsLarge ? 2.0 : 0.5);
            case "large":
                return LintValue.Of(IsLarge ? this : _world.LargeCell(Large));
            case "support":
                return LintValue.Of(Support(facts));
            case "in_door_keepout":
                return LintValue.Of(!IsLarge && facts.Opening(Cell).IsDoor);
            case "keepout_door":
                return !IsLarge && facts.Opening(Cell) is OpeningZone door && door.IsDoor
                    ? LintValue.Of(_world.ThingById(door.Id))
                    : LintValue.Null;
            case "on_window_face":
                return LintValue.Of(!IsLarge && facts.Opening(Cell).IsWindow);
            case "window":
                return !IsLarge && facts.Opening(Cell) is OpeningZone window && window.IsWindow
                    ? LintValue.Of(_world.ThingById(window.Id))
                    : LintValue.Null;
            case "door_jamb":
                return LintValue.Of(IsLarge ? null : _world.Thing(_world.JambDoorAt(Cell)));
            case "room":
                return LintValue.Of(_world.Room(facts.RoomAt(Large)));
            case "outdoors":
                return LintValue.Of(facts.RoomAt(Large) == null);
            case "pieces":
                return Things(true);
            case "devices":
                return Things(false);
            case "frame":
                return LintValue.Of(_world.Thing(facts.FrameAt(Large)));
            case "blocker":
                return LintValue.Of(_world.Thing(Blocker()));
            case "walls":
                List<ILintObject> walls = new List<ILintObject>();
                foreach (GridStep face in GridStep.All)
                {
                    foreach (Structure structure in facts.FaceStructures(Large, face))
                    {
                        if (_world.Thing(structure) is ThingSubject wall)
                        {
                            walls.Add(wall);
                        }
                    }
                }

                return LintValue.Objects(walls);
            default:
                throw NoField(field);
        }
    }

    private string Support(GridFacts facts)
    {
        if (IsLarge)
        {
            return facts.FrameAt(Cell) != null ? "inside_frame" : "air";
        }

        if (facts.Visibility(Cell) == CellVisibility.Inside)
        {
            return "inside_frame";
        }

        return facts.Support(Cell) switch
        {
            CellSupport.Frame => "frame_face",
            CellSupport.FrameEdge => "frame_face",
            CellSupport.Wall => "wall_plane",
            _ => "air"
        };
    }

    // The structure centred in the 2 m cell whose collision fills it (CollisionType.BlockGrid): what stops a drill.
    private Structure? Blocker()
    {
        Cell? cell = GridController.World.GetCell(PieceShapes.Grid(Large));
        return cell?.Lookup[StructureElement.Center] is Structure centre && centre != null && !centre.IsBeingDestroyed &&
               centre.StructureCollisionType == CollisionType.BlockGrid
            ? centre
            : null;
    }

    private LintValue Things(bool pieces)
    {
        List<ILintObject> things = new List<ILintObject>();
        if (IsLarge)
        {
            for (int index = 0; index < SmallCellCode.PerCell; index++)
            {
                AddFrom(_world.Facts.SmallAt(SmallCellCode.SmallAt(Cell, index)), pieces, things);
            }
        }
        else
        {
            AddFrom(_world.Facts.SmallAt(Cell), pieces, things);
        }

        return LintValue.Objects(things);
    }

    private void AddFrom(SmallCell? cell, bool pieces, List<ILintObject> things)
    {
        if (cell == null)
        {
            return;
        }

        foreach (SmallGrid? slot in pieces
                     ? new SmallGrid?[] { cell.Cable, cell.Pipe, cell.Chute }
                     : new SmallGrid?[] { cell.Device, cell.Other })
        {
            if (_world.Thing(slot) is ThingSubject thing && !things.Contains(thing))
            {
                things.Add(thing);
            }
        }
    }
}

/// <summary>
/// The band beside a door's jambs: on the door's plane band (the keep-out's reach either side of its plane), within
/// its height, in the column of small cells just past its side edges, not hidden inside a frame. Doors in floors and
/// ceilings have no jambs. Worked out once per door and lint call.
/// </summary>
internal sealed class DoorJamb
{
    private readonly int _axis;
    private readonly int _side;
    private readonly int _plane;
    private readonly int _sideMin;
    private readonly int _sideMax;
    private readonly int _yMin;
    private readonly int _yMax;
    private readonly int _reach;

    private DoorJamb(Structure door, int axis, int side, int plane, int sideMin, int sideMax, int yMin, int yMax,
        int reach)
    {
        Door = door;
        _axis = axis;
        _side = side;
        _plane = plane;
        _sideMin = sideMin;
        _sideMax = sideMax;
        _yMin = yMin;
        _yMax = yMax;
        _reach = reach;
    }

    internal Structure Door { get; }

    internal static DoorJamb? Of(Structure door, GridFacts facts)
    {
        List<GridCell> faces = Openings.FacesOf(door);
        if (faces.Count == 0)
        {
            return null;
        }

        int axis = FacePoints.AxisOf(faces[0]);
        if (axis == 1)
        {
            return null;
        }

        int side = axis == 0 ? 2 : 0;
        int sideMin = int.MaxValue, sideMax = int.MinValue, yMin = int.MaxValue, yMax = int.MinValue;
        foreach (GridCell face in faces)
        {
            sideMin = System.Math.Min(sideMin, FacePlane.Component(face, side) - 10);
            sideMax = System.Math.Max(sideMax, FacePlane.Component(face, side) + 10);
            yMin = System.Math.Min(yMin, face.Y - 10);
            yMax = System.Math.Max(yMax, face.Y + 10);
        }

        return new DoorJamb(door, axis, side, FacePlane.Component(faces[0], axis), sideMin, sideMax, yMin, yMax,
            facts.Band.Cells * GridStep.CellSize);
    }

    internal bool Holds(GridCell cell, GridFacts facts)
    {
        int across = FacePlane.Component(cell, _side);
        return System.Math.Abs(FacePlane.Component(cell, _axis) - _plane) <= _reach &&
               cell.Y >= _yMin && cell.Y <= _yMax &&
               (across == _sideMin - GridStep.CellSize || across == _sideMax + GridStep.CellSize) &&
               facts.Visibility(cell) != CellVisibility.Inside;
    }
}

/// <summary>The jamb bands of a set of doors (those in floors and ceilings have none).</summary>
internal static class DoorJambs
{
    internal static List<DoorJamb> Of(IReadOnlyList<Structure> doors, GridFacts facts)
    {
        List<DoorJamb> jambs = new List<DoorJamb>(doors.Count);
        foreach (Structure door in doors)
        {
            if (DoorJamb.Of(door, facts) is DoorJamb jamb)
            {
                jambs.Add(jamb);
            }
        }

        return jambs;
    }
}
