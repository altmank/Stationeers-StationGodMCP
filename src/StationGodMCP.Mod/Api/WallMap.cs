#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Build;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api;

/// <summary>
/// wall_map: a text elevation of one face plane as seen from one side (PlaneView), 0.5 m per character: the face behind
/// each small cell (W wall, G window, D door, F a frame with no plate, '.' open), what stands in it or right in front of
/// it (a device's key, c cable, p pipe, b both, h chute), 'x' in a door's keep-out; a ruler row marks the 2 m seams.
/// A device's key covers every cell its mesh box covers by more than 0.1 m, not only the small cells it is registered in.
/// The sections (2 m faces) with what stands on each, the devices' keys, and with free_rects where a w x h rectangle
/// fits on free wall. Read only.
/// </summary>
internal static class WallMapApi
{
    private const double DefaultRadiusM = 4.0;
    private const double MaximumRadiusM = 16.0;
    private const int MaximumRects = 50;
    private const int MaximumBodyCells = 40000;

    internal const string Legend =
        "Seen from the side named: columns left to right, rows top to bottom, 0.5 m each (a small cell on the " +
        "plane and the one in front of it). W wall, G window, D door, F frame without a plate, . open, x a door's " +
        "keep-out, c cable, p pipe, b cable and pipe, h chute, a capital or digit a device or mounted thing (things " +
        "lists them). The first line marks 2 m seams with |: a cell under | straddles two sections.";

    internal static WallMapView Handle(Args args)
    {
        GridFacts facts = new GridFacts(new CableRunKind(), SmallGridBlock.None, new HashSet<long>());
        CameraUse camera = new CameraUse();
        PlaneView plane = PlaneView.Read(args, facts, camera, out Vec3? looked);
        Vec3 around = args.Has("around") ? PlaneView.PointOf(args.Optional("around")!) : looked ?? Vec3.Zero;
        if (!args.Has("around") && looked == null)
        {
            throw ApiErrors.InvalidArgument("Pass around (a point on or near the plane) with plane.");
        }

        double radius = args.OptionalPositiveDouble("radius_m") ?? DefaultRadiusM;
        if (radius > MaximumRadiusM)
        {
            throw ApiErrors.InvalidArgument($"radius_m is at most {MaximumRadiusM}.");
        }

        WallMap map = Build(plane, around, radius, facts, out Dictionary<long, char> keys, out List<SmallGrid> things);
        List<WallThingView> thingViews = things.ConvertAll(thing => new WallThingView(keys[thing.ReferenceId].ToString(),
            GameLookup.ViewOf(thing), GameLookup.ViewOf(thing.Position)));
        return new WallMapView(plane.Plane.ToString(), plane.Side.Name, plane.Right.Name, plane.Up.Name,
            map.Lines(), PointView.Of(plane.PointAt(map.U(0), map.V(0))), Sections(plane, map, facts), thingViews,
            FreeRects(args, plane, map), Legend, camera.Source);
    }

    /// <summary>The map of the plane within radius of a point (as the viewer sees it), with the things keyed.</summary>
    internal static WallMap Build(PlaneView plane, Vec3 around, double radius, GridFacts facts,
        out Dictionary<long, char> keys, out List<SmallGrid> things)
    {
        (double u, double v) = plane.Project(around);
        double uMin = System.Math.Floor((u - radius) * 2.0) / 2.0;
        double uMax = System.Math.Ceiling((u + radius) * 2.0) / 2.0;
        double vMin = System.Math.Floor((v - radius) * 2.0) / 2.0;
        double vMax = System.Math.Ceiling((v + radius) * 2.0) / 2.0;
        int columns = (int)System.Math.Round((uMax - uMin) * 2.0) + 1;
        int rows = (int)System.Math.Round((vMax - vMin) * 2.0) + 1;
        double uStart = plane.RightSign > 0 ? uMin : uMax;
        double uStep = plane.RightSign > 0 ? 0.5 : -0.5;
        keys = new Dictionary<long, char>();
        things = new List<SmallGrid>();
        WallCell[,] cells = new WallCell[rows, columns];
        for (int row = 0; row < rows; row++)
        {
            double cellV = vMax - row * 0.5;
            for (int column = 0; column < columns; column++)
            {
                double cellU = uStart + column * uStep;
                GridCell cell = plane.CellAt(cellU, cellV);
                FaceLook look = plane.LookOf(plane.FaceAt(cellU, cellV), facts, out _);
                char thing = plane.ThingAt(cell, facts, keys, things);
                bool keepOut = facts.Opening(cell).IsDoor || facts.Opening(plane.Side.From(cell)).IsDoor;
                cells[row, column] = new WallCell(look, thing, keepOut);
            }
        }

        Box3 region = new Box3(plane.PointAt(uMin, vMin) - Vec3.Of(plane.Side),
            plane.PointAt(uMax, vMax) + Vec3.Of(plane.Side));
        foreach (NearBody body in NearBodies.Around(region, facts, new HashSet<long>(), NearKinds.Mounted,
                     MaximumBodyCells))
        {
            Cover(plane, body, cells, uStart, uStep, vMax, keys, things);
        }

        return new WallMap(uStart, uStep, vMax, cells);
    }

    // A body's mesh over map cells: the cells its mesh box covers by more than the clash tolerance show its key, even
    // those outside the small cells the game registers it in (a console's frame overhangs its 1 x 1 m of cells).
    private static void Cover(PlaneView plane, NearBody body, WallCell[,] cells, double uStart, double uStep,
        double vTop, Dictionary<long, char> keys, List<SmallGrid> things)
    {
        for (int row = 0; row < cells.GetLength(0); row++)
        {
            for (int column = 0; column < cells.GetLength(1); column++)
            {
                double u = uStart + column * uStep;
                double v = vTop - row * 0.5;
                if (!cells[row, column].HoldsBody && PlaneCells.Covers(body.Render, plane.CellBox(u, v)))
                {
                    cells[row, column] = cells[row, column].WithBody(PlaneView.KeyOf(body.Thing, keys, things));
                }
            }
        }
    }

    private static List<WallSectionView> Sections(PlaneView plane, WallMap map, GridFacts facts)
    {
        List<WallSectionView> sections = new List<WallSectionView>();
        HashSet<GridCell> seen = new HashSet<GridCell>();
        for (int row = 0; row < map.Rows; row++)
        {
            for (int column = 0; column < map.Columns; column++)
            {
                GridCell face = plane.FaceAt(map.U(column), map.V(row));
                if (!seen.Add(face))
                {
                    continue;
                }

                FaceLook look = plane.LookOf(face, facts, out Structure? structure);
                sections.Add(new WallSectionView(PointView.OfCell(face), look.ToString().ToLowerInvariant(),
                    structure != null ? GameLookup.ViewOf(structure) : null));
            }
        }

        return sections;
    }

    private static List<FreeRectView>? FreeRects(Args args, PlaneView plane, WallMap map)
    {
        JObject? request = args.OptionalObject("free_rects");
        if (request == null)
        {
            return null;
        }

        Args spec = new Args(request);
        double w = spec.OptionalPositiveDouble("w") ?? throw ApiErrors.InvalidArgument("free_rects.w is required (m).");
        double h = spec.OptionalPositiveDouble("h") ?? throw ApiErrors.InvalidArgument("free_rects.h is required (m).");
        int wide = System.Math.Max(1, (int)System.Math.Ceiling(w * 2.0 - 1e-6));
        int high = System.Math.Max(1, (int)System.Math.Ceiling(h * 2.0 - 1e-6));
        List<FreeRectView> rects = new List<FreeRectView>();
        foreach ((int row, int column) in map.FreeRects(wide, high, spec.OptionalBool("one_section") ?? true,
                     spec.OptionalBool("require_wall") ?? true,
                     spec.OptionalInt("limit", 1, MaximumRects) ?? 20))
        {
            double u0 = map.U(column);
            double u1 = map.U(column + wide - 1);
            rects.Add(new FreeRectView(row, column, PointView.Of(plane.PointAt((u0 + u1) / 2.0,
                (map.V(row) + map.V(row + high - 1)) / 2.0)), wide, high));
        }

        return rects;
    }
}
