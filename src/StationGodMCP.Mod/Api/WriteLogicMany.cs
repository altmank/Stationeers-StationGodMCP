#nullable enable

using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api;

/// <summary>
/// write_logic_many: up to 256 logic writes in order, each as write_logic, each failing alone. Writes.
/// </summary>
internal static class WriteLogicManyApi
{
    private const int MaximumWrites = 256;

    internal static LogicBatchView Handle(Args args)
    {
        DeviceScope scope = Devices.Scope(args);
        return new LogicBatchView(scope.Id, LogicOps.WriteMany(scope, args.Objects("writes", MaximumWrites)));
    }
}
