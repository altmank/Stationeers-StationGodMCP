#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Build;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.RemoteView;
using StationGodMCP.Pure.Verbs;
using UnityEngine;

namespace StationGodMCP.Verbs;

/// <summary>
/// find ghost (catalogue find.json): the world's ghost board (GhostBoardStore), filtered by ids, prefab, owner, source
/// and near with radius, nearest first around near, else by id; each ghost with its build structure options, and the
/// page's as one build. Read only.
/// </summary>
[ReplyFact(FactRule.Live, "the ghost board")]
internal static class FindGhost
{
    internal static VerbOutcome Run(FindGhostOptions options)
    {
        string label = FindGhostOptions.Spec.Label;
        List<OptionProblem> problems = new List<OptionProblem>();
        List<long>? ids = null;
        if (options.Ids is Newtonsoft.Json.Linq.JArray given)
        {
            ids = new List<long>();
            foreach (Newtonsoft.Json.Linq.JToken id in given)
            {
                if (id.Type == Newtonsoft.Json.Linq.JTokenType.Integer)
                {
                    ids.Add((long)id);
                }
                else
                {
                    problems.Add(new OptionProblem("ids", "is a list of ghost ids"));
                    break;
                }
            }
        }

        GhostSource? source = options.Source switch
        {
            "player" => GhostSource.Player,
            "agent" => GhostSource.Agent,
            _ => null
        };
        if (problems.Count > 0)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(label, problems));
        }

        Vec3? near = options.Selector.Near is { } point ? NearCentres.Of(point, label) : (Vec3?)null;
        GhostFilter filter = new GhostFilter(ids, options.Selector.Prefab, options.Owner, source);
        List<(BoardGhost Ghost, double? Distance)> kept = filter.Apply(GhostBoardStore.Board.All, near,
            options.Selector.Radius);
        Page<(BoardGhost Ghost, double? Distance)> page = options.Paging.Of(kept);
        List<BoardGhost> shown = new List<BoardGhost>(page.Entries.Count);
        foreach ((BoardGhost ghost, double? _) in page.Entries)
        {
            shown.Add(ghost);
        }

        return new VerbOutcome.Answered(ListReply.OfPage("ghosts", page,
            static entry => new GhostView(entry.Ghost, DisplayName(entry.Ghost), entry.Distance))
            .Add("build_arguments", GhostFilter.BuildArguments(shown)));
    }

    private static string? DisplayName(BoardGhost ghost) =>
        Prefab.Find(ghost.PrefabHash) is Thing prefab && prefab != null ? prefab.DisplayName : null;
}

/// <summary>
/// show ghost (catalogue show.json): puts planned structures on the world's ghost board, where every player sees them
/// until they are built or taken off, or takes ghosts off. The placements are planned as build structure plans them
/// (free, no lint; nothing built) and each goes on the board where the plan put it, problems or not; the reply's plan
/// says why one would not build. Nothing in the world changes.
/// </summary>
[ReplyFact(FactRule.Live, "the ghost board")]
internal static class ShowGhost
{
    internal static VerbOutcome Run(ShowGhostOptions options)
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        GhostOrder? order = GhostOrder.Of(options, problems);
        if (order == null)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(ShowGhostOptions.Spec.Label, problems));
        }

        List<string> notes = new List<string>();
        List<long> removed = new List<long>();
        if (order.Remove.Count > 0)
        {
            List<long> gone = GhostBoardStore.Remove(order.Remove);
            removed.AddRange(gone);
            if (gone.Count < order.Remove.Count)
            {
                notes.Add($"{order.Remove.Count - gone.Count} of the ids named no ghost on the board.");
            }
        }

        if (order.Clear == "all")
        {
            removed.AddRange(GhostBoardStore.RemoveWhere(static _ => true));
        }
        else if (order.Clear == "agent")
        {
            removed.AddRange(GhostBoardStore.RemoveWhere(static ghost => ghost.Source == GhostSource.Agent));
        }

        List<PlacedGhostView> placed = new List<PlacedGhostView>();
        GhostPlanView? plan = null;
        if (order.HasPlacements)
        {
            PlacePlan planned = PlacePlanner.Plan(order.Arguments, lint: false);
            plan = new GhostPlanView(planned.Ready, planned.Problems, planned.Warnings);
            List<GhostPlacement> ghosts = new List<GhostPlacement>();
            foreach (PlannedPlacement placement in planned.Placements)
            {
                if (placement.Prefab == null || !placement.Position.HasValue)
                {
                    notes.Add($"Placement {placement.Index} was not resolved to a point: no ghost for it.");
                    continue;
                }

                Quaternion rotation = placement.Rotation;
                Vec3 euler = CubeRotation.EulerDegreesOf(rotation.x, rotation.y, rotation.z, rotation.w) is { } turns
                    ? new Vec3(turns.X, turns.Y, turns.Z)
                    : Bodies.V(rotation.eulerAngles);
                int state = placement.State ?? placement.Prefab.BuildStates.Count - 1;
                ghosts.Add(new GhostPlacement(placement.Prefab.PrefabHash, state, Bodies.V(placement.Position.Value),
                    euler, order.TintOf(placement.Index), order.LabelOf(placement.Index)));
            }

            List<string> refused = new List<string>();
            foreach (BoardGhost ghost in GhostBoardStore.Place(ghosts, GhostSource.Agent, "agent", refused))
            {
                placed.Add(new PlacedGhostView(ghost));
            }

            notes.AddRange(refused);
        }

        return new VerbOutcome.Answered(new ShowGhostView(placed, removed, GhostBoardStore.Board.Count, plan, notes));
    }
}
