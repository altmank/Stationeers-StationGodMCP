#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace StationGodMCP.Pure.Verbs;

/// <summary>One need as a share of its capacity: before, after, and the capacity the share is of.</summary>
internal sealed record NeedChange(double Before, double After, double Capacity)
{
    /// <summary>A change from stored amounts (the game's own units) to shares of capacity.</summary>
    internal static NeedChange Of(double beforeAmount, double afterAmount, double capacity) =>
        new NeedChange(beforeAmount / capacity, afterAmount / capacity, capacity);
}

/// <summary>The stomach's waste ratio before and after; there is none without a stomach or with Sanitation off.</summary>
internal sealed record WasteChange(double Before, double After);

/// <summary>A warning on a reply: a code and what it means.</summary>
internal sealed record NeedsWarning(string Code, string Message);

/// <summary>The values a write needs call asks for; a value left out is left alone. At least one is given.</summary>
internal sealed record NeedsRequest(double? Nutrition, double? Hydration, double? Waste)
{
    internal const double MaximumNutrition = 1.0;

    /// <summary>The game clamps Entity.Hydration to 8.75, which is 1.75 times the 5 its ratio divides by.</summary>
    internal const double MaximumHydration = 1.75;

    internal const double MaximumWaste = 1.0;

    internal bool AsksWaste => Waste.HasValue;

    /// <summary>Whether the call leaves the waste at or over the point the next life tick wets the suit.</summary>
    internal bool WetsNextTick => Waste is >= 1.0;
}

/// <summary>What the options of a write needs call come to: a request, or why there is none.</summary>
internal abstract record NeedsAsk
{
    /// <summary>The request, or the refusal for options that name nothing or lie out of range.</summary>
    internal static NeedsAsk Of(string label, double? nutrition, double? hydration, double? waste)
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        Range(problems, "nutrition", nutrition, NeedsRequest.MaximumNutrition);
        Range(problems, "hydration", hydration, NeedsRequest.MaximumHydration);
        Range(problems, "waste", waste, NeedsRequest.MaximumWaste);
        if (problems.Count > 0)
        {
            return new NothingAsked(OptionProblem.Refusal(label, problems));
        }

        if (!nutrition.HasValue && !hydration.HasValue && !waste.HasValue)
        {
            return new NothingAsked(ItemMoveMissing.Of(label,
                "give at least one of nutrition, hydration or waste", "nutrition", "hydration", "waste"));
        }

        return new Asked(new NeedsRequest(nutrition, hydration, waste));
    }

    private static void Range(List<OptionProblem> problems, string name, double? value, double maximum)
    {
        if (value.HasValue && (double.IsNaN(value.Value) || value.Value < 0.0 || value.Value > maximum))
        {
            problems.Add(new OptionProblem(name, $"must be from 0 to {maximum.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}"));
        }
    }
}

/// <summary>A call that asks for something.</summary>
internal sealed record Asked(NeedsRequest Request) : NeedsAsk;

/// <summary>A call that asks for nothing that can be done: its refusal.</summary>
internal sealed record NothingAsked(VerbError Error) : NeedsAsk;

/// <summary>What the checks need to know of the body the needs are set on.</summary>
internal sealed record BodyFacts(bool HasStomach, bool SanitationOn);

/// <summary>The checks' answer: every check passed, or the first that refused.</summary>
internal abstract record NeedsCheck
{
    internal const string NoOrgan = "no_organ";
    internal const string SanitationOff = "sanitation_off";

    /// <summary>Every check runs before anything is set, so a refused call changes nothing.</summary>
    internal static NeedsCheck Of(NeedsRequest request, BodyFacts body)
    {
        if (!body.HasStomach)
        {
            return new NeedsRefused(NoOrgan,
                "This body has no stomach (a robot's), so it has no nutrition, hydration or waste to set.");
        }

        if (request.AsksWaste && !body.SanitationOn)
        {
            return new NeedsRefused(SanitationOff,
                "Sanitation is off in this game's difficulty, so the game reads waste as 0 and never fills it. " +
                "Drop waste, or turn Sanitation on.");
        }

        return new NeedsCleared();
    }
}

internal sealed record NeedsCleared : NeedsCheck;

internal sealed record NeedsRefused(string Code, string Message) : NeedsCheck;

/// <summary>The warnings a write needs reply carries.</summary>
internal static class NeedsWarnings
{
    internal const string WetsNextTick = "wets_next_tick";
    internal const string Drains = "drains";

    internal static List<NeedsWarning> Of(NeedsRequest request)
    {
        List<NeedsWarning> warnings = new List<NeedsWarning>();
        if (request.WetsNextTick)
        {
            warnings.Add(new NeedsWarning(WetsNextTick,
                "Waste at 1 wets the suit (or the room) at the next life tick and empties the stomach."));
        }

        if (request.Nutrition.HasValue || request.Hydration.HasValue)
        {
            warnings.Add(new NeedsWarning(Drains,
                "The values drop from the next life tick at the rates inspect player gives for hunger and thirst."));
        }

        return warnings;
    }
}

/// <summary>The stomach's waste as the game keeps it: PollutedWater moles, and the ratio they make.</summary>
internal static class WasteMoles
{
    /// <summary>Stomach.GetWasteCapacity: the stomach's Volume (10 L) times its 0.5 waste share.</summary>
    internal const double CapacityLitres = 5.0;

    /// <summary>The moles of polluted water that read as this ratio (Stomach.GetWasteRatio inverted).</summary>
    internal static double MolesFor(double ratio, double litresPerMole) =>
        Math.Clamp(ratio, 0.0, 1.0) * CapacityLitres / litresPerMole;

    /// <summary>Stomach.GetWasteRatio: moles times the liquid's molar volume over the capacity, clamped to 0..1.</summary>
    internal static double RatioOf(double moles, double litresPerMole) =>
        Math.Clamp(moles * litresPerMole / CapacityLitres, 0.0, 1.0);

    /// <summary>The moles to add (positive) or remove (negative) to take the stomach from one amount to another.</summary>
    internal static double Difference(double currentMoles, double targetMoles) => targetMoles - currentMoles;
}

/// <summary>Whether a waste read-back has reached what was asked, within what the game's float maths keeps.</summary>
internal static class WasteReadBack
{
    internal const double Tolerance = 0.002;

    internal static bool Reached(double asked, double read) => Math.Abs(asked - read) <= Tolerance;
}
