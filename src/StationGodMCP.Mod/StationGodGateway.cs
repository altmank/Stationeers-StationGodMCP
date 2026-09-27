#nullable enable

using System.Collections.Generic;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects.Electrical;
using StationGodMCP.Api.Shared.Game;

namespace StationGodMCP;

/// <summary>
/// The StationGod Gateway structure (PrefabRegistrar): a Logic Memory whose data networks can narrow a device call's
/// scope (gateway_id). It registers itself in GatewayRegistry while it is on the grid.
/// </summary>
public sealed class StationGodGateway : LogicMemory
{
    internal bool IsAvailable => GetUnavailableReason() == null;

    /// <summary>incomplete, no_data_network, or null when the gateway can scope a call.</summary>
    internal string? GetUnavailableReason()
    {
        if (!IsStructureCompleted)
        {
            return "incomplete";
        }

        if (InputNetwork1 == null && OutputNetwork1 == null)
        {
            return "no_data_network";
        }

        return null;
    }

    /// <summary>Its data networks (input, then output when that is another network).</summary>
    internal List<CableNetwork> GetDataNetworks()
    {
        List<CableNetwork> networks = new List<CableNetwork>(2);
        CableNetwork? first = InputNetwork1;
        CableNetwork? second = OutputNetwork1;
        if (first != null)
        {
            networks.Add(first);
        }

        if (second != null && !ReferenceEquals(second, first))
        {
            networks.Add(second);
        }

        return networks;
    }

    public override void OnRegistered(Cell cell)
    {
        base.OnRegistered(cell);
        GatewayRegistry.Register(this);
    }

    public override void OnDeregistered()
    {
        GatewayRegistry.Unregister(this);
        base.OnDeregistered();
    }

    public override void OnFinishedLoad()
    {
        base.OnFinishedLoad();
        GatewayRegistry.Register(this);
    }

    public override void OnDestroy()
    {
        GatewayRegistry.Unregister(this);
        base.OnDestroy();
    }
}
