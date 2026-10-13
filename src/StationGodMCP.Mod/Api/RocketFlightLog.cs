#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Assets.Scripts.Serialization;
using Objects.Rockets;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game.Flight;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Rockets;
using StationGodMCP.Pure.Shaping;
using UnityEngine;

namespace StationGodMCP.Api;

/// <summary>
/// The rocket flight recorders a write flight_log starts, stops and clears: a recorder samples a rocket's changing
/// fields at a set interval of game time into a bounded ring (and optionally a CSV in the save's folder). Recording only
/// reads. A started or stopped recording answers its first rows, as read flight_log reads them.
/// </summary>
internal static class FlightLogWrites
{
    private const int ShownRows = ReplyDefaults.FlightLogRows;

    internal static FlightLogView Start(Rocket rocket, double intervalS, int capacity, bool csv)
    {
        string? path = null;
        if (csv)
        {
            string folder = SaveFolder() ??
                            throw ApiErrors.Refused("no_save_folder", "No save is loaded, so there is no folder for the CSV.");
            path = Path.Combine(folder,
                $"rocket-log-{rocket.ReferenceId}-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.csv");
        }

        return Shown(RocketFlightRecorder.Start(rocket, intervalS, capacity, path));
    }

    internal static FlightLogView Stop(Rocket rocket) => Shown(RocketFlightRecorder.Stop(rocket.ReferenceId));

    internal static FlightLogClearedView Clear(Rocket rocket)
    {
        RocketFlightRecorder.Clear(rocket.ReferenceId);
        return new FlightLogClearedView(true);
    }

    private static FlightLogView Shown(RocketFlightLogEntry entry)
    {
        FlightLogView view = FlightLogs.View(entry, 0, ShownRows, 1);
        if (view.Rows.Count < entry.Buffer.Count)
        {
            Truncations.Note("rows", view.Rows.Count, entry.Buffer.Count,
                "read flight_log gives the rest (limit, every, offset)", false);
        }

        return view;
    }

    private static string? SaveFolder()
    {
        XmlSaveLoad? saves = XmlSaveLoad.Instance;
        string? station = saves != null ? saves.CurrentStationName : null;
        return string.IsNullOrEmpty(station)
            ? null
            : Path.Combine(StationSaveUtils.GetSavePathSavesSubDir().FullName, station);
    }
}

/// <summary>
/// The flight recordings as read flight_log reads them: one recording's rows, every n-th sample from offset (counted in
/// samples), at most limit, or every recording without its rows. Read only.
/// </summary>
internal static class FlightLogs
{
    /// <summary>The recordings, each without its rows.</summary>
    internal static FlightLogListView List()
    {
        List<FlightLogView> logs = new List<FlightLogView>(2);
        foreach (RocketFlightLogEntry entry in RocketFlightRecorder.Entries)
        {
            logs.Add(View(entry, 0, 0, 1));
        }

        return new FlightLogListView(logs);
    }

    /// <summary>One recording with every n-th of its samples from offset, at most limit.</summary>
    internal static FlightLogView View(RocketFlightLogEntry entry, int offset, int limit, int every)
    {
        List<FlightSample> samples = limit > 0 ? entry.Buffer.Page(offset, limit, every) : new List<FlightSample>();
        List<FlightSampleView> rows = new List<FlightSampleView>(samples.Count);
        for (int index = 0; index < samples.Count; index++)
        {
            rows.Add(new FlightSampleView(samples[index]));
        }

        return new FlightLogView(new ThingId(entry.RocketId), entry.RocketName, entry.Recording, entry.IntervalS,
            entry.CsvPath, entry.Buffer, rows, offset, every);
    }
}

/// <summary>One rocket's recording.</summary>
internal sealed class RocketFlightLogEntry
{
    internal RocketFlightLogEntry(long rocketId, string rocketName, double intervalS, int capacity, string? csvPath)
    {
        RocketId = rocketId;
        RocketName = rocketName;
        IntervalS = intervalS;
        Buffer = new FlightLogBuffer(capacity);
        CsvPath = csvPath;
    }

    internal long RocketId { get; }

    internal string RocketName { get; }

    internal double IntervalS { get; }

    internal FlightLogBuffer Buffer { get; }

    internal string? CsvPath { get; set; }

    internal bool Recording { get; set; } = true;

    internal float NextAt { get; set; }
}

/// <summary>
/// The recorders, ticked from StationGodMod.Update. A sample reads the rocket as rocket_status does (main thread,
/// last tick's values); a rocket that is gone stops its recording and keeps what it has. Any failure stops that
/// recording rather than the frame.
/// </summary>
internal static class RocketFlightRecorder
{
    private static readonly Dictionary<long, RocketFlightLogEntry> Logs = new Dictionary<long, RocketFlightLogEntry>();

    internal static IEnumerable<RocketFlightLogEntry> Entries => Logs.Values;

    internal static RocketFlightLogEntry Start(Rocket rocket, double intervalS, int capacity, string? csvPath)
    {
        RocketFlightLogEntry entry = new RocketFlightLogEntry(rocket.ReferenceId, rocket.DisplayName, intervalS,
            capacity, csvPath) { NextAt = Time.time };
        if (csvPath != null)
        {
            File.WriteAllText(csvPath, FlightSample.CsvHeader + Environment.NewLine);
        }

        Logs[rocket.ReferenceId] = entry;
        Sample(entry, rocket);
        return entry;
    }

    internal static RocketFlightLogEntry Require(long rocketId) =>
        Logs.TryGetValue(rocketId, out RocketFlightLogEntry entry)
            ? entry
            : throw ApiErrors.Refused("no_flight_log", "This rocket has no flight log: start one with write " +
                "flight_log, action start.");

    internal static RocketFlightLogEntry Stop(long rocketId)
    {
        RocketFlightLogEntry entry = Require(rocketId);
        entry.Recording = false;
        return entry;
    }

    internal static void Clear(long rocketId)
    {
        Require(rocketId);
        Logs.Remove(rocketId);
    }

    /// <summary>The world was left: every flight log was of its rockets (ids repeat across loads).</summary>
    internal static void ClearAll() => Logs.Clear();

    internal static void Tick()
    {
        if (Logs.Count == 0)
        {
            return;
        }

        float now = Time.time;
        foreach (RocketFlightLogEntry entry in Logs.Values)
        {
            if (!entry.Recording || now < entry.NextAt)
            {
                continue;
            }

            entry.NextAt = now + (float)entry.IntervalS;
            Rocket? rocket = Find(entry.RocketId);
            if (rocket == null)
            {
                entry.Recording = false;
                continue;
            }

            try
            {
                Sample(entry, rocket);
            }
            catch (Exception exception)
            {
                entry.Recording = false;
                StationGodMod.LogWarning($"Rocket flight log for {entry.RocketName} stopped: {exception.Message}");
            }
        }
    }

    private static Rocket? Find(long id)
    {
        for (int index = 0; index < Rocket.AllRockets.Count; index++)
        {
            Rocket rocket = Rocket.AllRockets[index];
            if (rocket != null && rocket.ReferenceId == id && !rocket.BeingDestroyed && rocket.RocketNetwork != null)
            {
                return rocket;
            }
        }

        return null;
    }

    private static void Sample(RocketFlightLogEntry entry, Rocket rocket)
    {
        RocketParts parts = RocketParts.Of(rocket);
        double fuel = 0.0;
        double kelvin = 0.0;
        double kpa = 0.0;
        for (int index = 0; index < parts.Lines.Count; index++)
        {
            FuelLineRead line = parts.Lines[index];
            fuel += line.Network.Atmosphere.TotalMoles.ToDouble();
            for (int tank = 0; tank < line.Tanks.Count; tank++)
            {
                fuel += line.Tanks[tank].Atmosphere.TotalMoles.ToDouble();
            }

            if (index == 0)
            {
                Assets.Scripts.Atmospherics.Atmosphere first = line.Tanks.Count > 0
                    ? line.Tanks[0].Atmosphere
                    : line.Network.Atmosphere;
                kelvin = first.Temperature.ToDouble();
                kpa = first.PressureGassesAndLiquids.ToDouble();
            }
        }

        double battery = 0.0;
        for (int index = 0; index < parts.Batteries.Count; index++)
        {
            battery += parts.Batteries[index].PowerStored;
        }

        float confidence = parts.Avionics != null
            ? parts.Avionics.GetAutoLandConfidenceRatio(out _, out _)
            : rocket.GetAutoLandConfidenceRatio(SpaceMap.Current.DistanceToOrbit * 2f, WorldSetting.Current.Gravity,
                RoutePlanner.AltitudeOf(rocket.ReEntryProfile), out _);
        FlightSample sample = new FlightSample(Time.time, rocket.RocketState.ToString(),
            rocket.CurrentNode != null ? SpaceRoutes.NameOf(rocket.CurrentNode) : string.Empty,
            rocket.TargetNode != null ? SpaceRoutes.NameOf(rocket.TargetNode) : string.Empty, rocket.Progress,
            rocket.GetAltitude(), rocket.Velocity, rocket.Acceleration, rocket.TotalMass(), fuel, kelvin, kpa,
            RocketModel.FirstThrottle(parts), rocket.GetThrust(), rocket.MaxRecordedThrust, confidence, battery,
            parts.CargoSlotsFilled, rocket.EstimatedRemainingBurnTimeSeconds, rocket.FlightControlRule.ToString());
        entry.Buffer.Add(sample);
        if (entry.CsvPath != null)
        {
            try
            {
                File.AppendAllText(entry.CsvPath, sample.ToCsv() + Environment.NewLine);
            }
            catch (IOException exception)
            {
                StationGodMod.LogWarning($"Rocket flight log CSV stopped: {exception.Message}");
                entry.CsvPath = null;
            }
        }
    }
}
