#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Sampling;
using StationGodMCP.Pure.Subscriptions;

namespace StationGodMCP.Subscriptions;

/// <summary>
/// A devices subscription's sample through read_devices' own handler, so a subscription reads exactly what the same
/// read_devices call would, in the calling frame. Errors of the whole call (gateway_not_found) throw as they do there.
/// </summary>
internal sealed class ReadDevicesReader : IDeviceReader<ReadDevicesView>
{
    internal static readonly ReadDevicesReader Instance = new ReadDevicesReader();

    private ReadDevicesReader()
    {
    }

    public ReadDevicesView Read(DeviceSubscriptionQuery query) => ReadDevicesApi.Handle(new Args(query.ReadArguments));
}

/// <summary>A sample_logic sample through read_logic_many's own handler: each target read as read_logic reads it.</summary>
internal sealed class ReadLogicManyReader : ILogicSampleReader<BatchItemView>
{
    internal static readonly ReadLogicManyReader Instance = new ReadLogicManyReader();

    private ReadLogicManyReader()
    {
    }

    public LogicSampleRead Read(SampleLogicArguments arguments, List<BatchItemView> into)
    {
        try
        {
            LogicBatchView batch = ReadLogicManyApi.Handle(new Args(arguments.ReadArguments()));
            into.AddRange(batch.Results);
            return LogicSampleRead.Read(batch.GatewayId);
        }
        catch (ApiException refused)
        {
            return LogicSampleRead.Failed(refused.Code, refused.Message);
        }
    }
}
