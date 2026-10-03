#nullable enable

using System.Collections;
using System.Collections.Generic;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api;

/// <summary>
/// highlight: show the local player where things are, through walls, frames and terrain (Highlights). Things by id
/// and a network's pieces are drawn with their own meshes, tinted, as the T-Ray SPU draws pipes and cables (XRay); a
/// world point gets a beam and, out of view, an arrow at the screen's edge with its distance and bearing. Timed; a new
/// call replaces the last unless keep is true; clear: true only removes them. Changes nothing in the world.
/// </summary>
internal static class HighlightApi
{
    private const int MaximumPieces = 4096;

    internal static HighlightView Handle(Args args)
    {
        if (args.OptionalBool("clear") ?? false)
        {
            args.Reject("clear", "targets", "seconds", "keep");
            return new HighlightView(new List<HighlightTargetView>(), Highlights.Clear(), 0, XRay.MeshSource,
                new List<string> { "Cleared." });
        }

        HighlightRequest request = HighlightRequest.Parse(args);
        if (!Look.HasCamera)
        {
            throw ApiErrors.Refused("no_camera",
                "There is no player camera to draw for (a dedicated server has none: a remote player's camera " +
                "stays on their own machine).");
        }

        if (XRay.MeshMaterial == null)
        {
            throw ApiErrors.Refused("no_xray_material",
                "Neither the T-Ray SPU's material nor a built-in shader to draw through walls with was found.");
        }

        int cleared = request.Keep ? 0 : Highlights.Clear();
        float until = Time.realtimeSinceStartup + (float)request.Seconds;
        Vec3 eye = Bodies.V(Highlights.CameraPosition());
        List<HighlightTargetView> views = new List<HighlightTargetView>(request.Targets.Count);
        for (int index = 0; index < request.Targets.Count; index++)
        {
            views.Add(Show(request.Targets[index], index, until, eye));
        }

        List<string> notes = new List<string>
        {
            "Drawn on this game's screen only, through walls, frames and terrain; nothing in the world changed.",
            "distance_m and bearing_deg are from the camera when the call ran (bearing: 0 north, +z; 90 east, +x)."
        };
        return new HighlightView(views, cleared, request.Seconds, XRay.MeshSource, notes);
    }

    private static HighlightTargetView Show(HighlightTarget target, int index, float until, Vec3 eye)
    {
        switch (target)
        {
            case HighlightTarget.Point point:
                Highlights.Add(new PointMark(point, until));
                return HighlightTargetView.OfPoint(index, point, Heading.From(eye, point.At));
            case HighlightTarget.Network network:
                List<Thing> pieces = NetworkPieces(network.Id, $"targets[{index}].network_id", out ThingId resolved,
                    out bool truncated);
                Highlights.Add(new ThingsMark(target, until, pieces));
                return HighlightTargetView.OfThings(index, target, "network", resolved, pieces.Count,
                    new List<ThingId>(), Nearest(eye, pieces), truncated);
            case HighlightTarget.Things things:
                List<Thing> found = new List<Thing>(things.Ids.Count);
                List<ThingId> missing = new List<ThingId>();
                foreach (ThingId id in things.Ids)
                {
                    if (GameLookup.TryFindThing(id, out Thing thing) && !thing.IsBeingDestroyed)
                    {
                        found.Add(thing);
                    }
                    else
                    {
                        missing.Add(id);
                    }
                }

                Highlights.Add(new ThingsMark(target, until, found));
                return HighlightTargetView.OfThings(index, target, "things", null, found.Count, missing,
                    Nearest(eye, found), false);
            default:
                throw ApiErrors.InvalidArgument($"targets[{index}] names nothing to highlight.");
        }
    }

    // The heading of the nearest of the things, so far ones can be found; null for none.
    private static Heading? Nearest(Vec3 eye, List<Thing> things)
    {
        Heading? best = null;
        foreach (Thing thing in things)
        {
            Heading heading = Heading.From(eye, Bodies.V(thing.Position));
            if (best == null || heading.Distance < best.Value.Distance)
            {
                best = heading;
            }
        }

        return best;
    }

    // A network's own id, or a cable, pipe or chute piece standing for it: every piece of it, up to the cap.
    private static List<Thing> NetworkPieces(ThingId id, string name, out ThingId resolved, out bool truncated)
    {
        IReferencable? network;
        if (GameLookup.TryFindThing(id, out Thing thing))
        {
            network = thing switch
            {
                Cable cable => cable.CableNetwork,
                Pipe pipe => pipe.PipeNetwork,
                Chute chute => chute.ChuteNetwork,
                _ => throw ApiErrors.InvalidArgument(
                    $"{name}: {Names.Of(thing)} ({thing.PrefabName}) is no cable, pipe or chute piece; give a " +
                    "device in reference_ids, or name a network or a piece on it.")
            };
            if (network == null)
            {
                throw ApiErrors.Refused("no_network", $"{name}: {thing.PrefabName} {id} is on no network.");
            }
        }
        else
        {
            network = Referencable.Find<CableNetwork>(id.Value) as IReferencable ??
                      Referencable.Find<PipeNetwork>(id.Value) as IReferencable ??
                      Referencable.Find<ChuteNetwork>(id.Value) ??
                      throw ApiErrors.Refused("network_not_found",
                          $"{name}: {id} names no thing and no cable, pipe or chute network. A network's id " +
                          "changes after almost every edit; name a piece on it instead.");
        }

        resolved = new ThingId(network.ReferenceId);
        IEnumerable members = network switch
        {
            CableNetwork cables => cables.CableList,
            PipeNetwork pipes => pipes.StructureList,
            ChuteNetwork chutes => chutes.StructureList,
            _ => new List<Thing>()
        };
        List<Thing> pieces = new List<Thing>();
        truncated = false;
        foreach (object? member in members)
        {
            if (!(member is Thing piece) || piece == null || piece.IsBeingDestroyed)
            {
                continue;
            }

            if (pieces.Count == MaximumPieces)
            {
                truncated = true;
                break;
            }

            pieces.Add(piece);
        }

        return pieces;
    }
}
