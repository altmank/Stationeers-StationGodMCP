#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Api.Views;

/// <summary>
/// Where a refund would go, as the job would deliver it now: refund_to as given (a word, or the list of targets),
/// each target skipped and why, and each part of each item with the target it lands in.
/// </summary>
internal sealed class RefundPlanView
{
    internal RefundPlanView(object refundTo, List<string> skipped, List<RefundDestinationView> destinations)
    {
        RefundTo = refundTo;
        Skipped = skipped;
        Destinations = destinations;
        foreach (RefundDestinationView destination in destinations)
        {
            OnGroundAsFallback += destination.Fallback == true ? destination.Quantity : 0;
        }
    }

    /// <summary>A single word (source, ground, none) or the list of targets in order.</summary>
    public object RefundTo { get; }

    /// <summary>Targets left out because what they need is missing (no player, from_id not a stack).</summary>
    public List<string> Skipped { get; }

    public List<RefundDestinationView> Destinations { get; }

    /// <summary>How many items fit none of the targets and go on the ground in front of the holder.</summary>
    public int OnGroundAsFallback { get; }
}

/// <summary>One part of one item of a refund: how many, into which target, and how (merged, slot, ground).</summary>
internal sealed class RefundDestinationView
{
    internal RefundDestinationView(string? prefabName, int quantity, string target, string where, ThingId? into,
        bool fallback, ThingView? container = null)
    {
        TargetId = container?.ReferenceId;
        TargetName = container?.DisplayName;
        PrefabName = prefabName;
        Quantity = quantity;
        Target = target;
        Where = where;
        Into = into;
        Fallback = fallback ? true : null;
    }

    public string? PrefabName { get; }

    public int Quantity { get; }

    /// <summary>inventory, source, storage, container or ground.</summary>
    public string Target { get; }

    /// <summary>A container target: the container's reference id; left out for the other targets.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ThingId? TargetId { get; }

    /// <summary>A container target: its name.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? TargetName { get; }

    /// <summary>merged (onto the stack into), slot (a new item in an empty slot of into) or ground.</summary>
    public string Where { get; }

    /// <summary>The stack topped up, or the thing whose slot takes the new item; absent on the ground.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ThingId? Into { get; }

    /// <summary>true when no target took it and it goes on the ground in front of the holder.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public bool? Fallback { get; }
}
