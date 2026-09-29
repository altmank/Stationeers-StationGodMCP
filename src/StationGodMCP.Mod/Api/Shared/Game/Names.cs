#nullable enable

using Assets.Scripts.Objects;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>The name a message shows for a thing.</summary>
internal static class Names
{
    /// <summary>
    /// The game's DisplayName (the label, else the localised name), or the prefab name where the language file has no
    /// name (ThingName). Never for matching the game's own texts, which name things by DisplayName as it is.
    /// </summary>
    internal static string Of(Thing thing) => ThingName.Shown(thing.DisplayName, thing.PrefabName);
}
