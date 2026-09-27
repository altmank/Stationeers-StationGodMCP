#nullable enable

using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api;

/// <summary>
/// read_logic_many: up to 256 logic reads in order, each as read_logic, each failing alone. Read only.
/// </summary>
internal static class ReadLogicManyApi
{
    private const int MaximumReads = 256;

    internal static LogicBatchView Handle(Args args)
    {
        DeviceScope scope = Devices.Scope(args);
        return new LogicBatchView(scope.Id, LogicOps.ReadMany(scope, args.Objects("reads", MaximumReads)));
    }
}
