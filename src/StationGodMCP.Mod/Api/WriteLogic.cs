#nullable enable

using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api;

/// <summary>
/// write_logic: one logic value written to one device, as a chip's s instruction writes it
/// (ILogicable.SetLogicValue), with the value read before and after when the type also reads. Writes.
/// </summary>
internal static class WriteLogicApi
{
    internal static LogicWriteView Handle(Args args)
    {
        DeviceScope scope = Devices.Scope(args);
        return new LogicWriteView(scope.Id, LogicOps.Write(scope, args));
    }
}
