#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// plan flight's call, checked without the game: where to fly (node; left out, the rocket's target, else its pad), the
/// re-entry profile, the stop at the destination (park_s, mine, cargo, payload deploy, transfers, park_load_w, the way
/// back), the what-ifs that replace measured values, and what the reply works out besides the flight.
/// </summary>
internal sealed class ForecastRequest
{
    internal const int MaximumCargoSlots = 100000;

    private ForecastRequest(PlanFlightOptions options, ForecastWhatIf whatIf, bool explain)
    {
        Node = Trimmed(options.Node);
        Profile = options.Profile;
        ParkS = options.ParkS ?? 0.0;
        Mine = options.Mine ?? false;
        FillHolds = options.FillHolds ?? false;
        AddCargoSlots = options.AddCargoSlots is { } slots ? (int)slots : null;
        ParkLoadW = options.ParkLoadW;
        ThenReturn = options.ThenReturn ?? false;
        ReturnTo = Trimmed(options.ReturnTo);
        DeployPayload = options.DeployPayload ?? false;
        TransferMol = options.TransferMol;
        TransferWith = Trimmed(options.TransferWith);
        TransferBatteryJ = options.TransferBatteryJ ?? 0.0;
        WhatIf = whatIf;
        MinConfidence = options.MinConfidence ?? 0.25;
        Limits = options.Limits ?? true;
        ColumnCheck = options.ColumnCheck ?? true;
        Explain = explain;
    }

    /// <summary>The destination as given: a node's name, map code or id, or "pad"; null for the rocket's own.</summary>
    public string? Node { get; }

    /// <summary>Low, Medium, High or Max; null for the rocket's own.</summary>
    public string? Profile { get; }

    public double ParkS { get; }

    public bool Mine { get; }

    public bool FillHolds { get; }

    public int? AddCargoSlots { get; }

    /// <summary>The draw while parked; null takes what auto shut-off leaves on, plus mining gear with mine.</summary>
    public double? ParkLoadW { get; }

    public bool ThenReturn { get; }

    public string? ReturnTo { get; }

    public bool DeployPayload { get; }

    public double? TransferMol { get; }

    /// <summary>Another rocket at the stop, by id or name.</summary>
    public string? TransferWith { get; }

    public double TransferBatteryJ { get; }

    public ForecastWhatIf WhatIf { get; }

    public double MinConfidence { get; }

    public bool Limits { get; }

    public bool ColumnCheck { get; }

    /// <summary>include explain: the model's fixed rules among the assumptions.</summary>
    public bool Explain { get; }

    /// <summary>Whether the call stops at the destination before anything else (park, mine, the way back).</summary>
    public bool Stops => ParkS > 0.0 || ThenReturn || Mine;

    /// <summary>The call's request, or null with every problem added.</summary>
    internal static ForecastRequest? Of(PlanFlightOptions options, List<OptionProblem> problems)
    {
        int before = problems.Count;
        if (options.TransferMol != null && options.TransferWith != null)
        {
            problems.Add(new OptionProblem("transfer_with", "goes without transfer_mol: give one of them"));
        }

        if (options.AddCargoSlots > MaximumCargoSlots)
        {
            problems.Add(new OptionProblem("add_cargo_slots", $"is at most {MaximumCargoSlots}"));
        }

        ForecastWhatIf whatIf = ForecastWhatIf.Of(options.WhatIf as JObject, problems);
        return problems.Count > before ? null : new ForecastRequest(options, whatIf, options.Fields.Includes("explain"));
    }

    private static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text!.Trim();
}

/// <summary>
/// plan flight's what_if: values that replace what is measured, each optional. fuel_mix is gas or liquid names with a
/// fraction each; the game knows the names.
/// </summary>
internal sealed class ForecastWhatIf
{
    private static readonly string[] Keys =
    {
        "fuel_mol", "fuel_temperature_k", "fuel_mix", "thrust_scale", "throttle", "cargo_slots", "add_cargo_kg",
        "payload_kg", "battery_j", "battery_percent", "extra_load_w"
    };

    private ForecastWhatIf()
    {
    }

    public double? FuelMol { get; private set; }

    public double? FuelTemperatureK { get; private set; }

    /// <summary>Fractions by gas or liquid name, as given; null keeps the fuel's own mix.</summary>
    public IReadOnlyDictionary<string, double>? FuelMix { get; private set; }

    public double? ThrustScale { get; private set; }

    public double? Throttle { get; private set; }

    public int? CargoSlots { get; private set; }

    public double? AddCargoKg { get; private set; }

    public double? PayloadKg { get; private set; }

    public double? BatteryJ { get; private set; }

    public double? BatteryPercent { get; private set; }

    public double? ExtraLoadW { get; private set; }

    internal static ForecastWhatIf None { get; } = new ForecastWhatIf();

    internal static ForecastWhatIf Of(JObject? given, List<OptionProblem> problems)
    {
        if (given == null)
        {
            return None;
        }

        foreach (JProperty property in given.Properties())
        {
            if (System.Array.IndexOf(Keys, property.Name) < 0)
            {
                string? nearest = NearestName.Of(property.Name, Keys);
                problems.Add(new OptionProblem("what_if." + property.Name, "is not a what-if",
                    nearest != null ? new[] { nearest } : null));
            }
        }

        double? Ranged(string key, double minimum, double maximum, bool aboveMinimum = false)
        {
            JToken? token = given[key];
            if (token == null)
            {
                return null;
            }

            double? value = OptionReader.NumberOf(token);
            if (value is not { } number || double.IsNaN(number) || number < minimum || number > maximum ||
                (aboveMinimum && number <= minimum))
            {
                problems.Add(new OptionProblem("what_if." + key, aboveMinimum
                    ? $"is a number above {minimum}"
                    : maximum == double.MaxValue
                        ? $"is a number, {minimum} or more"
                        : $"is a number from {minimum} to {maximum}"));
                return null;
            }

            return number;
        }

        ForecastWhatIf what = new ForecastWhatIf
        {
            FuelMol = Ranged("fuel_mol", 0.0, double.MaxValue),
            FuelTemperatureK = Ranged("fuel_temperature_k", 0.0, double.MaxValue, aboveMinimum: true),
            ThrustScale = Ranged("thrust_scale", 0.0, 10.0),
            Throttle = Ranged("throttle", 0.0, 100.0),
            AddCargoKg = Ranged("add_cargo_kg", 0.0, double.MaxValue),
            PayloadKg = Ranged("payload_kg", 0.0, double.MaxValue),
            BatteryJ = Ranged("battery_j", 0.0, double.MaxValue),
            BatteryPercent = Ranged("battery_percent", 0.0, 100.0),
            ExtraLoadW = Ranged("extra_load_w", 0.0, double.MaxValue)
        };
        if (Ranged("cargo_slots", 0.0, ForecastRequest.MaximumCargoSlots) is { } slots)
        {
            if (slots != System.Math.Floor(slots))
            {
                problems.Add(new OptionProblem("what_if.cargo_slots", "is a whole number of slots"));
            }
            else
            {
                what.CargoSlots = (int)slots;
            }
        }

        if (given["fuel_mix"] is { } mix)
        {
            what.FuelMix = MixOf(mix, problems);
        }

        return what;
    }

    private static Dictionary<string, double>? MixOf(JToken given, List<OptionProblem> problems)
    {
        if (given is not JObject mix)
        {
            problems.Add(new OptionProblem("what_if.fuel_mix", "is {gas or liquid name: fraction}"));
            return null;
        }

        Dictionary<string, double> fractions = new Dictionary<string, double>(System.StringComparer.Ordinal);
        foreach (JProperty property in mix.Properties())
        {
            if (OptionReader.NumberOf(property.Value) is { } fraction && fraction >= 0.0)
            {
                fractions[property.Name] = fraction;
            }
            else
            {
                problems.Add(new OptionProblem("what_if.fuel_mix." + property.Name, "is a fraction of 0 or more"));
            }
        }

        return fractions;
    }
}
