#nullable enable

using System;
using Assets.Scripts.Atmospherics;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// When a gas or liquid mix burns by itself, by the game's own rule (GasMixture.IsAutoIgnition, called from
/// Atmosphere.TryCombust): methane or hydrogen over 1 mol (GasMixture.MinCombustionMoles) above 573.15 K, liquid
/// alcohol above 673.15 K, each 250 K lower with over 1 mol of nitrous oxide or 150 K with ozone; hydrazine above its
/// critical temperature. The lint function auto_ignition_temperature and check ignition's id form both judge with it.
/// </summary>
internal static class MixIgnition
{
    /// <summary>The kelvin above which the mix burns by itself; null when no fuel is over 1 mol.</summary>
    internal static double? AutoIgnitionK(Func<Chemistry.GasType, double> mol)
    {
        double least = GasMixture.MinCombustionMoles.ToDouble();
        double offset = Math.Min(
            mol(Chemistry.GasType.NitrousOxide) + mol(Chemistry.GasType.LiquidNitrousOxide) > least
                ? Chemistry.AutoIgnitionOffsetNitrogenDioxide.ToDouble()
                : 0.0,
            mol(Chemistry.GasType.Ozone) + mol(Chemistry.GasType.LiquidOzone) > least
                ? Chemistry.AutoIgnitionOffsetOzone.ToDouble()
                : 0.0);
        double? limit = null;
        void Fuel(bool present, double kelvin) => limit = present ? Math.Min(limit ?? double.MaxValue, kelvin) : limit;
        Fuel(mol(Chemistry.GasType.Methane) + mol(Chemistry.GasType.LiquidMethane) > least,
            Chemistry.AutoIgnitionMethane.ToDouble() + offset);
        Fuel(mol(Chemistry.GasType.Hydrogen) + mol(Chemistry.GasType.LiquidHydrogen) > least,
            Chemistry.AutoIgnitionHydrogen.ToDouble() + offset);
        Fuel(mol(Chemistry.GasType.LiquidAlcohol) > least, Chemistry.AutoIgnitionAlcohol.ToDouble() + offset);
        Fuel(mol(Chemistry.GasType.Hydrazine) + mol(Chemistry.GasType.LiquidHydrazine) > least,
            Chemistry.AutoIgnitionHydrazine.ToDouble());
        return limit;
    }

    /// <summary>The autoignition temperature of a mix the game holds.</summary>
    internal static double? AutoIgnitionK(GasMixture mixture) =>
        AutoIgnitionK(type => mixture.GetMoleValue(type).Quantity.ToDouble());

    /// <summary>
    /// Whether the mix burns on the next combustion tick by itself (Atmosphere.TryCombust, unsparked): it is past its
    /// autoignition temperature and holds fuel and oxidiser, or hypergolics.
    /// </summary>
    internal static bool IgnitesNow(GasMixture mixture)
    {
        double minimum = Chemistry.MINIMUM_QUANTITY_MOLES.ToDouble();
        bool starved = mixture.TotalHypergolics.ToDouble() < minimum &&
                       (mixture.TotalOxidiser.ToDouble() < minimum || mixture.TotalFuel.ToDouble() < minimum);
        return !starved && mixture.IsAutoIgnition();
    }
}
