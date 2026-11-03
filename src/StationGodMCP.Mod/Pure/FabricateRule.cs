#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;

namespace StationGodMCP.Pure;

/// <summary>
/// write fabricator's rules, without game types: the limits on a call's load, why a fabricator is busy or cannot run,
/// what a call consumed, and the refusals' messages (each ends "Nothing was loaded or made.").
/// </summary>
internal static class FabricateRule
{
    internal const int MaximumLoads = 16;

    internal const double MaximumLoadQuantity = 10000;

    /// <summary>The most maker prefabs not_a_recipe names.</summary>
    internal const int MaximumMakers = 5;

    /// <summary>Amounts at or below this are rounding left in a mixture.</summary>
    internal const double Trace = 1e-6;

    private const string Closing = "Nothing was loaded or made.";

    /// <summary>Why the fabricator cannot take a job now, checked in this order: making, open, output_occupied; else null.</summary>
    internal static string? BusyReason(bool activate, bool open, string? exportOccupant)
    {
        if (activate)
        {
            return "making";
        }

        if (open)
        {
            return "open";
        }

        return exportOccupant != null ? "output_occupied" : null;
    }

    /// <summary>Why the fabricator cannot make, checked in this order: not_built, off, unpowered, error; else null.</summary>
    internal static string? PowerReason(bool completed, bool canManufacture, bool onOff, bool powered, int error)
    {
        if (!completed || !canManufacture)
        {
            return "not_built";
        }

        if (!onOff)
        {
            return "off";
        }

        if (!powered)
        {
            return "unpowered";
        }

        return error != 0 ? "error" : null;
    }

    /// <summary>Per reagent, before + loaded - after rounded to 6 places, keeping only values above the trace.</summary>
    internal static Dictionary<string, double> Consumed(IReadOnlyDictionary<string, double> before,
        IReadOnlyDictionary<string, double> loaded, IReadOnlyDictionary<string, double> after)
    {
        List<string> names = new List<string>(before.Keys);
        foreach (string name in loaded.Keys)
        {
            if (!names.Contains(name))
            {
                names.Add(name);
            }
        }

        foreach (string name in after.Keys)
        {
            if (!names.Contains(name))
            {
                names.Add(name);
            }
        }

        names.Sort(StringComparer.Ordinal);
        Dictionary<string, double> consumed = new Dictionary<string, double>();
        foreach (string name in names)
        {
            double value = Math.Round(Get(before, name) + Get(loaded, name) - Get(after, name), 6);
            if (value > Trace)
            {
                consumed[name] = value;
            }
        }

        return consumed;
    }

    internal static string WrongTier(string prefab, string recipeTier, string fabricator, string machineTier) =>
        $"{prefab} is a {recipeTier} recipe; {fabricator} is {machineTier}. Build it on to a {recipeTier} state " +
        $"(build finish). {Closing}";

    /// <summary>missing: reagent to amount lacking; ingotOf: reagent to the ingot prefab that supplies it, where known.</summary>
    internal static string MissingReagents(string fabricator, IReadOnlyDictionary<string, double> missing,
        IReadOnlyDictionary<string, string> ingotOf)
    {
        List<string> parts = new List<string>();
        foreach (KeyValuePair<string, double> lack in missing)
        {
            string amount = lack.Value.ToString("0.######", CultureInfo.InvariantCulture);
            parts.Add(ingotOf.TryGetValue(lack.Key, out string? ingot)
                ? $"{amount} {lack.Key} ({ingot})"
                : $"{amount} {lack.Key}");
        }

        return $"{fabricator}'s stock lacks {string.Join(", ", parts)} for that recipe; load more. {Closing}";
    }

    internal static string NotARecipe(string prefab, string fabricator, IReadOnlyList<string> makers)
    {
        List<string> shown = new List<string>();
        for (int index = 0; index < makers.Count && index < MaximumMakers; index++)
        {
            shown.Add(makers[index]);
        }

        string who = makers.Count == 0
            ? "no machine makes it"
            : "made by " + string.Join(", ", shown) +
              (makers.Count > MaximumMakers ? $" and {makers.Count - MaximumMakers} more" : string.Empty);
        return $"{fabricator} has no recipe for {prefab} ({who}). {Closing}";
    }

    private static double Get(IReadOnlyDictionary<string, double> values, string name) =>
        values.TryGetValue(name, out double value) ? value : 0.0;
}
