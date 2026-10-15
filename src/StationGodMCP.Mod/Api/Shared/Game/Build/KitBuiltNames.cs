#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game.Build;

/// <summary>The names of the loaded prefabs a kit builds, for naming the variants of a prefab no kit builds.</summary>
internal static class KitBuiltNames
{
    internal static string HintFor(Structure prefab, HashSet<int> kitBuilt) =>
        BuildableVariants.Hint(BuildableVariants.Of(prefab.PrefabName, WorldModel.Prefabs.NamesOf(kitBuilt)));
}
