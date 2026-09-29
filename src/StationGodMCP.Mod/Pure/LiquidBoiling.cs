#nullable enable

using System;

namespace StationGodMCP.Pure;

/// <summary>
/// Whether a liquid boils away completely at the temperature its contents settle to once it has. The game never puts a
/// liquid's boiling point under its freezing point (Mole.EvaporationTemperatureClamped), and each mole that boils takes
/// its latent heat out of what is left: settling under either point means the rest cools to it first and stays
/// liquid, or freezes, before the last of it has boiled.
/// </summary>
internal static class LiquidBoiling
{
    /// <summary>
    /// How far above its freezing point a liquid must settle to be sure to boil away. Live (1.4.4 round 3, air-17):
    /// 0.268 mol water into a 0.9 kPa room predicted to settle at 273.187 K, 0.037 K above freezing, still lost its
    /// last 0.004 mol, which vanished with the room at 273.408 K (round 2: at 273.54 K); a move predicted to settle at
    /// 277.6 K lost nothing. Under freezing + 1 K the game's evaporation slows to a fixed trickle (Mole.ChangeState),
    /// so the tail of the liquid lingers where a cell cooler than the room's mean freezes it. 0.5 K covers both losses
    /// (at most 0.39 K above freezing) with room to spare.
    /// </summary>
    internal const double FreezingMarginK = 0.5;

    internal static bool BoilsAway(double settledK, double evaporationK, double freezingK) =>
        settledK >= Math.Max(evaporationK, freezingK + FreezingMarginK);
}
