#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Appliances;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Objects.Pipes;
using Objects.Electrical;
using Objects.Rockets;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;
using ObjectLabel = Assets.Scripts.Objects.Label;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// The hand Labeller's names. The game has no "can be labelled" flag: a thing takes a name when its class answers a
/// Labeller in AttackWith (or, for plants in a tray, HydroponicsUtils.LabellerInHand) with Labeller.Rename, which
/// stores the name through Thing.RenameThing into Thing.CustomName. Thing.DisplayName is then that name instead of the
/// prefab's localised name (Localization.GetThingName). The classes that rename, with every subclass: DraggableThing
/// (portable tanks and canisters, crates, portable generators, rovers), Device (every logic device, including Sign and
/// Label), ItemRenamable, ProgrammableChip, Plant, Flag, BobbleHead, Chicken, HydroponicTray, StructureInLineTank,
/// LaunchMount, StructureFuselage, LandingPadCenter. Plain structures (pipes, cables, frames, walls) and ordinary items
/// cannot be renamed, nor can the pipe-size in-line tanks (InLineTank: a Pipe with no Labeller answer), unlike the big
/// ones (StructureInLineTank); LabelRule words that refusal. A class may still refuse the Labeller before reaching its base; this is the class rule only.
/// <para>
/// A rename takes the Labeller's own path (Labeller.InputRenameFinished): an empty name becomes the prefab's name
/// (Thing.SourcePrefab.DisplayName), so "clearing" a label writes the game's name rather than removing CustomName; a
/// name is cut to 200 characters; rich-text tags are stripped (Regex "&lt;.*?&gt;") except on a Sign or Label, which
/// keep them. The host renames with Thing.RenameThing (sets CustomName, which marks the thing's network update so
/// clients follow, then OnRenamed and the OnThingCustomName event; CustomName is saved with the thing); a multiplayer
/// client sends NetworkClient.RenameThing to the host and sees the name when the host's update arrives. The
/// Labeller's power and on/off checks belong to the tool in hand and do not apply.
/// </para>
/// </summary>
internal static class Labels
{
    private const int MaximumLength = 200;
    private static readonly System.Text.RegularExpressions.Regex RichText =
        new System.Text.RegularExpressions.Regex("<.*?>");

    /// <summary>
    /// The renames in order, each failing alone: checked and reported (report), or made. An id may be "looking_at".
    /// </summary>
    internal static List<LabelResult> WriteMany(IReadOnlyList<LabelOrder> orders, bool report, string label)
    {
        List<LabelResult> results = new List<LabelResult>(orders.Count);
        foreach (LabelOrder order in orders)
        {
            try
            {
                Thing thing = Require(TargetIds.Of(order.Id, label));
                string written = AsTheLabellerWrites(thing, order.Label);
                results.Add(report
                    ? new LabelResult.Planned(new ThingId(thing.ReferenceId), thing.DisplayName,
                        CustomNameOf(thing) ?? thing.DisplayName, written)
                    : new LabelResult.Renamed(Rename(thing, written)));
            }
            catch (ApiException refused)
            {
                results.Add(new LabelResult.Failed(order.Index, order.Id, VerbError.Of(refused)));
            }
        }

        return results;
    }

    /// <summary>The name as the Labeller writes it: the prefab's name for an empty one, cut, tags stripped.</summary>
    internal static string AsTheLabellerWrites(Thing thing, string name)
    {
        string value = string.IsNullOrEmpty(name) ? thing.SourcePrefab.DisplayName : name;
        value = value.Length <= MaximumLength ? value : value.Substring(0, MaximumLength);
        return thing is Sign || thing is ObjectLabel ? value : RichText.Replace(value, string.Empty);
    }

    // The thing, there and of a class the Labeller renames.
    private static Thing Require(ThingId id)
    {
        Thing thing = GameLookup.RequireThing(id);
        if (thing.IsBeingDestroyed)
        {
            throw ApiErrors.ThingNotFound(id);
        }

        return CanRename(thing)
            ? thing
            : throw ApiErrors.Refused("not_labelable",
                LabelRule.NotLabelable($"{Names.Of(thing)} ({id})", thing.GetType().Name));
    }

    private static Relabel Rename(Thing thing, string written)
    {
        LabelStateView previous = StateOf(thing);
        bool sent = !GameManager.RunSimulation;
        if (sent)
        {
            NetworkClient.RenameThing(thing.ReferenceId, written);
        }
        else
        {
            Thing.RenameThing(thing.ReferenceId, written);
        }

        return new Relabel(new ThingId(thing.ReferenceId), thing.PrefabName, written, sent, previous, StateOf(thing));
    }

    private static LabelStateView StateOf(Thing thing) =>
        new LabelStateView(thing.DisplayName, string.IsNullOrEmpty(thing.CustomName) ? null : thing.CustomName);

    internal static bool CanRename(Thing thing) =>
        thing is DraggableThing or Device or ItemRenamable or ProgrammableChip or Plant or Flag or BobbleHead
            or Chicken or HydroponicTray or StructureInLineTank or LaunchMount or StructureFuselage
            or LandingPadCenter;

    /// <summary>The name the Labeller gave, rich text removed; null when it has none.</summary>
    internal static string? CustomNameOf(Thing thing) =>
        string.IsNullOrEmpty(thing.CustomName) ? null : Text.Plain(thing.CustomName);

    /// <summary>The game's name for the thing's prefab, whatever it has been labelled; the prefab name when the
    /// language file has none.</summary>
    internal static string GameNameOf(Thing thing) =>
        ThingName.Shown(Localization.GetThingName(thing.PrefabName), thing.PrefabName);

    /// <summary>
    /// Whether the name the game shows (the label when there is one), or under a label the prefab's own name, contains
    /// part, ignoring case. Looks the prefab name up only for labelled things, whose DisplayName hides it.
    /// </summary>
    internal static bool NameContains(Thing thing, string part) =>
        ItemFilter.Contains(thing.DisplayName, part) ||
        (!string.IsNullOrEmpty(thing.CustomName) && ItemFilter.Contains(GameNameOf(thing), part));
}
