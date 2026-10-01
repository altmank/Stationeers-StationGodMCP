#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure.Lint;

/// <summary>
/// Which gases burn and which feed a fire, by the game's gas type names (Chemistry.GasType): what GasMixture counts as
/// fuel (methane, hydrogen, alcohol, hydrazine, gas or liquid) and as oxidiser (oxygen, nitrous oxide, ozone).
/// </summary>
internal static class LintGases
{
    private static readonly HashSet<string> Fuels = new HashSet<string>(StringComparer.Ordinal)
        { "Methane", "LiquidMethane", "Hydrogen", "LiquidHydrogen", "LiquidAlcohol", "Hydrazine", "LiquidHydrazine" };

    private static readonly HashSet<string> Oxidisers = new HashSet<string>(StringComparer.Ordinal)
        { "Oxygen", "LiquidOxygen", "NitrousOxide", "LiquidNitrousOxide", "Ozone", "LiquidOzone" };

    internal static void Register(LintLibrary library)
    {
        library
            .Add(new LintFunction("is_fuel", "(gas: string) -> bool",
                "A gas the game burns as fuel: Methane, Hydrogen, LiquidAlcohol, Hydrazine and their liquids.",
                call => LintValue.Of(Fuels.Contains(call[0].AsString))))
            .Add(new LintFunction("is_oxidiser", "(gas: string) -> bool",
                "A gas the game burns fuel with: Oxygen, NitrousOxide, Ozone and their liquids.",
                call => LintValue.Of(Oxidisers.Contains(call[0].AsString))))
            .Add(new LintFunction("mol_of", "(gases: map<number>, names: list<string>) -> number",
                "The moles of the named gases together.",
                call =>
                {
                    double total = 0;
                    IReadOnlyDictionary<string, LintValue> gases = call[0].AsMap;
                    foreach (LintValue name in call[1].AsList)
                    {
                        total += gases.TryGetValue(name.AsString, out LintValue mol) ? mol.AsNumber : 0;
                    }

                    return LintValue.Of(total);
                }));
    }
}
