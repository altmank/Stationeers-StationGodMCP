#nullable enable

namespace StationGodMCP.Pure;

/// <summary>
/// When a carried item catches fire (CODE Thing.ShouldIgnite, DynamicThing.OnFireTick), kept free of game types.
///
/// Two paths, either one: the flashpoint path needs the air already burning (Atmosphere.Inflamed) and hotter than
/// the flashpoint divided by the air's pressure as a share of one atmosphere, clamped to 0..1, so thin air raises
/// the bar and air at zero pressure never lights; the autoignition path needs the air hotter than the autoignition
/// temperature and holding more than 10 MJ (Thing.MinimumAutoignitionEnergy). OnFireTick checks either only for a
/// burnable thing, not hidden in its parent's slot, in air of at least 1.5 kPa (Thing.MinimumIgnitionPressurePropane).
/// </summary>
internal static class IgnitionRule
{
    internal const double MinimumAutoignitionEnergyJ = 10000000.0;

    internal const double MinimumIgnitionPressureKpa = 1.5;

    /// <summary>The temperature the air must pass for the flashpoint path; null without a flashpoint or air.</summary>
    internal static double? EffectiveFlashpointK(double? flashpointK, double oneAtmosphereRatio)
    {
        if (flashpointK is not > 0.0 || !(oneAtmosphereRatio > 0.0))
        {
            return null;
        }

        return flashpointK.Value / (oneAtmosphereRatio > 1.0 ? 1.0 : oneAtmosphereRatio);
    }

    /// <summary>OnFireTick's guards before it asks ShouldIgnite.</summary>
    internal static bool FireTickChecks(bool burnable, bool hidden, double pressureKpa) =>
        burnable && !hidden && pressureKpa >= MinimumIgnitionPressureKpa;
}
