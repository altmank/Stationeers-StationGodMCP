#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// write chip (catalogue write.json): a circuit holder's chip, in the gateway's scope, by id or "looking_at": code
/// writes its source (IC10, or Lua for a Lua chip), pins sets an IC Housing's d0..d5, run pauses, steps, resumes or
/// restarts it (ChipWrites). Given together, every part is checked before any acts, and a refused part refuses the
/// call with every refused part at once; then they act in that order (WriteParts): one part answers its reply, several
/// {code, pins, run}, a refusal while acting stopping the rest, named in not_run. include code echoes the stored source;
/// allow off_network pins a device off the housing's data network.
/// </summary>
[ReplyFact(FactRule.Live, "what it changes, read and checked when it acts")]
internal static class WriteChip
{
    private const string CodePart = "code";
    private const string PinsPart = "pins";
    private const string RunPart = "run";

    internal static VerbOutcome Run(WriteChipOptions options)
    {
        string label = WriteChipOptions.Spec.Label;
        List<string> parts = new List<string>(3);
        List<OptionProblem> problems = new List<OptionProblem>();
        if (options.Code != null)
        {
            parts.Add(CodePart);
        }

        if (options.Pins != null)
        {
            parts.Add(PinsPart);
            if (options.Pins is not JObject { Count: > 0 })
            {
                problems.Add(new OptionProblem(PinsPart, "names at least one pin: {d0..d5: device id or null}"));
            }
        }

        if (options.Run != null)
        {
            parts.Add(RunPart);
        }

        if (parts.Count == 0)
        {
            problems.Add(new OptionProblem(CodePart, "code, pins or run is needed"));
        }

        if (problems.Count > 0)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(label, problems));
        }

        DeviceScope scope = Devices.Scope(options.Gateway);
        IcTarget ic = CircuitHolders.Require(scope, TargetIds.Of(options.Selector.Ids!.Ids[0], label));
        bool echo = options.Fields.Includes(CodePart);
        bool offNetwork = Allow.Lifts(options.Allow, "off_network");
        bool sourceFirst = options.Code != null;
        return WriteParts.Of(label, parts, part => part switch
        {
            CodePart => ChipWrites.Source(scope, ic, options.Code!, echo),
            PinsPart => ChipWrites.Pins(scope, ic, (JObject)options.Pins!, offNetwork),
            _ => (Func<object>)ChipWrites.Run(scope, ic, options.Run!, sourceFirst)
        });
    }
}
