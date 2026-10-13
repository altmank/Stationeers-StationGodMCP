#nullable enable

using Assets.Scripts.Objects.Motherboards;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared.Game.Lint;
using StationGodMCP.Api.Views;
using StationGodMCP.Protocol;
using StationGodMCP.Pure.Lint;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// Held restores' devices in the game, on the main thread: devices in the world scope (Devices.Find), logic types as
/// write logic reads them (LogicTypes.Parse), the write through write logic's own game write (LogicOps.SetValue), and
/// the write rules through the checked write's judge (OpeningWrites.Judge).
/// </summary>
internal sealed class RestoreDevices : IRestoreDevices
{
    internal static RestoreDevices Instance { get; } = new RestoreDevices();

    public LogicTypeView LogicType(JToken? token) => LogicTypes.ViewOf(LogicTypes.Parse(token));

    public void RequireHoldable(long referenceId, LogicTypeView type)
    {
        ScopedTarget device = Devices.Require(DeviceScope.World, new ThingId(referenceId));
        LogicTypes.RequireWritable(device, (LogicType)type.Id);
        LogicTypes.RequireReadable(device, (LogicType)type.Id);
    }

    public bool TryRead(long referenceId, ushort type, out double value, out string problem)
    {
        value = 0;
        problem = string.Empty;
        ScopedTarget? device = Devices.Find(DeviceScope.World, referenceId);
        if (device == null)
        {
            problem = "device_not_found";
            return false;
        }

        try
        {
            value = LogicOps.ReadValue(device, (LogicType)type);
            return true;
        }
        catch (ApiException refused)
        {
            problem = refused.Code;
            return false;
        }
    }

    public bool TryWrite(long referenceId, ushort type, double value, out string problem)
    {
        problem = string.Empty;
        ScopedTarget? device = Devices.Find(DeviceScope.World, referenceId);
        if (device == null)
        {
            problem = "device_not_found";
            return false;
        }

        try
        {
            LogicOps.SetValue(device, (LogicType)type, value);
            return true;
        }
        catch (ApiException refused)
        {
            problem = refused.Code;
            return false;
        }
    }

    public OpeningVerdict Judge(long referenceId, ushort type, double previous, double value)
    {
        ScopedTarget? device = Devices.Find(DeviceScope.World, referenceId);
        return device == null ? OpeningVerdict.Closed : OpeningWrites.Judge(device, (LogicType)type, previous, value);
    }
}
