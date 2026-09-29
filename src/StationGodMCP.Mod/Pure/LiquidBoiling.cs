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
    internal static bool BoilsAway(double settledK, double evaporationK, double freezingK) =>
        settledK >= Math.Max(evaporationK, freezingK);
}
