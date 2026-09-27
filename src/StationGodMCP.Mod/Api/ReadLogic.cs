#nullable enable

using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api;

/// <summary>
/// read_logic: one logic value of one device, as a chip's l instruction reads it (ILogicable.GetLogicValue).
/// Read only.
/// </summary>
internal static class ReadLogicApi
{
    internal static LogicReadView Handle(Args args)
    {
        DeviceScope scope = Devices.Scope(args);
        return new LogicReadView(scope.Id, LogicOps.Read(scope, args));
    }
}
