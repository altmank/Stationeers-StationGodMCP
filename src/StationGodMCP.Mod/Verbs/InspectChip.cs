#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// inspect chip (catalogue inspect.json): a circuit holder's chip in the gateway's scope, by id or "looking_at"
/// (ChipReads), in parts: status (start, count: the stack window; log: Lua log lines), source, selectors (targets: the
/// devices to evaluate; default those on the holder's batch list). Every part asked is read first (PartAnswers): one
/// answers its reply as it is, several {status, source, selectors}; any that cannot be answered refuses the call with
/// all of them listed. Read only.
/// </summary>
internal static class InspectChip
{
    internal static VerbOutcome Run(InspectChipOptions options)
    {
        string label = InspectChipOptions.Spec.Label;
        DeviceScope scope = Devices.Scope(options.Gateway);
        IReadOnlyList<string> parts = options.Parts!;
        int start = (int)System.Math.Min(options.Start!.Value, int.MaxValue);
        int count = (int)options.Count!.Value;
        int log = (int)options.Log!.Value;
        IcTarget ic = CircuitHolders.Require(scope, TargetIds.Of(options.Selector.Ids!.Ids[0], label));
        return PartAnswers.Of(label, parts, part => part switch
        {
            "source" => ChipReads.Source(scope, ic),
            "selectors" => ChipReads.Selectors(scope, ic, Targets(options.Targets, label)),
            _ => ChipReads.Status(scope, ic, start, count, log)
        });
    }

    private static List<ThingId>? Targets(IReadOnlyList<string>? targets, string label)
    {
        if (targets == null)
        {
            return null;
        }

        List<ThingId> ids = new List<ThingId>(targets.Count);
        foreach (string target in targets)
        {
            ids.Add(TargetIds.Of(target, label));
        }

        return ids;
    }
}
