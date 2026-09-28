#nullable enable

using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Appliances;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Objects.Pipes;
using Objects.Electrical;
using Objects.Rockets;

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
/// </summary>
internal static class Labels
{
    internal static bool CanRename(Thing thing) =>
        thing is DraggableThing or Device or ItemRenamable or ProgrammableChip or Plant or Flag or BobbleHead
            or Chicken or HydroponicTray or StructureInLineTank or LaunchMount or StructureFuselage
            or LandingPadCenter;

    /// <summary>The name the Labeller gave, rich text removed; null when it has none.</summary>
    internal static string? CustomNameOf(Thing thing) =>
        string.IsNullOrEmpty(thing.CustomName) ? null : Text.Plain(thing.CustomName);

    /// <summary>The game's name for the thing's prefab, whatever it has been labelled.</summary>
    internal static string GameNameOf(Thing thing) => Localization.GetThingName(thing.PrefabName);

    /// <summary>
    /// Whether the name the game shows (the label when there is one), or under a label the prefab's own name, contains
    /// part, ignoring case. Looks the prefab name up only for labelled things, whose DisplayName hides it.
    /// </summary>
    internal static bool NameContains(Thing thing, string part) =>
        ItemFilter.Contains(thing.DisplayName, part) ||
        (!string.IsNullOrEmpty(thing.CustomName) && ItemFilter.Contains(GameNameOf(thing), part));
}
