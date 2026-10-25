#nullable enable

using System.Collections;
using System.Collections.Generic;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Views;
using StationGodMCP.LatticeLink;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game.Show;

/// <summary>
/// show highlight (catalogue show.json), the two branches the old show highlight handler ran: clear removes every mark the
/// player's screen has of the mod's, and a draw finds what each target names and hands the screen the marks to draw
/// through walls, frames and terrain with (Highlights, the T-Ray SPU's material). Things by id and a network's pieces
/// are drawn with their own meshes, tinted; a world point gets a beam and, out of view, an arrow at the screen's edge
/// with its distance and bearing. Each target's reply row says what it drew and where it is from the camera. Drawn on
/// the player's screen (PlayerSight): this game's, or a remote player's through their StationGod; with several players
/// in play and none named (as_player), each of theirs (ShowAudience). Nothing in the world changes.
/// </summary>
internal static class HighlightScreen
{
    /// <summary>A network's pieces are drawn up to this many; the reply's row says when more were found.</summary>
    private const int MaximumPieces = 4096;

    /// <summary>clear: every mark drawn for the player so far goes (each player's in play, ShowAudience); nothing is
    /// found and nothing drawn.</summary>
    internal static HighlightView Clear()
    {
        ShowAudience audience = ShowAudience.ToClear();
        int cleared = audience.Each(static screen => screen.ClearHighlights());
        if (LatticePresence.Running)
        {
            LatticePresence.EndHighlights();
        }

        audience.Notes.Insert(0, "Cleared.");
        return new HighlightView(new List<HighlightTargetView>(), cleared, 0.0, audience.Renderer, audience.Notes,
            audience.DrawnOn, audience.Source);
    }

    /// <summary>Each target found and drawn: the player's screen shows them for the call's seconds, or keeps what it
    /// already showed with keep. With several players in play and none named, each one's screen (ShowAudience).</summary>
    internal static HighlightView Show(HighlightRequest request)
    {
        ShowAudience audience = ShowAudience.ToDraw("show highlight", measures: true);
        Vec3? eye = audience.Camera?.Eye;
        List<HighlightTargetView> views = new List<HighlightTargetView>(request.Targets.Count);
        List<ScreenMark> marks = new List<ScreenMark>(request.Targets.Count);
        for (int index = 0; index < request.Targets.Count; index++)
        {
            views.Add(Found(request.Targets[index], index, eye, marks));
        }

        List<string> notes = audience.Notes;
        LightGhosts(request, notes);
        int cleared = audience.Each(screen => screen.Highlight(!request.Keep, marks, request.Seconds, notes));
        return new HighlightView(views, cleared, request.Seconds, audience.Renderer, notes, audience.DrawnOn,
            audience.Source);
    }

    // One target found: what it names, the mark the screen draws and the row the reply lists.
    private static HighlightTargetView Found(HighlightTarget target, int index, Vec3? eye, List<ScreenMark> marks)
    {
        switch (target)
        {
            case HighlightTarget.Point point:
                marks.Add(new ScreenMark(point, new List<Thing>()));
                return HighlightTargetView.OfPoint(index, point, eye.HasValue ? Heading.From(eye.Value, point.At) : null);
            case HighlightTarget.Ghost ghost:
                BoardGhost standing = GhostBoardStore.Board.Find(ghost.Id) ?? throw ApiErrors.Refused("ghost_not_found",
                    $"targets[{index}].ghost: no ghost {ghost.Id} on the board (find ghost lists them).");
                HighlightTarget.Point beacon = new HighlightTarget.Point(standing.Position, ghost.Tint, ghost.ColorName,
                    ghost.Label ?? $"ghost {ghost.Id} ({standing.PrefabName})", ghost.Pulse);
                marks.Add(new ScreenMark(beacon, new List<Thing>()));
                return HighlightTargetView.OfPoint(index, beacon,
                    eye.HasValue ? Heading.From(eye.Value, beacon.At) : null);
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

    // With Lattice, the ghost targets themselves are lit for the call's seconds (Lattice's timed highlight), beside the
    // beacons every screen draws; a new call without keep ends the last call's.
    private static void LightGhosts(HighlightRequest request, List<string> notes)
    {
        if (!LatticePresence.Running)
        {
            return;
        }

        if (!request.Keep)
        {
            LatticePresence.EndHighlights();
        }

        List<long> ids = new List<long>();
        foreach (HighlightTarget target in request.Targets)
        {
            if (target is HighlightTarget.Ghost ghost)
            {
                ids.Add(ghost.Id);
            }
        }

        if (ids.Count > 0 && LatticePresence.Highlight(ids, request.Seconds) is { } refused)
        {
            notes.Add($"Lattice did not light the ghosts: {refused}; their beacons show where they are.");
        }
    }

    // The heading of the nearest of the things, so far ones can be found; null for none.
    private static Heading? Nearest(Vec3? from, List<Thing> things)
    {
        if (!from.HasValue)
        {
            return null;
        }

        Vec3 eye = from.Value;
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
