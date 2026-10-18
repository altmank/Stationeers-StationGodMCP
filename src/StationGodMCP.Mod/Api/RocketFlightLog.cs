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
using World = StationGodMCP.WorldModel.World;

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

    internal static FlightLogView Stop(RocketFlightLogEntry entry) => Shown(RocketFlightRecorder.Stop(entry.RocketId));

    internal static FlightLogClearedView Clear(RocketFlightLogEntry entry)
    {
        RocketFlightRecorder.Clear(entry.RocketId);
        return new FlightLogClearedView(true);
    }

    private static FlightLogView Shown(RocketFlightLogEntry entry)
    {
        FlightLogView view = FlightLogs.View(entry, WalkRequest.None, 0, ShownRows, 1);
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
            logs.Add(View(entry, WalkRequest.None, 0, 0, 1));
        }

        return new FlightLogListView(logs);
    }

    /// <summary>
    /// One recording with every n-th of its samples from offset, at most limit (KeyedPage: a row's key is its sample's
    /// sequence number); a walk's later page takes the rows at its kept sequence numbers that are still held.
    /// </summary>
    internal static FlightLogView View(RocketFlightLogEntry entry, WalkRequest walk, int offset, int limit, int every)
    {
        FlightLogBuffer buffer = entry.Buffer;
        Func<long, FlightSampleView?> describe = sequence =>
            buffer.At(sequence) is { } sample ? new FlightSampleView(sample) : null;
        Slice<FlightSampleView> rows = walk.Continues
            ? KeyedPage.Later(walk, limit, describe)
            : KeyedPage.First(walk, buffer.Sequences(offset, every), 0, limit, describe);
        return new FlightLogView(new ThingId(entry.RocketId), entry.RocketName, entry.Recording, entry.IntervalS,
            entry.CsvPath, buffer, walk.Continues ? rows : Slice<FlightSampleView>.Walked(rows.Items, offset,
                rows.Limit, rows.Total, rows.HasMore, rows.CursorId), every);
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
/// The recorders, ticked from StationGodMod.Update. A sample reads the rocket as inspect rocket does (main thread,
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

    /// <summary>
    /// The log the call names (FlightLogPick: by rocket id or name, or the only one), the rocket gone or not; else the
    /// log of the live rocket the id names (RocketLocator), refused when it has none.
    /// </summary>
    internal static RocketFlightLogEntry Named(string? id)
    {
        List<RocketFlightLogEntry> entries = new List<RocketFlightLogEntry>(Logs.Values);
        List<(long RocketId, string RocketName)> keys = new List<(long RocketId, string RocketName)>(entries.Count);
        foreach (RocketFlightLogEntry entry in entries)
        {
            keys.Add((entry.RocketId, entry.RocketName));
        }

        return FlightLogPick.Of(keys, id) is { } index
            ? entries[index]
            : Require(RocketLocator.One(id).ReferenceId);
    }

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
        for (int index = 0; index < World.Rockets.Count; index++)
        {
            Rocket rocket = World.Rockets[index];
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
