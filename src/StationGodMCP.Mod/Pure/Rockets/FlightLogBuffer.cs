#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace StationGodMCP.Pure.Rockets;

/// <summary>One flight-log sample: the rocket fields that change in flight.</summary>
internal sealed class FlightSample
{
    internal FlightSample(double gameSeconds, string state, string node, string target, double progress,
        double altitude, double velocity, double acceleration, double massKg, double fuelMol, double fuelTemperatureK,
        double fuelPressureKpa, double throttle, double thrustN, double maxRecordedThrustN, double confidence,
        double batteryJ, int cargoSlots, double burnTimeS, string rule)
    {
        GameSeconds = gameSeconds;
        State = state;
        Node = node;
        Target = target;
        Progress = progress;
        Altitude = altitude;
        Velocity = velocity;
        Acceleration = acceleration;
        MassKg = massKg;
        FuelMol = fuelMol;
        FuelTemperatureK = fuelTemperatureK;
        FuelPressureKpa = fuelPressureKpa;
        Throttle = throttle;
        ThrustN = thrustN;
        MaxRecordedThrustN = maxRecordedThrustN;
        Confidence = confidence;
        BatteryJ = batteryJ;
        CargoSlots = cargoSlots;
        BurnTimeS = burnTimeS;
        Rule = rule;
    }

    internal double GameSeconds { get; }

    internal string State { get; }

    internal string Node { get; }

    internal string Target { get; }

    internal double Progress { get; }

    /// <summary>Rocket.GetAltitude: above the launch mount while launching or landing, -1 in space.</summary>
    internal double Altitude { get; }

    internal double Velocity { get; }

    internal double Acceleration { get; }

    internal double MassKg { get; }

    internal double FuelMol { get; }

    internal double FuelTemperatureK { get; }

    internal double FuelPressureKpa { get; }

    internal double Throttle { get; }

    internal double ThrustN { get; }

    internal double MaxRecordedThrustN { get; }

    internal double Confidence { get; }

    internal double BatteryJ { get; }

    internal int CargoSlots { get; }

    internal double BurnTimeS { get; }

    internal string Rule { get; }

    internal const string CsvHeader =
        "game_s,state,node,target,progress,altitude_m,velocity_ms,acceleration_ms2,mass_kg,fuel_mol,fuel_k," +
        "fuel_kpa,throttle,thrust_n,max_recorded_thrust_n,confidence,battery_j,cargo_slots,burn_time_s,rule";

    internal string ToCsv()
    {
        StringBuilder line = new StringBuilder(160);
        Append(line, GameSeconds).Append(',').Append(Quote(State)).Append(',').Append(Quote(Node)).Append(',')
            .Append(Quote(Target)).Append(',');
        Append(line, Progress).Append(',');
        Append(line, Altitude).Append(',');
        Append(line, Velocity).Append(',');
        Append(line, Acceleration).Append(',');
        Append(line, MassKg).Append(',');
        Append(line, FuelMol).Append(',');
        Append(line, FuelTemperatureK).Append(',');
        Append(line, FuelPressureKpa).Append(',');
        Append(line, Throttle).Append(',');
        Append(line, ThrustN).Append(',');
        Append(line, MaxRecordedThrustN).Append(',');
        Append(line, Confidence).Append(',');
        Append(line, BatteryJ).Append(',');
        line.Append(CargoSlots.ToString(CultureInfo.InvariantCulture)).Append(',');
        Append(line, BurnTimeS).Append(',').Append(Quote(Rule));
        return line.ToString();
    }

    private static StringBuilder Append(StringBuilder line, double value) =>
        line.Append(value.ToString("0.###", CultureInfo.InvariantCulture));

    private static string Quote(string text) =>
        text.IndexOf(',') < 0 && text.IndexOf('"') < 0 ? text : "\"" + text.Replace("\"", "\"\"") + "\"";
}

/// <summary>A state or target change the log noticed between two samples.</summary>
internal sealed class FlightEvent
{
    internal FlightEvent(double gameSeconds, string what)
    {
        GameSeconds = gameSeconds;
        What = what;
    }

    internal double GameSeconds { get; }

    internal string What { get; }
}

/// <summary>
/// The flight recorder's memory: a ring of the last samples (the oldest drop out once it is full), the state and target
/// changes seen (bounded too), and a running summary over everything recorded, dropped samples included.
/// </summary>
internal sealed class FlightLogBuffer
{
    internal const int MaximumEvents = 200;

    private readonly FlightSample?[] _ring;
    private readonly List<FlightEvent> _events = new List<FlightEvent>(16);
    private int _next;
    private FlightSample? _last;

    internal FlightLogBuffer(int capacity)
    {
        _ring = new FlightSample?[Math.Max(1, capacity)];
    }

    internal int Capacity => _ring.Length;

    /// <summary>Samples held now (at most the capacity).</summary>
    internal int Count { get; private set; }

    /// <summary>Samples ever recorded.</summary>
    internal long Recorded { get; private set; }

    internal FlightSample? First { get; private set; }

    internal FlightSample? Last => _last;

    internal double LowestAltitude { get; private set; } = double.PositiveInfinity;

    internal double FastestDescent { get; private set; }

    internal double PeakThrustN { get; private set; }

    internal double LowestConfidence { get; private set; } = double.PositiveInfinity;

    internal double LowestBatteryJ { get; private set; } = double.PositiveInfinity;

    internal IReadOnlyList<FlightEvent> Events => _events;

    /// <summary>Events dropped past MaximumEvents.</summary>
    internal int EventsDropped { get; private set; }

    internal void Add(FlightSample sample)
    {
        if (_last != null)
        {
            if (!string.Equals(_last.State, sample.State, StringComparison.Ordinal))
            {
                Note(sample.GameSeconds, $"state {_last.State} -> {sample.State}");
            }

            if (!string.Equals(_last.Target, sample.Target, StringComparison.Ordinal))
            {
                Note(sample.GameSeconds, $"target {Named(_last.Target)} -> {Named(sample.Target)}");
            }

            if (!string.Equals(_last.Node, sample.Node, StringComparison.Ordinal))
            {
                Note(sample.GameSeconds, $"at {Named(sample.Node)}");
            }
        }

        First ??= sample;
        _ring[_next] = sample;
        _next = (_next + 1) % _ring.Length;
        Count = Math.Min(Count + 1, _ring.Length);
        Recorded++;
        _last = sample;
        if (sample.Altitude >= 0.0)
        {
            LowestAltitude = Math.Min(LowestAltitude, sample.Altitude);
        }

        FastestDescent = Math.Min(FastestDescent, sample.Velocity);
        PeakThrustN = Math.Max(PeakThrustN, sample.ThrustN);
        LowestConfidence = Math.Min(LowestConfidence, sample.Confidence);
        LowestBatteryJ = Math.Min(LowestBatteryJ, sample.BatteryJ);
    }

    /// <summary>The held samples oldest first, from offset, every 'every'th, at most limit of them.</summary>
    internal List<FlightSample> Page(int offset, int limit, int every)
    {
        int step = Math.Max(1, every);
        List<FlightSample> page = new List<FlightSample>(Math.Min(limit, Count));
        int oldest = Count < _ring.Length ? 0 : _next;
        for (int index = offset; index < Count && page.Count < limit; index += step)
        {
            FlightSample? sample = _ring[(oldest + index) % _ring.Length];
            if (sample != null)
            {
                page.Add(sample);
            }
        }

        return page;
    }

    private void Note(double seconds, string what)
    {
        if (_events.Count >= MaximumEvents)
        {
            EventsDropped++;
            return;
        }

        _events.Add(new FlightEvent(seconds, what));
    }

    private static string Named(string text) => text.Length == 0 ? "none" : text;
}
