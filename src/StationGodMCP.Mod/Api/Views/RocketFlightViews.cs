#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure.Rockets;

namespace StationGodMCP.Api.Views;

/// <summary>Rounding for the rocket tools' numbers, so replies stay short.</summary>
internal static class RocketRound
{
    internal static double Of(double value, int decimals) =>
        double.IsNaN(value) || double.IsInfinity(value) ? value : Math.Round(value, decimals);

    /// <summary>A finite number rounded, else null (JSON has no infinity).</summary>
    internal static double? Finite(double value, int decimals) =>
        double.IsNaN(value) || double.IsInfinity(value) ? null : Math.Round(value, decimals);
}

/// <summary>A space-map node: its name as the game shows it, the code DestinationCode takes, its kind and whether it is charted.</summary>
internal sealed class SpaceNodeView
{
    internal SpaceNodeView(ThingId referenceId, string name, string code, ulong codeValue, string type, bool charted)
    {
        ReferenceId = referenceId;
        Name = name;
        Code = code;
        CodeValue = codeValue;
        Type = type;
        Charted = charted;
    }

    public ThingId ReferenceId { get; }

    public string Name { get; }

    /// <summary>SpaceMapCode.String, as the map shows it.</summary>
    public string Code { get; }

    /// <summary>SpaceMapCode.Value: the number the avionics' DestinationCode logic takes.</summary>
    public ulong CodeValue { get; }

    public string Type { get; }

    public bool Charted { get; }
}

/// <summary>One hop of the route as the game's pathfinder lays it, re-planned at every node as the game does.</summary>
internal sealed class RouteHopView
{
    internal RouteHopView(SpaceNodeView from, SpaceNodeView to, double distance, double doneFraction)
    {
        From = from.Name;
        To = to.Name;
        ToCode = to.Code;
        Distance = RocketRound.Of(distance, 1);
        Done = doneFraction > 0.0 ? RocketRound.Of(doneFraction, 3) : null;
        Charted = to.Charted;
    }

    public string From { get; }

    public string To { get; }

    public string ToCode { get; }

    /// <summary>NodeTransit.GetDistance (twice the map distance): the Δv the hop costs (m/s).</summary>
    public double Distance { get; }

    /// <summary>The share already flown (the rocket's Progress), only on the hop under way.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public double? Done { get; }

    public bool Charted { get; }
}

/// <summary>One leg the forecast flew.</summary>
internal sealed class ForecastLegView
{
    internal ForecastLegView(LegResult result)
    {
        LegTally tally = result.Tally;
        Kind = result.Leg.Kind;
        From = result.Leg.From;
        To = result.Leg.To;
        Distance = result.Leg.Distance > 0f ? RocketRound.Of(result.Leg.Distance, 1) : null;
        Outcome = result.Outcome.Name;
        Detail = result.Outcome.Detail;
        Seconds = RocketRound.Of(tally.Seconds, 1);
        BurnSeconds = RocketRound.Of(tally.BurnSeconds, 1);
        DeltaV = RocketRound.Of(tally.DeltaV, 1);
        FuelStartMol = RocketRound.Of(tally.FuelStartMol, 0);
        FuelUsedMol = RocketRound.Of(tally.FuelUsedMol, 0);
        FuelLeftMol = RocketRound.Of(tally.FuelEndMol, 0);
        FuelKpaEnd = RocketRound.Of(tally.PressureEndKpa, 0);
        MassStartKg = RocketRound.Of(tally.MassStartKg, 1);
        MassEndKg = RocketRound.Of(tally.MassEndKg, 1);
        BatteryStartJ = RocketRound.Of(tally.BatteryStartJ, 0);
        BatteryEndJ = RocketRound.Of(tally.BatteryEndJ, 0);
        PeakThrustN = RocketRound.Of(tally.PeakThrustN, 0);
        MaxRecordedThrustN = RocketRound.Of(tally.MaxRecordedThrustN, 0);
        Landing = result.Outcome switch
        {
            LegOutcome.Landed landed => new LandingRunView(landed.Landing),
            LegOutcome.Crashed crashed => new LandingRunView(crashed.Landing),
            LegOutcome.LandingAborted aborted => new LandingRunView(aborted.Landing),
            _ => null
        };
    }

    /// <summary>launch, hop, park or landing.</summary>
    public string Kind { get; }

    public string From { get; }

    public string To { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public double? Distance { get; }

    public string Outcome { get; }

    public string Detail { get; }

    public double Seconds { get; }

    public double BurnSeconds { get; }

    /// <summary>Engine Δv spent (thrust / mass over time; on a launch it includes what gravity took back).</summary>
    public double DeltaV { get; }

    public double FuelStartMol { get; }

    public double FuelUsedMol { get; }

    public double FuelLeftMol { get; }

    /// <summary>The first tank's pressure at the end of the leg.</summary>
    public double FuelKpaEnd { get; }

    public double MassStartKg { get; }

    public double MassEndKg { get; }

    public double BatteryStartJ { get; }

    public double BatteryEndJ { get; }

    public double PeakThrustN { get; }

    public double MaxRecordedThrustN { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public LandingRunView? Landing { get; }
}

/// <summary>The landing autopilot's run: the check at the start and how it came down.</summary>
internal sealed class LandingRunView
{
    internal LandingRunView(LandingDetail detail)
    {
        Confidence = RocketRound.Finite(detail.Confidence, 2);
        ConfidenceBand = float.IsNaN(detail.Confidence) ? null : FlightMath.ConfidenceBand(detail.Confidence);
        MinRequiredThrustN = RocketRound.Finite(detail.MinRequiredThrust, 0);
        MaxExpectedThrustN = RocketRound.Of(detail.MaxExpectedThrust, 0);
        StartAltitudeM = RocketRound.Of(detail.StartAltitude, 0);
        StartMassKg = RocketRound.Of(detail.StartMassKg, 1);
        LowestStopAltitudeM = RocketRound.Finite(detail.LowestApex, 1);
        TouchdownSpeedMs = RocketRound.Of(Math.Abs(detail.TouchdownVelocity), 2);
        Seconds = RocketRound.Of(detail.Seconds, 1);
        LastRule = detail.LastRule.ToString();
        PowerLost = detail.PowerLost;
        FuelRanOut = detail.FuelRanOut;
    }

    /// <summary>The game's confidence ratio at the landing's start; null for a landing already under way.</summary>
    public double? Confidence { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? ConfidenceBand { get; }

    public double? MinRequiredThrustN { get; }

    public double MaxExpectedThrustN { get; }

    public double StartAltitudeM { get; }

    public double StartMassKg { get; }

    /// <summary>The lowest stop point (Rocket.GetApex) the autopilot saw while arresting the fall.</summary>
    public double? LowestStopAltitudeM { get; }

    public double TouchdownSpeedMs { get; }

    public double Seconds { get; }

    public string LastRule { get; }

    public bool PowerLost { get; }

    public bool FuelRanOut { get; }
}

/// <summary>The landing's margins.</summary>
internal sealed class LandingLimitsView
{
    internal LandingLimitsView(LandingLimits limits, double structureWithoutCargoKg, float confidenceFloor)
    {
        MaxLandingMassKg = Rounded(limits.MaxLandingMassKg, 0);
        MaxFuelPlusCargoKg = limits.MaxLandingMassKg.HasValue
            ? RocketRound.Of(limits.MaxLandingMassKg.Value - structureWithoutCargoKg, 0)
            : null;
        MinFuelMol = Rounded(limits.MinFuelMol, 0);
        MaxFuelMol = Rounded(limits.MaxFuelMol, 0);
        SurvivableThrustDropPct = limits.ThrustDrop.HasValue ? RocketRound.Of(limits.ThrustDrop.Value * 100.0, 1) : null;
        ConfidenceFloor = confidenceFloor;
        ConfidentMassKg = Rounded(limits.ConfidentMassKg, 0);
    }

    /// <summary>Heaviest total mass at the landing's start that still lands undamaged (null: none does).</summary>
    public double? MaxLandingMassKg { get; }

    /// <summary>That mass less the rocket's structure without cargo: what fuel and cargo together may weigh.</summary>
    public double? MaxFuelPlusCargoKg { get; }

    public double? MinFuelMol { get; }

    public double? MaxFuelMol { get; }

    /// <summary>The thrust loss (%) the landing survives while the confidence check still sees the recorded peak.</summary>
    public double? SurvivableThrustDropPct { get; }

    public double ConfidenceFloor { get; }

    /// <summary>Heaviest landing mass whose confidence stays at or above confidence_floor.</summary>
    public double? ConfidentMassKg { get; }

    private static double? Rounded(double? value, int decimals) =>
        value.HasValue ? RocketRound.Of(value.Value, decimals) : null;
}

/// <summary>The same landing flown from another re-entry profile.</summary>
internal sealed class ProfileLandingView
{
    internal ProfileLandingView(string profile, float altitude, LegResult result, double? thrustDrop)
    {
        Profile = profile;
        AltitudeM = altitude;
        Outcome = result.Outcome.Name;
        FuelUsedMol = RocketRound.Of(result.Tally.FuelUsedMol, 0);
        LandingDetail? detail = result.Outcome switch
        {
            LegOutcome.Landed landed => landed.Landing,
            LegOutcome.Crashed crashed => crashed.Landing,
            LegOutcome.LandingAborted aborted => aborted.Landing,
            _ => null
        };
        Confidence = detail == null ? null : RocketRound.Finite(detail.Confidence, 2);
        Seconds = detail == null ? null : RocketRound.Of(detail.Seconds, 1);
        TouchdownSpeedMs = detail == null ? null : RocketRound.Of(Math.Abs(detail.TouchdownVelocity), 2);
        SurvivableThrustDropPct = thrustDrop.HasValue ? RocketRound.Of(thrustDrop.Value * 100.0, 1) : null;
    }

    public string Profile { get; }

    public float AltitudeM { get; }

    public string Outcome { get; }

    public double FuelUsedMol { get; }

    public double? Confidence { get; }

    public double? Seconds { get; }

    public double? TouchdownSpeedMs { get; }

    public double? SurvivableThrustDropPct { get; }
}

/// <summary>Structures or terrain in the column a landing rocket sweeps (Rocket.CheckForOverlap, Rocket.cs:2320-2372).</summary>
internal sealed class ColumnView
{
    internal ColumnView(bool clear, double fromY, double toY, List<ColumnBlockerView> blockers, int more)
    {
        Clear = clear;
        FromY = RocketRound.Of(fromY, 1);
        ToY = RocketRound.Of(toY, 1);
        Blockers = blockers;
        More = more;
    }

    public bool Clear { get; }

    public double FromY { get; }

    public double ToY { get; }

    public List<ColumnBlockerView> Blockers { get; }

    /// <summary>Blockers found past the ones listed.</summary>
    public int More { get; }
}

internal sealed class ColumnBlockerView
{
    internal ColumnBlockerView(ThingId? referenceId, string what, double y, string sweptBy)
    {
        ReferenceId = referenceId;
        What = what;
        Y = RocketRound.Of(y, 1);
        SweptBy = sweptBy;
    }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ThingId? ReferenceId { get; }

    public string What { get; }

    public double Y { get; }

    /// <summary>engine (the sphere 1 m under the lowest hull piece) or nose (1 m over the highest).</summary>
    public string SweptBy { get; }
}

/// <summary>One in-game check: the game's own value against the port's.</summary>
internal sealed class SelfCheckView
{
    internal SelfCheckView(string name, double game, double ours, double tolerance)
    {
        Name = name;
        Game = RocketRound.Finite(game, 4);
        Ours = RocketRound.Finite(ours, 4);
        Ok = (double.IsInfinity(game) && game.Equals(ours)) || (double.IsNaN(game) && double.IsNaN(ours)) ||
             Math.Abs(game - ours) <= tolerance;
    }

    public string Name { get; }

    public double? Game { get; }

    public double? Ours { get; }

    public bool Ok { get; }
}

/// <summary>One flight-log row.</summary>
internal sealed class FlightSampleView
{
    internal FlightSampleView(FlightSample sample)
    {
        T = RocketRound.Of(sample.GameSeconds, 1);
        State = sample.State;
        Node = sample.Node;
        Target = sample.Target;
        Progress = RocketRound.Of(sample.Progress, 4);
        AltitudeM = RocketRound.Of(sample.Altitude, 1);
        VelocityMs = RocketRound.Of(sample.Velocity, 2);
        AccelerationMs2 = RocketRound.Of(sample.Acceleration, 3);
        MassKg = RocketRound.Of(sample.MassKg, 1);
        FuelMol = RocketRound.Of(sample.FuelMol, 1);
        FuelK = RocketRound.Of(sample.FuelTemperatureK, 1);
        FuelKpa = RocketRound.Of(sample.FuelPressureKpa, 0);
        Throttle = RocketRound.Of(sample.Throttle, 2);
        ThrustN = RocketRound.Of(sample.ThrustN, 0);
        MaxRecordedThrustN = RocketRound.Of(sample.MaxRecordedThrustN, 0);
        Confidence = RocketRound.Of(sample.Confidence, 2);
        BatteryJ = RocketRound.Of(sample.BatteryJ, 0);
        CargoSlots = sample.CargoSlots;
        BurnTimeS = RocketRound.Of(sample.BurnTimeS, 0);
        Rule = sample.Rule;
    }

    public double T { get; }

    public string State { get; }

    public string Node { get; }

    public string Target { get; }

    public double Progress { get; }

    public double AltitudeM { get; }

    public double VelocityMs { get; }

    public double AccelerationMs2 { get; }

    public double MassKg { get; }

    public double FuelMol { get; }

    public double FuelK { get; }

    public double FuelKpa { get; }

    public double Throttle { get; }

    public double ThrustN { get; }

    public double MaxRecordedThrustN { get; }

    public double Confidence { get; }

    public double BatteryJ { get; }

    public int CargoSlots { get; }

    public double BurnTimeS { get; }

    public string Rule { get; }
}

/// <summary>rocket_flight_log: one rocket's log, summarised, with a page of rows.</summary>
internal sealed class FlightLogView
{
    internal FlightLogView(ThingId rocketId, string rocketName, bool recording, double intervalS, string? csvPath,
        FlightLogBuffer buffer, List<FlightSampleView> rows, int offset, int every)
    {
        RocketId = rocketId;
        RocketName = rocketName;
        Recording = recording;
        IntervalS = intervalS;
        CsvPath = csvPath;
        Capacity = buffer.Capacity;
        Held = buffer.Count;
        Recorded = buffer.Recorded;
        FromT = buffer.First == null ? null : RocketRound.Of(buffer.First.GameSeconds, 1);
        ToT = buffer.Last == null ? null : RocketRound.Of(buffer.Last.GameSeconds, 1);
        FuelUsedMol = buffer.First == null || buffer.Last == null
            ? null
            : RocketRound.Of(buffer.First.FuelMol - buffer.Last.FuelMol, 1);
        LowestAltitudeM = RocketRound.Finite(buffer.LowestAltitude, 1);
        FastestDescentMs = RocketRound.Of(-buffer.FastestDescent, 2);
        PeakThrustN = RocketRound.Of(buffer.PeakThrustN, 0);
        LowestConfidence = RocketRound.Finite(buffer.LowestConfidence, 2);
        LowestBatteryJ = RocketRound.Finite(buffer.LowestBatteryJ, 0);
        Last = buffer.Last == null ? null : new FlightSampleView(buffer.Last);
        Events = new List<FlightEventView>(buffer.Events.Count);
        for (int index = 0; index < buffer.Events.Count; index++)
        {
            Events.Add(new FlightEventView(buffer.Events[index]));
        }

        EventsDropped = buffer.EventsDropped;
        Rows = rows;
        Offset = offset;
        Every = every;
        HasMore = offset + rows.Count * Math.Max(1, every) < buffer.Count;
    }

    public ThingId RocketId { get; }

    public string RocketName { get; }

    public bool Recording { get; }

    public double IntervalS { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? CsvPath { get; }

    public int Capacity { get; }

    public int Held { get; }

    public long Recorded { get; }

    public double? FromT { get; }

    public double? ToT { get; }

    public double? FuelUsedMol { get; }

    public double? LowestAltitudeM { get; }

    public double FastestDescentMs { get; }

    public double PeakThrustN { get; }

    public double? LowestConfidence { get; }

    public double? LowestBatteryJ { get; }

    public FlightSampleView? Last { get; }

    public List<FlightEventView> Events { get; }

    public int EventsDropped { get; }

    public List<FlightSampleView> Rows { get; }

    public int Offset { get; }

    public int Every { get; }

    public bool HasMore { get; }
}

internal sealed class FlightEventView
{
    internal FlightEventView(FlightEvent flightEvent)
    {
        T = RocketRound.Of(flightEvent.GameSeconds, 1);
        What = flightEvent.What;
    }

    public double T { get; }

    public string What { get; }
}

/// <summary>rocket_flight_log list: every log, summaries only.</summary>
internal sealed class FlightLogListView
{
    internal FlightLogListView(List<FlightLogView> logs)
    {
        Logs = logs;
        Count = logs.Count;
    }

    public List<FlightLogView> Logs { get; }

    public int Count { get; }
}

/// <summary>rocket_flight_log clear.</summary>
internal sealed class FlightLogClearedView
{
    internal FlightLogClearedView(bool cleared)
    {
        Cleared = cleared;
    }

    public bool Cleared { get; }
}
