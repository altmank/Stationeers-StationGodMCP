#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Build;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// survey wall (catalogue survey.json): a text elevation of one 2 m face plane seen from one side, 0.5 m per character,
/// half_width metres around at (or around the point the look ray hits, with looking), with its sections, its things by
/// key and, with free_rects, where a rectangle fits (WallMaps). Answered in its declared shape. Read only.
/// </summary>
internal static class SurveyWall
{
    internal static VerbOutcome Run(SurveyWallOptions options)
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        Vec3? at = null;
        if (options.At is Newtonsoft.Json.Linq.JArray point)
        {
            at = Point.Of(point);
            if (at == null)
            {
                problems.Add(new OptionProblem("at", "must be [x, y, z]"));
            }
        }
        else if (options.At != null)
        {
            problems.Add(new OptionProblem("at", "must be [x, y, z]"));
        }

        FreeRectRequest? request = FreeRectRequest.Of(options.FreeRects, problems);
        bool looking = options.Looking == true;
        if (!looking && options.Plane == null)
        {
            problems.Add(new OptionProblem("plane", "or looking: true is needed: the face plane to map"));
        }
        else if (!looking && at == null)
        {
            problems.Add(new OptionProblem("at", "is needed with plane: a point on or near it, the map's centre"));
        }

        if (problems.Count > 0)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(SurveyWallOptions.Spec.Label, problems));
        }

        GridFacts facts = AreaSurvey.Facts();
        CameraUse camera = new CameraUse();
        PlaneView plane = PlaneView.Of(looking, options.Plane, options.Side, at, facts, camera, out Vec3? looked);
        WallMapView map = WallMaps.Map(plane, at ?? looked ?? Vec3.Zero, options.HalfWidth ?? 0.0, request, facts,
            camera);
        return new VerbOutcome.Answered(ReplyFields.Of(("plane", map.Plane), ("side", map.Side), ("right", map.Right),
            ("up", map.Up), ("rows", map.Rows), ("top_left", map.TopLeft), ("sections", map.Sections),
            ("things", map.Things), ("free_rects", map.FreeRects), ("legend", map.Legend), ("view", map.View)));
    }
}
