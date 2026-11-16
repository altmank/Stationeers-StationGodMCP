#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// The world as the stand search reads it: the structures near the target as boxes (Bodies.RenderBox), the floor under
/// a feet point by a ray down, and the line from the eye to the target by a ray. Main thread.
/// </summary>
internal static class StandWorld
{
    private const float Margin = 2.5f;

    private const string Terrain = "terrain";

    /// <summary>The stand for a player now at from, round the target's body (the target thing, when it is one).</summary>
    internal static StandResult Search(Thing? target, Box3 body, Vec3 from, StandOptions options)
    {
        float reach = (float)options.ReachM + Margin;
        Vector3 centre = Bodies.U(body.Centre);
        Vector3 half = Bodies.U(body.Size) * 0.5f + new Vector3(reach, reach, reach);
        List<StandObstacle> obstacles = new List<StandObstacle>();
        HashSet<long> seen = new HashSet<long>();
        foreach (Collider collider in Physics.OverlapBox(centre, half, Quaternion.identity, Physics.DefaultRaycastLayers,
                     QueryTriggerInteraction.Ignore))
        {
            Thing? thing = Thing.Find(collider);
            if (thing is not Structure || !seen.Add(thing.ReferenceId) || (options.On && thing == target))
            {
                continue;
            }

            obstacles.Add(new StandObstacle(thing.ReferenceId.ToString(), Bodies.RenderBox(thing)));
        }

        if (target != null && !options.On && seen.Add(target.ReferenceId))
        {
            obstacles.Add(new StandObstacle(target.ReferenceId.ToString(), body));
        }

        return StandSearch.Find(body, from, options, obstacles, FloorBelow, (eye, point) => LineClear(eye, point, target));
    }

    private static StandFloor? FloorBelow(Vec3 feet)
    {
        Vector3 top = Bodies.U(feet) + Vector3.up * 0.1f;
        RaycastHit[] hits = Physics.RaycastAll(top, Vector3.down, (float)StandSearch.FloorDropM + 0.1f,
            Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        float nearest = float.MaxValue;
        StandFloor? floor = null;
        foreach (RaycastHit hit in hits)
        {
            Thing? thing = Thing.Find(hit.collider);
            if (thing is Human or Item || hit.distance >= nearest)
            {
                continue;
            }

            nearest = hit.distance;
            floor = new StandFloor(thing != null ? thing.ReferenceId.ToString() : Terrain);
        }

        return floor;
    }

    private static bool LineClear(Vec3 eye, Vec3 point, Thing? target)
    {
        Vector3 from = Bodies.U(eye);
        Vector3 to = Bodies.U(point);
        Vector3 along = to - from;
        float length = along.magnitude;
        if (length < 0.05f)
        {
            return true;
        }

        RaycastHit[] hits = Physics.RaycastAll(from, along / length, length - 0.02f, Physics.DefaultRaycastLayers,
            QueryTriggerInteraction.Ignore);
        foreach (RaycastHit hit in hits)
        {
            Thing? thing = Thing.Find(hit.collider);
            if (thing is Human or Item || (target != null && thing == target))
            {
                continue;
            }

            return false;
        }

        return true;
    }
}
