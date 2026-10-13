#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// write memory (catalogue write.json): values written into a device's memory from start, as a chip's put instruction
/// writes them (DeviceMemory), in the gateway's scope, with the memory read before and after when it also reads. One
/// id, or "looking_at". A cheat: without confirm the call reports the write from the same checks and writes nothing.
/// </summary>
internal static class WriteMemory
{
    internal static VerbOutcome Run(WriteMemoryOptions options)
    {
        string label = WriteMemoryOptions.Spec.Label;
        List<OptionProblem> problems = new List<OptionProblem>();
        int? start = MemoryWrites.Start(options.Start, problems);
        List<double>? values = MemoryWrites.Values(options.Values, problems);
        if (problems.Count > 0)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(label, problems));
        }

        string id = options.Selector.Ids!.Ids[0];
        DeviceScope scope = Devices.Scope(options.Gateway);
        return WriteCheck<MemoryWritePlan>.Run(() =>
                DeviceMemory.Check(scope, TargetIds.Of(id, label), start!.Value, values!))
            .Outcome(options.Report,
                plan => new VerbOutcome.Answered(MemoryWrites.Written(plan.Place, plan.Values, plan.Previous,
                    DeviceMemory.Write(plan))),
                plan => MemoryWrites.Reported(plan.Place, plan.Name, plan.Values, plan.Previous));
    }
}
