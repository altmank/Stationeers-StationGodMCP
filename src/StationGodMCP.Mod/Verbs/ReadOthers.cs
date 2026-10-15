#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects.Pipes;
using Assets.Scripts.Objects.Motherboards;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Flight;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// read memory (catalogue read.json): consecutive values of a device's memory (IMemoryReadable.ReadMemory: a chip's
/// stack, a Logic Sorter's, a satellite dish's) from start, count of them, as a chip's get instruction reads them
/// (DeviceMemory). The device in the gateway's scope, by id or "looking_at"; a list of ids answers one row each. Read
/// only.
/// </summary>
[ReplyFact(FactRule.Live, "memory values")]
internal static class ReadMemory
{
    internal static VerbOutcome Run(ReadMemoryOptions options)
    {
        DeviceScope scope = Devices.Scope(options.Gateway);
        int start = (int)System.Math.Min(options.Start!.Value, int.MaxValue);
        int count = (int)options.Count!.Value;
        return IdAnswers.Things(options.Selector.Ids!, ReadMemoryOptions.Spec.Label, id => IdAnswers.Device(scope, id,
            device =>
            {
                if (device.Thing is not IMemoryReadable memory)
                {
                    return ApiErrors.Refused("memory_not_readable",
                        $"Device {device.ReferenceId} does not expose readable memory.");
                }

                DeviceMemory.RequireRange(device, start, count);
                return new MemoryReadView(DeviceMemory.Place(scope, device, start),
                    DeviceMemory.Read(memory, start, count));
            }));
    }
}

/// <summary>
/// read snapshot (catalogue read.json): the logic values of every device in the gateway's scope kept by id, prefab and
/// name, up to limit by reference id: the logic types listed, or every type each reads less the gas and liquid ratios
/// reading 0 (include zero_ratios keeps them) (DeviceSnapshots); {gateway_id, devices, count, matched_count,
/// truncated}. Read only.
/// </summary>
[ReplyFact(FactRule.Followed, "which things exist, by class and prefab", Keeper = typeof(StationGodMCP.WorldModel.ThingIndex))]
[ReplyFact(FactRule.Live, "logic values")]
internal static class ReadSnapshot
{
    internal static VerbOutcome Run(ReadSnapshotOptions options)
    {
        Selector selector = options.Selector;
        HashSet<long>? ids = null;
        if (selector.Ids is { } given)
        {
            ids = new HashSet<long>();
            foreach (string id in given.Ids)
            {
                ids.Add(TargetIds.Of(id, ReadSnapshotOptions.Spec.Label).Value);
            }
        }

        DeviceScope scope = Devices.Scope(options.Gateway);
        JArray? logic = options.Logic as JArray;
        bool zeroRatios = options.Fields.Includes("zero_ratios");
        int maximum = options.Paging.Limit ?? int.MaxValue;
        return WorkResult<NetworkSnapshotView>.Of(answer => DeviceSnapshots.Reading(scope, selector.Prefab,
            selector.Name, ids, logic, zeroRatios, maximum, answer), snapshot => Answer.Of(snapshot));
    }
}

/// <summary>
/// read flight_log (catalogue read.json): action read, one rocket's recording (FlightLogs) with every n-th of its
/// samples from offset, at most limit (samples are no things a cursor keeps, so they page by offset only); action list,
/// every recording without its rows. id names the rocket by its id, name, network or any part; left out, the only
/// rocket. Read only.
/// </summary>
[ReplyFact(FactRule.Live, "the mod's flight log records")]
internal static class ReadFlightLog
{
    internal static VerbOutcome Run(ReadFlightLogOptions options)
    {
        if (options.Action == "list")
        {
            return Answer.Of(FlightLogs.List());
        }

        Paging paging = options.Paging;
        int named = options.Selector.Ids?.Ids.Count ?? 1;
        if (KeyedPage.OneThing(paging, named) is { } problem)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(ReadFlightLogOptions.Spec.Label, new[] { problem }));
        }

        WalkRequest walk = KeyedPage.WalkOf(paging, named);
        int every = (int)options.Every!.Value;
        if (options.Selector.Ids is not { } ids)
        {
            return Answer.Of(Read(RocketLocator.One((string?)null), walk, paging, every));
        }

        return IdAnswers.Of(ids, id =>
        {
            Objects.Rockets.Rocket rocket = RocketLocator.Find(id);
            return (rocket.ReferenceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Read(rocket, walk, paging, every));
        });
    }

    private static FlightLogView Read(Objects.Rockets.Rocket rocket, WalkRequest walk, Paging paging, int every) =>
        FlightLogs.View(RocketFlightRecorder.Require(rocket.ReferenceId), walk, paging.Offset,
            paging.Limit ?? int.MaxValue, every);
}
