#nullable enable

using System;
using Assets.Scripts.Objects;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Verbs;
using UnityEngine;

namespace StationGodMCP.Verbs;

/// <summary>
/// inspect contents (catalogue inspect.json): a thing's slots and what is in each, down to depth levels, empty slots
/// left out unless include empty, kept by prefab and name at any depth (ThingContents); id "player" is the player. On a
/// silo also its stored entries, one page of them (limit, offset; one id: with the wire cursor a walk of the entries,
/// KeyedPage). One id, or a list of ids (one row each). Read only.
/// </summary>
[ReplyFact(FactRule.Live, "the thing's values as the game holds them now")]
internal static class InspectContents
{
    private const string Player = "player";
    private const string Empty = "empty";

    internal static VerbOutcome Run(InspectContentsOptions options)
    {
        int ids = options.Selector.Ids!.Ids.Count;
        if (KeyedPage.OneThing(options.Paging, ids) is { } problem)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(InspectContentsOptions.Spec.Label, new[] { problem }));
        }

        WalkRequest walk = KeyedPage.WalkOf(options.Paging, ids);

        Selector selector = options.Selector;
        PrefabFilter? prefab = selector.Prefab;
        SlotFilter filter = new SlotFilter(
            prefab == null ? null : name => prefab.Keeps(name, string.IsNullOrEmpty(name) ? 0 : Animator.StringToHash(name)),
            selector.Name);
        int depth = (int)options.Depth!.Value;
        bool includeEmpty = options.Fields.Includes(Empty);
        Paging paging = options.Paging;
        return IdAnswers.Of(selector.Ids!, id =>
        {
            Thing thing = string.Equals(id.Trim(), Player, StringComparison.OrdinalIgnoreCase)
                ? PlayerOrigin.RequireHuman()
                : TargetIds.Thing(id, InspectContentsOptions.Spec.Label);
            return (new ThingId(thing.ReferenceId).ToString(), ThingContents.Read(thing, depth, includeEmpty, filter,
                walk, paging.Offset, paging.Limit ?? int.MaxValue));
        });
    }
}
