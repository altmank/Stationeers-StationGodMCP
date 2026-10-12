#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.Objects.Motherboards;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Controllers;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// driven_by on a logic write: the controllers (Controllers) that write the same logic type on the same device, so a
/// caller learns its value may be written back. Slot writes and mirrors do not count. A write's checks (LogicOps.Check)
/// call For once per item, so a reply and a report (dry_run) carry the same; inside Controllers.Open a batch shares one
/// scan.
/// </summary>
internal static class DrivenBy
{
    /// <summary>The controllers writing this logic type on the device; null when none, or for a worn or held item.</summary>
    internal static List<ControllerView>? For(ScopedTarget device, LogicType type)
    {
        string? name = EnumNames<LogicType>.Of(type);
        if (name == null || !(device.Thing is Device networked))
        {
            return null;
        }

        try
        {
            return ControllerView.ListOrNull(DrivenByMatch.Writing(Controllers.Of(networked), name));
        }
        catch (Exception)
        {
            // A device or network torn down while it was read (DataDeviceList, a holder's pins): driven_by is advice,
            // so the write and its reply go without it rather than failing.
            return null;
        }
    }
}
