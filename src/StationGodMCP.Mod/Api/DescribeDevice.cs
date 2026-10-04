#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects.Motherboards;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Build;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api;

/// <summary>
/// describe_device: a device and every logic type it reads or writes (ILogicable.CanLogicRead, CanLogicWrite), each
/// LogicType value once; its rocket, an umbilical's pairing, and a Logic Rocket Uplink's downlink (DataLinks). Read
/// only.
/// </summary>
internal static class DescribeDeviceApi
{
    internal static DescribeDeviceView Handle(Args args)
    {
        DeviceScope scope = Devices.Scope(args);
        ScopedTarget device = Devices.Require(scope, args.ThingId("reference_id"));
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

        return new DescribeDeviceView(Devices.ViewOf(device, scope), types, RocketReadings.PartOf(device.Thing),
            RocketReadings.UmbilicalOf(device.Thing), DataLinks.UplinkOf(device.Thing), BuildStates.Of(device.Thing));
    }
}
