#nullable enable

using System;
using System.Reflection;
using StationGodMCP.Pure.Rockets;

namespace StationGodMCP.Api.Shared.Game.Flight;

/// <summary>
/// The engines' combustion rate now: Terraforming Reloaded's TerraformingReloaded.Patching.Rockets.CombustionRate()
/// when that mod is loaded (read by reflection, the method cached by GameMembers; no reference to the mod), else the
/// game's own. Asked on every probe: the mod's option is per world. A call that fails falls back to the game's rate
/// with a note, never fails the tool.
/// </summary>
internal static class CombustionRates
{
    internal static CombustionRate Current()
    {
        if (!GameMembers.TerraformingCombustionRate.TryResolve())
        {
            return CombustionRate.Game;
        }

        try
        {
            return CombustionRate.FromTerraforming(GameMembers.TerraformingCombustionRate.Invoke(null));
        }
        catch (TargetInvocationException exception)
        {
            return CombustionRate.Unusable(
                $"Terraforming Reloaded's CombustionRate() failed ({exception.InnerException?.Message ?? exception.Message}); " +
                "the game's rate is used.");
        }
    }
}
