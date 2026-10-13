#nullable enable

using System.Collections;
using System.Collections.Generic;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game.Show;

/// <summary>
/// show highlight (catalogue show.json), the two branches the old highlight handler ran: clear removes every mark the
/// player's screen has of the mod's, and a draw finds what each target names and hands the screen the marks to draw
/// through walls, frames and terrain with (Highlights, the T-Ray SPU's material). Things by id and a network's pieces
/// are drawn with their own meshes, tinted; a world point gets a beam and, out of view, an arrow at the screen's edge
/// with its distance and bearing. Each target's reply row says what it drew and where it is from the camera. Drawn on
/// the player's screen (PlayerView): this game's, or a remote player's through their StationGod. Nothing in the world
/// changes.
/// </summary>
internal static class HighlightScreen
{
    /// <summary>A network's pieces are drawn up to this many; the reply's row says when more were found.</summary>
    private const int MaximumPieces = 4096;

    /// <summary>clear: every mark drawn for the player so far goes; nothing is found and nothing drawn.</summary>
    internal static HighlightView Clear()
    {
        PlayerView player = PlayerView.Current();
        PlayerScreen shown = player.ScreenOrLocal();
        return new HighlightView(new List<HighlightTargetView>(), shown.ClearHighlights(), 0.0, shown.Renderer,
            new List<string> { "Cleared." }, shown.DrawnOn, player.Source);
    }

    /// <summary>Each target found and drawn: the player's screen shows them for the call's seconds, or keeps what it
    /// already showed with keep.</summary>
    internal static HighlightView Show(HighlightRequest request)
    {
        CameraView camera = PlayerView.Current().Require("show highlight");
        Vec3 eye = camera.Eye;
        List<HighlightTargetView> views = new List<HighlightTargetView>(request.Targets.Count);
        List<ScreenMark> marks = new List<ScreenMark>(request.Targets.Count);
        for (int index = 0; index < request.Targets.Count; index++)
        {
            views.Add(Found(request.Targets[index], index, eye, marks));
        }

        PlayerScreen screen = camera.Screen;
        List<string> notes = new List<string>();
        int cleared = screen.Highlight(!request.Keep, marks, request.Seconds, notes);
        return new HighlightView(views, cleared, request.Seconds, screen.Renderer, notes, screen.DrawnOn,
            camera.Source);
    }

    // One target found: what it names, the mark the screen draws and the row the reply lists.
    private static HighlightTargetView Found(HighlightTarget target, int index, Vec3 eye, List<ScreenMark> marks)
    {
        switch (target)
        {
            case HighlightTarget.Point point:
                marks.Add(new ScreenMark(point, new List<Thing>()));
                return HighlightTargetView.OfPoint(index, point, Heading.From(eye, point.At));
            case HighlightTarget.Network network:
                List<Thing> pieces = NetworkPieces(network.Id, $"targets[{index}].network", out ThingId resolved,
                    out bool truncated);
                marks.Add(new ScreenMark(target, pieces));
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

                marks.Add(new ScreenMark(target, found));
                return HighlightTargetView.OfThings(index, target, "things", null, found.Count, missing,
                    Nearest(eye, found), false);
            default:
                throw new System.InvalidOperationException("Unknown highlight target.");
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
                    $"{name}: {Names.Of(thing)} ({thing.PrefabName}) is no cable, pipe or chute piece; give a device " +
                    "in id, or name a network or a piece on it.")
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
                          $"{name}: {id} names no thing and no cable, pipe or chute network. A network's id changes " +
                          "after almost every edit; name a piece on it instead.");
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
