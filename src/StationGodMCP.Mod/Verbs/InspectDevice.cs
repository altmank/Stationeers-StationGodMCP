#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects.Motherboards;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Build;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Profiling;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// inspect device (catalogue inspect.json): a device in the gateway's scope, how many logic types it reads or writes
/// (ILogicable.CanLogicRead, CanLogicWrite), each LogicType once, the names of the writable ones, and with include
/// logic_types every one with its access; its rocket, an umbilical's pairing, a Logic Rocket Uplink's downlink
/// (DataLinks) and its build state. One id, or a list of ids (one row each). Read only.
/// </summary>
[ReplyFact(FactRule.Live, "the thing's values as the game holds them now")]
internal static class InspectDevice
{
    private const string LogicTypesPart = "logic_types";

    [Profiled]
    internal static VerbOutcome Run(InspectDeviceOptions options)
    {
        DeviceScope scope = Devices.Scope(options.Gateway);
        bool listed = options.Fields.Includes(LogicTypesPart);
        return IdAnswers.Things(options.Selector.Ids!, InspectDeviceOptions.Spec.Label,
            id => IdAnswers.Device(scope, id, device => Describe(scope, device, listed)));
    }

    [Profiled]
    private static DescribeDeviceView Describe(DeviceScope scope, ScopedTarget device, bool listed)
    {
        List<LogicAccessView> types = new List<LogicAccessView>();
        foreach (LogicType type in LogicTypes.Distinct)
        {
            bool readable = LogicTypes.CanRead(device, type);
            bool writable = LogicTypes.CanWrite(device, type);
            if (readable || writable)
            {
                types.Add(new LogicAccessView(LogicTypes.ViewOf(type), readable, writable));
            }
        }

        return new DescribeDeviceView(Devices.ViewOf(device, scope), types, listed, RocketReadings.PartOf(device.Thing),
            RocketReadings.UmbilicalOf(device.Thing), DataLinks.UplinkOf(device.Thing), BuildStates.Of(device.Thing));
    }
}
