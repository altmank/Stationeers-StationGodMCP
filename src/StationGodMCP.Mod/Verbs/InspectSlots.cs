#nullable enable

using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// inspect slots (catalogue inspect.json): a device's logical slots in the gateway's scope (DeviceSlots), what is in
/// each and every slot logic type it reads there; slot keeps one. One id, or a list of ids (one row each). Read only.
/// </summary>
internal static class InspectSlots
{
    internal static VerbOutcome Run(InspectSlotsOptions options)
    {
        DeviceScope scope = Devices.Scope(options.Gateway);
        int? slot = options.Slot is { } index ? (int)System.Math.Min(index, int.MaxValue) : null;
        return IdAnswers.Things(options.Selector.Ids!, InspectSlotsOptions.Spec.Label,
            id => DeviceSlots.Read(scope, Devices.Require(scope, id), slot));
    }
}
