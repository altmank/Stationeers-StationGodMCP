#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Pipes;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game.Build;

/// <summary>A face plane seen from one side, with the axes a viewer there reads as right and up.</summary>
internal sealed class PlaneView
{
    internal PlaneView(FacePlane plane, GridStep side)
    {
        Plane = plane;
        Side = side;
        (Right, Up) = RelativeMath.FaceAxes(side, GridStep.All[4], GridStep.All[0]);
    }

    internal FacePlane Plane { get; }

    /// <summary>The viewer's side: the plane's outward normal toward the viewer.</summary>
    internal GridStep Side { get; }

    internal GridStep Right { get; }

    internal GridStep Up { get; }

    /// <summary>The small cell on the plane at right-axis and up-axis coordinates (metres).</summary>
    internal GridCell CellAt(double u, double v)
    {
        int[] point = new int[3];
        point[Plane.Axis] = Plane.Coordinate;
        point[Right.Axis] = (int)System.Math.Round(u * 10.0);
        point[Up.Axis] = (int)System.Math.Round(v * 10.0);
        return new GridCell(point[0], point[1], point[2]);
    }

    /// <summary>
    /// The face point of the 2 m face holding the small cell at (u, v): the face of the 2 m cell the small cell belongs
    /// to, whichever way the viewer looks (PlaneCells.FaceCentre: a coordinate on a 2 m seam belongs to the section on
    /// its plus side, as the grid gives a 2 m cell its first small cell on its minimum plane).
    /// </summary>
    internal GridCell FaceAt(double u, double v)
    {
        int[] point = new int[3];
        point[Plane.Axis] = Plane.Coordinate;
        point[Right.Axis] = PlaneCells.FaceCentre(u);
        point[Up.Axis] = PlaneCells.FaceCentre(v);
        return new GridCell(point[0], point[1], point[2]);
    }

    /// <summary>Whether the 2 m cell just in front of the plane at (u, v), on the viewer's side, is in the room.</summary>
    internal bool RoomOnSide(double u, double v, Room room, GridFacts facts) =>
        facts.RoomAt(LargeAt(PointAt(u, v) + Vec3.Of(Side) * 1.01)) == room;

    /// <summary>A point's coordinates along the viewer's right and up axes.</summary>
    internal (double U, double V) Project(Vec3 point) => (point[Right.Axis], point[Up.Axis]);

    internal Vec3 PointAt(double u, double v) => PlaneCells.PointAt(Plane, Right.Axis, Up.Axis, u, v);

    /// <summary>The box the map character at (u, v) covers: the small cell on the plane and the one in front of it.</summary>
    internal Box3 CellBox(double u, double v) => PlaneCells.CellBox(Plane, Side, Right.Axis, Up.Axis, u, v);

    /// <summary>+1 when the viewer's right runs along its axis' positive direction, else -1. Up is always positive: +y
    /// on a wall, +z on a floor or ceiling.</summary>
    internal int RightSign => Right.Dx + Right.Dy + Right.Dz;

    /// <summary>
    /// The plane as asked: plane "z=668" with side (+z or -z; default the side whose 2 m cell is in a room), or the face
    /// the look ray hits (looking: true). Also the point the map centres on when the call gives none.
    /// </summary>
    internal static PlaneView Read(Args args, GridFacts facts, out Vec3? around)
    {
        around = null;
        if (args.OptionalBool("looking") ?? false)
        {
            CursorManager cursor = CursorManager.Instance;
            if (cursor == null || !Look.Cast(cursor, 20.0, out RaycastHit hit))
            {
                throw ApiErrors.Refused("no_crosshair_hit", "The look ray hits nothing within 20 m.");
            }

            GridStep? outward = ViewBasis.Along(Bodies.V(hit.normal), 10.0);
            Vec3 point = Bodies.V(hit.point);
            if (!outward.HasValue)
            {
                throw ApiErrors.Refused("no_face", $"The look ray hits {point}, not a surface along an axis.");
            }

            double along = point[outward.Value.Axis];
            around = point;
            return new PlaneView(
                FacePlane.Of(outward.Value.Axis, (int)System.Math.Round(System.Math.Round(along / 2.0) * 20.0)),
                outward.Value);
        }

        string text = args.OptionalString("plane") ??
                      throw ApiErrors.InvalidArgument("Pass plane (e.g. \"z=668\", an even metre) or looking: true.");
        FacePlane plane = ParsePlane(text);
        string? sideText = args.OptionalString("side");
        GridStep side;
        if (sideText != null)
        {
            if (!GridStep.TryParse(sideText, out side) || side.Axis != plane.Axis)
            {
                throw ApiErrors.InvalidArgument($"side must be +{"xyz"[plane.Axis]} or -{"xyz"[plane.Axis]}.");
            }
        }
        else
        {
            side = RoomSide(plane, args, facts);
        }

        return new PlaneView(plane, side);
    }

    internal static FacePlane ParsePlane(string text)
    {
        string[] parts = text.Trim().ToLowerInvariant().Split('=');
        int axis = parts.Length == 2 ? "xyz".IndexOf(parts[0].Trim(), System.StringComparison.Ordinal) : -1;
        if (axis < 0 || parts[0].Trim().Length != 1 ||
            !double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double metres) ||
            System.Math.Abs(metres / 2.0 - System.Math.Round(metres / 2.0)) > 1e-6)
        {
            throw ApiErrors.InvalidArgument($"plane must be like \"z=668\": an axis and an even metre, not {text}.");
        }

        return FacePlane.Of(axis, (int)System.Math.Round(metres * 10.0));
    }

    // The side whose neighbouring 2 m cell (at around, or anywhere along the plane near it) is in a room.
    private static GridStep RoomSide(FacePlane plane, Args args, GridFacts facts)
    {
        GridStep plus = GridStep.All[plane.Axis * 2];
        JToken? token = args.Optional("around");
        Vec3 centre = token != null ? PointOf(token) : Vec3.Zero;
        if (token == null)
        {
            return plus;
        }

        Vec3 onPlane = centre.With(plane.Axis, plane.Metres);
        bool plusRoom = facts.RoomAt(LargeAt(onPlane + Vec3.Of(plus) * 1.0)) != null;
        bool minusRoom = facts.RoomAt(LargeAt(onPlane - Vec3.Of(plus) * 1.0)) != null;
        return plusRoom || !minusRoom ? plus : plus.Opposite;
    }

    internal static Vec3 PointOf(JToken token)
    {
        Metres point = BuildArgs.PositionOf(token, "around");
        return new Vec3(point.X, point.Y, point.Z);
    }

    internal static GridCell LargeAt(Vec3 point) =>
        SmallCellCode.LargeOf(new GridCell((int)System.Math.Round(point.X * 10.0),
            (int)System.Math.Round(point.Y * 10.0), (int)System.Math.Round(point.Z * 10.0)));

    /// <summary>What a face shows: door, window, wall, else a frame right behind it, else open.</summary>
    internal FaceLook LookOf(GridCell face, GridFacts facts, out Structure? structure)
    {
        FaceLook look = FaceLook.Open;
        structure = null;
        foreach (Structure found in facts.FaceStructuresAt(face))
        {
            FaceLook kind = Openings.KindOf(found) switch
            {
                OpeningKind.Door => FaceLook.Door,
                OpeningKind.Window => FaceLook.Window,
                _ => FaceLook.Wall
            };
            if (Rank(kind) > Rank(look))
            {
                look = kind;
                structure = found;
            }
        }

        if (look != FaceLook.Open)
        {
            return look;
        }

        Vec3 behind = new Vec3(face.X / 10.0, face.Y / 10.0, face.Z / 10.0) - Vec3.Of(Side) * 1.0;
        return facts.FrameAt(LargeAt(behind)) != null ? FaceLook.Frame : FaceLook.Open;
    }

    private static int Rank(FaceLook look) => look switch
    {
        FaceLook.Door => 4,
        FaceLook.Window => 3,
        FaceLook.Wall => 2,
        FaceLook.Frame => 1,
        _ => 0
    };

    /// <summary>What stands in a small cell and the one in front of it: a device's key, h, b, c, p; '\0' for nothing.</summary>
    internal char ThingAt(GridCell cell, GridFacts facts, Dictionary<long, char> keys, List<SmallGrid> things)
    {
        char best = '\0';
        foreach (GridCell look in new[] { cell, Side.From(cell) })
        {
            SmallCell? small = facts.SmallAt(look);
            if (small == null)
            {
                continue;
            }

            SmallGrid? device = Live(small.Device);
            if (device == null)
            {
                device = Live(small.Other);
            }

            if (device == null && !(small.Pipe is Piping))
            {
                // A pipe-network member that is not a pipe piece (a passive vent, an in-line tank) is a thing here.
                device = Live(small.Pipe);
            }

            if (device != null)
            {
                return KeyOf(device, keys, things);
            }

            SmallOccupancy occupancy = facts.Occupancy(look);
            char run = occupancy.Chute ? 'h'
                : occupancy.Cable && occupancy.Pipe ? 'b'
                : occupancy.Cable ? 'c'
                : occupancy.Pipe ? 'p'
                : '\0';
            best = best == '\0' ? run : best;
        }

        return best;
    }

    private static SmallGrid? Live(SmallGrid? thing) => thing != null && !thing.IsBeingDestroyed ? thing : null;

    // Keys for things on a wall map: capitals and digits, without the face letters D, F, G, W.
    private const string ThingKeys = "ABCEHIJKLMNOPQRSTUVYZ123456789";

    internal static char KeyOf(SmallGrid device, Dictionary<long, char> keys, List<SmallGrid> things)
    {
        if (keys.TryGetValue(device.ReferenceId, out char key))
        {
            return key;
        }

        key = things.Count < ThingKeys.Length ? ThingKeys[things.Count] : '#';
        keys[device.ReferenceId] = key;
        things.Add(device);
        return key;
    }

    internal PositionView ViewOf(double u, double v) => GameLookup.ViewOf(Bodies.U(PointAt(u, v)));
}
