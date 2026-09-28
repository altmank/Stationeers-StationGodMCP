#nullable enable

using Assets.Scripts.Objects;
using Objects.Rockets;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game.Build;

/// <summary>
/// Reads RocketTraits off a prefab or a standing structure: IRocketInternals.StrictlyInternal (a Battery's or
/// Transformer's own flag, a Tank's InternalCellType, always true for rocket-only classes), the hull classes, and the
/// rocket a placed piece is tied to (Structure.RocketData, IRocketInternals.RocketNetwork).
/// </summary>
internal static class RocketParts
{
    internal static RocketTraits Of(Structure structure) =>
        new RocketTraits(
            structure is IRocketInternals { StrictlyInternal: true },
            structure is StructureFuselage || structure is LaunchMount,
            structure.RocketData?.Network != null || structure is IRocketInternals { RocketNetwork: not null } ||
            (structure is SmallGrid piece && RunPlanner.InRocket(piece)));
}
