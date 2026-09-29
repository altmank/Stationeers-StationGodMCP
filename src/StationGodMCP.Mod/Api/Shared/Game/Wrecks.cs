#nullable enable

using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// Whether a thing is a wreck, as thing_health and find_things report it (is_broken, condition broken, the broken
/// filters): the game's Thing.IsBroken, or a burst pipe. A pipe bursts by Pipe.BurstPipe, which sets Pipe.IsBurst and
/// swaps in its BurstMesh but leaves DamageState alone, so a burst pipe reads 0 damage and Thing.IsBroken false.
/// </summary>
internal static class Wrecks
{
    internal static bool IsBroken(Thing thing) =>
        HealthCondition.IsWreck(thing.IsBroken, thing is Pipe pipe && pipe.IsBurst != PipeBurst.None);
}
