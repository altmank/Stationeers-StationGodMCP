#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using Assets.Scripts.Objects.Motherboards;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Sampling;
using StationGodMCP.Pure.Subscriptions;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Subscriptions;

/// <summary>
/// A devices subscription's sample through read logic's own read path (DeviceReads), so a subscription reads exactly
/// what the same read would, in the calling frame, from the request parsed once at subscribe; only the gateway is
/// looked up again, since it may be gone. Errors of the whole read (gateway_not_found) throw as they do there.
/// </summary>
internal sealed class ReadDevicesReader : IDeviceReader<ReadDevicesView>
{
    internal static readonly ReadDevicesReader Instance = new ReadDevicesReader();

    private ReadDevicesReader()
    {
    }

    public ReadDevicesView Read(DeviceSubscriptionQuery query) =>
        DeviceReads.Read(Devices.Scope(query.GatewayId), query.Request, Label);

    private const string Label = "subscribe";
}

/// <summary>
/// A read sample sample: every target read as read logic reads it (the device in the gateway's scope, the logic type
/// readable), "looking_at" the thing under the crosshair. A target that cannot be read is a miss with its error (its
/// reading a failure); a gateway that cannot be found fails the whole read.
/// </summary>
internal sealed class SampleReader : ILogicSampleReader<BatchItemView>
{
    internal static readonly SampleReader Instance = new SampleReader();

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
                into.Add(new LogicReadItemView(index,
                    LogicOps.Read(scope, TargetIds.Of(target.Id, ReadSampleOptions.Spec.Label), target.Logic)));
            }
            catch (ApiException refused)
            {
                into.Add(new LogicFailedItemView(index, null, null, refused));
                (misses ??= new List<SampleMiss>()).Add(new SampleMiss(index, refused.Code, refused.Message));
            }
        }

        return LogicSampleRead.Read(scope.Id, misses);
    }
}
