using System.Text.Json.Nodes;

namespace StationGodMCP.Server;

/// <summary>
/// What one call of a run carries through the pipeline to its gate and back: the code line, the group its writes act
/// in, whether it is a hold's restore, and what the gate decided (the write entry it opened, or the row it refused).
/// </summary>
internal sealed class RunCallContext
{
    internal RunCallContext(int? line, WriteGroup group = WriteGroup.Done, bool holdExit = false)
    {
        Line = line;
        Group = group;
        HoldExit = holdExit;
    }

    internal int? Line { get; }

    internal WriteGroup Group { get; }

    /// <summary>A hold's restore: it acts as CLEANUP after a stop (not after a world change).</summary>
    internal bool HoldExit { get; }

    /// <summary>
    /// The values read before a hold's restore write, by (id, logic) key: the before of its targets. Every other
    /// write's before values come from the write reply's rows (previous_value).
    /// </summary>
    internal Dictionary<string, JsonNode?>? KnownBefore { get; init; }

    /// <summary>The write the gate let act, in the run's log; null when the call wrote nothing that acted.</summary>
    internal WriteEntry? Pending { get; set; }

    /// <summary>The error row the gate answered instead of acting (after a stop).</summary>
    internal JsonObject? Refused { get; set; }

    /// <summary>True when the gate stopped the run on this call (the stop is recorded already).</summary>
    internal bool RuleStop { get; set; }

    /// <summary>
    /// A write the gate let act that the game answered with its report instead (a check refused it, so nothing
    /// acted): the error the script gets in place of that report.
    /// </summary>
    internal VerbReply.Failed? ActRefused { get; set; }
}

/// <summary>
/// A run's write gate for one call (hub.md, Routes): the run decides, the context remembers. One per call, so writes
/// from a run's threads never mix up their entries.
/// </summary>
internal sealed class RunGate(Run run, RunCallContext context) : IWriteGate
{
    public WriteDecision Decide(VerbCall call, WriteTargets targets) => run.Decide(call, targets, context);
}
