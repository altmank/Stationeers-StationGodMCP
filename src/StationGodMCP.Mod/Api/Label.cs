#nullable enable

using System.Collections.Generic;
using System.Text.RegularExpressions;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using ObjectLabel = Assets.Scripts.Objects.Label;

namespace StationGodMCP.Api;

/// <summary>
/// label: rename a thing as the hand Labeller does. Writes.
///
/// The Labeller's own path (CODE, Labeller.InputRenameFinished): an empty name becomes the prefab's name
/// (Thing.SourcePrefab.DisplayName), so "clearing" a label writes the game's name rather than removing CustomName; a
/// name is cut to 200 characters; rich-text tags are stripped (Regex "&lt;.*?&gt;") except on a Sign or Label, which
/// keep them. The host renames with Thing.RenameThing (sets CustomName, which marks the thing's network update so
/// clients follow, then OnRenamed and the OnThingCustomName event; CustomName is saved with the thing); a
/// multiplayer client sends NetworkClient.RenameThing to the host and sees the name when the host's update arrives.
/// Only classes the Labeller renames are taken (Labels.CanRename). The Labeller's power and on/off checks belong to
/// the tool in hand and do not apply.
/// </summary>
internal static class LabelApi
{
    internal const int MaximumLabels = 64;

    private const int MaximumLength = 200;
    private static readonly Regex RichText = new Regex("<.*?>");

    internal static object Handle(Args args)
    {
        if (!args.Has("labels"))
        {
            return Rename(args.ThingId("reference_id"), args.String("name"), 0);
        }

        args.Reject("labels", "reference_id", "name");
        JArray list = args.Array("labels", MaximumLabels);
        BatchBuilder batch = new BatchBuilder(list.Count);
        for (int index = 0; index < list.Count; index++)
        {
            ThingId? id = null;
            try
            {
                if (!(list[index] is JObject entry))
                {
                    throw ApiErrors.InvalidArgument($"labels[{index}] must be an object.");
                }

                Args entryArgs = new Args(entry);
                id = entryArgs.ThingId("reference_id");
                batch.Succeeded(Rename(id.Value, entryArgs.String("name"), index));
            }
            catch (ApiException refusal)
            {
                batch.Failed(new NotLabelledView(index, id, refusal));
            }
        }

        return batch.Build();
    }

    private static LabelledView Rename(ThingId id, string name, int index)
    {
        Thing thing = GameLookup.RequireThing(id);
        if (thing.IsBeingDestroyed)
        {
            throw ApiErrors.ThingNotFound(id);
        }

        if (!Labels.CanRename(thing))
        {
            throw ApiErrors.Refused("not_labelable",
                LabelRule.NotLabelable($"{thing.DisplayName} ({id})", thing.GetType().Name));
        }

        LabelStateView previous = StateOf(thing);
        string written = AsTheLabellerWrites(thing, name);
        bool sent = !GameManager.RunSimulation;
        if (sent)
        {
            NetworkClient.RenameThing(thing.ReferenceId, written);
        }
        else
        {
            Thing.RenameThing(thing.ReferenceId, written);
        }

        return new LabelledView(index, GameLookup.ViewOf(thing), written, sent, previous, StateOf(thing));
    }

    private static LabelStateView StateOf(Thing thing) =>
        new LabelStateView(thing.DisplayName, string.IsNullOrEmpty(thing.CustomName) ? null : thing.CustomName);

    internal static string AsTheLabellerWrites(Thing thing, string name)
    {
        string value = string.IsNullOrEmpty(name) ? thing.SourcePrefab.DisplayName : name;
        value = value.Length <= MaximumLength ? value : value.Substring(0, MaximumLength);
        return thing is Sign || thing is ObjectLabel ? value : RichText.Replace(value, string.Empty);
    }
}
