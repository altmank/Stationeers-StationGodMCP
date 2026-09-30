#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Newtonsoft.Json.Linq;
using Objects.Pipes;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game.Build;

/// <summary>
/// place_structure's orient, read against the world: {mount: wall|floor|ceiling|an axis (the side the surface is on),
/// upright (default true), controls_toward: a target, ports: [{role, index, type, toward}], flow: {from, to}}. A target
/// is an axis (+x .. -z), "room" (into the room), "player" (the local player), a point [x, y, z] or {reference_id}
/// (that thing's position).
/// </summary>
internal static class Orienter
{
    private const int MaximumPorts = 8;

    internal static OrientIntent Read(JObject orient)
    {
        Args args = new Args(orient);
        MountIntent? mount = MountOf(args.OptionalString("mount"));
        List<PortIntent> ports = new List<PortIntent>();
        if (args.Has("ports"))
        {
            List<Args?> items = args.Objects("ports", MaximumPorts);
            for (int index = 0; index < items.Count; index++)
            {
                Args item = items[index] ?? throw ApiErrors.InvalidArgument($"orient.ports[{index}] must be an object.");
                JToken toward = item.Optional("toward") ??
                                throw ApiErrors.InvalidArgument($"orient.ports[{index}].toward is required.");
                ports.Add(new PortIntent(item.OptionalString("role"), item.OptionalInt("index", 0, 64),
                    item.OptionalString("type"), Target(toward, $"orient.ports[{index}].toward")));
            }
        }

        JObject? flow = args.OptionalObject("flow");
        OrientTarget? from = null;
        OrientTarget? to = null;
        if (flow != null)
        {
            from = flow["from"] != null ? Target(flow["from"]!, "orient.flow.from") : null;
            to = flow["to"] != null ? Target(flow["to"]!, "orient.flow.to") : null;
        }

        JToken? controls = args.Optional("controls_toward");
        return new OrientIntent(mount, args.OptionalBool("upright") ?? true,
            controls != null ? Target(controls, "orient.controls_toward") : null, ports, from, to);
    }

    /// <summary>An orient target as given: an axis, room, player, a point or a thing.</summary>
    internal static OrientTarget Target(JToken token, string name)
    {
        if (token.Type == JTokenType.String)
        {
            string word = token.Value<string>()!.Trim().ToLowerInvariant();
            if (GridStep.TryParse(word, out GridStep axis))
            {
                return new OrientTarget.Along(axis);
            }

            switch (word)
            {
                case "room":
                    return OrientTarget.IntoRoom.Instance;
                case "player":
                    Human human = PlayerOrigin.RequireHuman();
                    return new OrientTarget.At(Bodies.V(human.Position), "the player");
            }
        }

        if (token is JObject thing && thing["reference_id"] != null)
        {
            Thing found = GameLookup.RequireThing(new Args(thing).ThingId("reference_id"));
            return new OrientTarget.At(Bodies.V(found.Position), $"{Names.Of(found)} {found.ReferenceId}");
        }

        if (token is JArray || token is JObject)
        {
            Metres point = BuildArgs.PositionOf(token, name);
            return new OrientTarget.At(new Vec3(point.X, point.Y, point.Z), point.ToString());
        }

        throw ApiErrors.InvalidArgument(
            $"{name} must be an axis (+x .. -z), \"room\", \"player\", a point [x, y, z] or {{reference_id}}.");
    }

    private static MountIntent? MountOf(string? text)
    {
        if (text == null)
        {
            return null;
        }

        string word = text.Trim().ToLowerInvariant();
        if (GridStep.TryParse(word, out GridStep side))
        {
            return MountIntent.Side(side);
        }

        return word switch
        {
            "wall" => MountIntent.Wall,
            "floor" => MountIntent.Floor,
            "ceiling" => MountIntent.Ceiling,
            _ => throw ApiErrors.InvalidArgument("orient.mount must be wall, floor, ceiling or an axis (+x .. -z).")
        };
    }

    /// <summary>A prefab whose flow a logic Mode reverses: the turbo volume pumps (Mode 0 right, 1 left).</summary>
    internal static bool ReversibleFlow(Structure prefab) => prefab is TurboVolumePump;

    /// <summary>The ports of a prefab at a position and turn, for scoring.</summary>
    internal static List<OrientPort> Ports(Structure prefab, Vector3 position, Quaternion rotation, int typeMask)
    {
        List<OrientPort> ports = new List<OrientPort>();
        PieceModel? model = PieceShapes.Placed(prefab, position, rotation, 0);
        if (model == null)
        {
            return ports;
        }

        foreach (PortCell port in PortCells.Of(model.Ends, typeMask))
        {
            if (!port.Toward.HasValue)
            {
                continue;
            }

            string role = ((ConnectionRole)port.Role).ToString();
            ports.Add(new OrientPort(port.Index, ((NetworkType)port.Type).ToString(), role,
                TwoWayChutePorts.FlowOf(prefab, port.Type, role), Bodies.V(PieceShapes.CentreOf(port.Cell)), port.Toward.Value.Opposite));
        }

        return ports;
    }

    /// <summary>
    /// Whether the 2 m cell a little more than a metre out from a point along a direction is in a room: from a cell's
    /// centre the next cell, from a point on a face plane the cell across it. The point ahead is read without rounding
    /// (LargeCells.Containing): rounded to a decimetre, a step back from a cell centre lands on the face plane, which
    /// belongs to the cell the point started in.
    /// </summary>
    internal static System.Func<Vec3, GridStep, bool> RoomAhead(GridFacts facts) =>
        (from, direction) => facts.RoomAt(LargeCells.Containing(from + Vec3.Of(direction) * 1.01)) != null;

    internal static OrientChoiceView ViewOf(OrientScore score) =>
        new OrientChoiceView(OrientationView.Of(score.Candidate.Turn),
            double.IsInfinity(score.Score) ? (double?)null : System.Math.Round(score.Score, 2), score.Reasons,
            score.ReversedFlow);
}
