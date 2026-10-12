#nullable enable

using Assets.Scripts.Objects.Motherboards;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Views;
using StationGodMCP.Protocol;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// Held restores' devices in the game, on the main thread: devices in the world scope (Devices.Find), logic types as
/// write_logic reads them (LogicTypes.Parse), and the write through write_logic's own game write (LogicOps.SetValue).
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
}
