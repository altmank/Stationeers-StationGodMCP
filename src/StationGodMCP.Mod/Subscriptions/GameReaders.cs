#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using Assets.Scripts.Objects.Motherboards;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Sampling;
using StationGodMCP.Pure.Subscriptions;

namespace StationGodMCP.Subscriptions;

/// <summary>
/// A devices subscription's sample through read_devices' own read path, so a subscription reads exactly what the same
/// read_devices call would, in the calling frame, from the request parsed once at subscribe; only the gateway is looked
/// up again, since it may be gone. Errors of the whole call (gateway_not_found) throw as they do there.
/// </summary>
internal sealed class ReadDevicesReader : IDeviceReader<ReadDevicesView>
{
    internal static readonly ReadDevicesReader Instance = new ReadDevicesReader();

    private ReadDevicesReader()
    {
    }

    public ReadDevicesView Read(DeviceSubscriptionQuery query) =>
        ReadDevicesApi.Read(Devices.Scope(new Args(query.ReadArguments)), query.Request);
}

/// <summary>
/// A read sample sample: every target read as read logic reads it (the device in the gateway's scope, the logic type
/// readable), "looking_at" the thing under the crosshair. A target that cannot be read is a miss with its error (its
/// reading a failure); a gateway that cannot be found fails the whole read.
/// </summary>
internal sealed class SampleReader : ILogicSampleReader<BatchItemView>
{
    internal static readonly SampleReader Instance = new SampleReader();

    private const string LookingAt = "looking_at";

    private SampleReader()
    {
    }

    public LogicSampleRead Read(SampleRequest request, List<BatchItemView> into)
    {
        DeviceScope scope;
        try
        {
            scope = Devices.Scope(request.Gateway);
        }
        catch (ApiException refused)
        {
            return LogicSampleRead.Failed(refused.Code, refused.Message);
        }

        List<SampleMiss>? misses = null;
        for (int index = 0; index < request.Targets.Count; index++)
        {
            SampleTarget target = request.Targets[index];
            try
            {
                ScopedTarget device = Devices.Require(scope, IdOf(target.Id));
                LogicType type = LogicTypes.Parse(target.Logic);
                into.Add(new LogicReadItemView(index, new LogicRead(new ThingId(device.ReferenceId),
                    LogicTypes.ViewOf(type), LogicOps.ReadValue(device, type))));
            }
            catch (ApiException refused)
            {
                into.Add(new LogicFailedItemView(index, null, null, refused));
                (misses ??= new List<SampleMiss>()).Add(new SampleMiss(index, refused.Code, refused.Message));
            }
        }

        return LogicSampleRead.Read(scope.Id, misses);
    }

    // A target's id: a thing's reference id, or the thing under the crosshair.
    private static ThingId IdOf(string id)
    {
        if (string.Equals(id, LookingAt, System.StringComparison.OrdinalIgnoreCase))
        {
            Assets.Scripts.Objects.Thing? thing = PlayerView.Current().Require("read sample").Target;
            return thing != null
                ? new ThingId(thing.ReferenceId)
                : throw ApiErrors.Refused("thing_not_found", "read sample: nothing is under the crosshair for \"looking_at\".");
        }

        return ThingId.TryRead(new Newtonsoft.Json.Linq.JValue(id), out ThingId read)
            ? read
            : throw ApiErrors.Refused("thing_not_found", $"read sample: '{id}' is not a thing's id.");
    }
}
