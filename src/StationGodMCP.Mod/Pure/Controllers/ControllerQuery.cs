#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure.Controllers;

/// <summary>
/// What inspect_controllers asks about: one device (reference_id), a list of devices (reference_ids), or every data
/// device of one cable network (network_id). Exactly one is set.
/// </summary>
internal sealed class ControllerQuery
{
    private ControllerQuery(List<long>? ids, bool single, long? networkId)
    {
        Ids = ids;
        Single = single;
        NetworkId = networkId;
    }

    /// <summary>The devices asked for, in order; null for a network.</summary>
    internal List<long>? Ids { get; }

    /// <summary>Asked as reference_id: a device that is not found refuses the call instead of being listed.</summary>
    internal bool Single { get; }

    internal long? NetworkId { get; }

    internal static ControllerQuery Device(long id) => new ControllerQuery(new List<long>(1) { id }, true, null);

    internal static ControllerQuery Devices(List<long> ids) => new ControllerQuery(ids, false, null);

    internal static ControllerQuery Network(long id) => new ControllerQuery(null, false, id);
}
