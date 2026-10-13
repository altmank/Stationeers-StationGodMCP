#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// The build options that lay a route plan route found, in build's words, so the reply's build_arguments feeds build
/// as it is: {what (the kind), waypoints (the main run's corners, cell centres in metres), grade, join, branches
/// [{waypoints, attach}], extra_ends [{at, toward}], remove_ids (a reroute's old run and the kind's assumed pieces),
/// assume_removed (the other things assumed gone), then the route's own allow_bridge, allow, split_long, materials,
/// root, join_to and join_trunk where they differ from build's defaults}.
/// </summary>
internal static class RouteBuildArguments
{
    internal static JObject Of(RouteOrder order, string grade, RouteTree tree, IReadOnlyList<ThingId> removes,
        IReadOnlyList<ThingId> assumedOthers, NetworkTarget? joinTo, Func<GridCell, Vec3> centre)
    {
        JObject build = new JObject
        {
            ["what"] = order.Kind,
            ["waypoints"] = Points(tree.Main, centre),
            ["grade"] = grade,
            ["join"] = order.JoinWord
        };
        if (tree.Branches.Count > 0)
        {
            JArray branches = new JArray();
            foreach (RunBranch branch in tree.Branches)
            {
                branches.Add(new JObject
                {
                    ["waypoints"] = Points(branch.Cells, centre),
                    ["attach"] = Point(branch.Attach!.Value, centre)
                });
            }

            build["branches"] = branches;
        }

        if (tree.Extra.Count > 0)
        {
            JArray extra = new JArray();
            foreach (ExtraEnd end in tree.Extra)
            {
                extra.Add(new JObject { ["at"] = Point(end.Cell, centre), ["toward"] = end.Step.Name });
            }

            build["extra_ends"] = extra;
        }

        if (removes.Count > 0)
        {
            build["remove_ids"] = Ids(removes);
        }

        if (assumedOthers.Count > 0)
        {
            build["assume_removed"] = Ids(assumedOthers);
        }

        if (order.AllowBridgeGiven != null)
        {
            build["allow_bridge"] = order.AllowBridgeGiven.DeepClone();
        }

        if (order.Allow is { Count: > 0 } allow)
        {
            build["allow"] = new JArray(allow);
        }

        if (!order.SplitLong)
        {
            build["split_long"] = false;
        }

        if (Payers(order.Materials) is { } materials)
        {
            build["materials"] = materials;
        }

        if (order.Root is { } root)
        {
            build["root"] = root.ToString();
        }

        if (joinTo != null)
        {
            build["join_to"] = joinTo.ToJson();
        }

        if (order.JoinTrunk)
        {
            build["join_trunk"] = true;
        }

        return build;
    }

    // The holders materials names, as given; null when the player alone pays, build's default.
    private static JToken? Payers(MaterialsSource materials)
    {
        if (materials.Payers.Count == 1 && materials.Payers[0] is Payer.Player)
        {
            return null;
        }

        JArray named = new JArray();
        foreach (Payer payer in materials.Payers)
        {
            named.Add(payer is Payer.Holder holder ? holder.Id : "player");
        }

        return named.Count == 1 ? named[0] : named;
    }

    private static JArray Ids(IReadOnlyList<ThingId> ids)
    {
        JArray array = new JArray();
        foreach (ThingId id in ids)
        {
            array.Add(id.ToString());
        }

        return array;
    }

    private static JArray Points(IReadOnlyList<GridCell> cells, Func<GridCell, Vec3> centre)
    {
        JArray points = new JArray();
        foreach (GridCell cell in RunPath.Waypoints(cells))
        {
            points.Add(Point(cell, centre));
        }

        return points;
    }

    private static JArray Point(GridCell cell, Func<GridCell, Vec3> centre)
    {
        Vec3 at = centre(cell);
        return new JArray(Round(at.X), Round(at.Y), Round(at.Z));
    }

    private static double Round(double value) => Math.Round(value * 100.0) / 100.0;
}
