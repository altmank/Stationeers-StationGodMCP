using System.Text.Json.Nodes;

namespace StationGodMCP.Server;

/// <summary>How a run ended, as the hub records it (its own record decides, not the worker's word).</summary>
public enum RunState
{
    Running,
    Done,

    /// <summary>A report run that ended (done, or its pass ended): nothing changed.</summary>
    Report,
    Stopped,
    Failed,
    Killed
}

/// <summary>Why a write in the log acted: in the run's body, in a hold, or as cleanup after a stop.</summary>
public enum WriteGroup
{
    Done,
    Held,
    Cleanup
}

/// <summary>One target of a write that acted: the id (or cell), the logic type or field, and its values.</summary>
public sealed class WrittenTarget
{
    public WrittenTarget(string id, string? field, JsonNode? next)
    {
        Id = id;
        Field = field;
        New = next;
    }

    public string Id { get; }

    /// <summary>The logic type for a logic write; the field written for others (label, color); null when none.</summary>
    public string? Field { get; }

    /// <summary>The value before the write: the handler's previous_value, a hold's pre-read, or a report row's current.</summary>
    public JsonNode? Before { get; set; }

    public JsonNode? New { get; }

    /// <summary>The value read back after the write (current_value); null when the handler gives none.</summary>
    public JsonNode? After { get; set; }

    /// <summary>False when the handler refused this target although the call went through.</summary>
    public bool Acted { get; set; } = true;
}

/// <summary>
/// One write of a run, in the order sent: what it was, its targets, its undo class and group, the code line. It is
/// handed to the journal before it is sent (Acted unknown) and again after its reply.
/// </summary>
public sealed class WriteEntry
{
    public WriteEntry(int sequence, string verb, string? what, List<WrittenTarget> targets, string undo, string undoMark,
        WriteGroup group, int? line)
    {
        Sequence = sequence;
        Verb = verb;
        What = what;
        Targets = targets;
        Undo = undo;
        UndoMark = undoMark;
        Group = group;
        Line = line;
    }

    /// <summary>The write's number in its run, from 1.</summary>
    public int Sequence { get; }

    public string Verb { get; }

    public string? What { get; }

    public List<WrittenTarget> Targets { get; }

    /// <summary>The what's undo class (x-undo): undo, cannot, mixed.</summary>
    public string Undo { get; }

    public string UndoMark { get; }

    public WriteGroup Group { get; }

    public int? Line { get; }

    /// <summary>True once its reply said it acted (or may have: MaybeRan).</summary>
    public bool Acted { get; set; }

    public bool MaybeRan { get; set; }

    /// <summary>"write logic": the verb and what as the footer names them.</summary>
    public string Label => What == null ? Verb : Verb + " " + What;
}

/// <summary>A report row a run listed, or a write it refused after a stop, with the call it came from.</summary>
public sealed class ListedRow
{
    public ListedRow(string label, string undoMark, JsonObject row)
    {
        Label = label;
        UndoMark = undoMark;
        Row = row;
    }

    public string Label { get; }

    public string UndoMark { get; }

    public JsonObject Row { get; }
}

/// <summary>
/// The write-ahead hook (the journal, hub.md): Before is called just before a write is sent, After once its reply is
/// in, NotSent when a write the journal was told of never went (or was cancelled unrun); a run's start, its held
/// restores and its end, and a world change that dropped live runs' holds.
/// </summary>
public interface IRunJournal
{
    void Before(RunRecord run, WriteEntry write);

    void After(RunRecord run, WriteEntry write);

    void NotSent(RunRecord run, WriteEntry write);

    void Started(RunRecord run);

    void Held(RunRecord run, string id, string logic, JsonNode? after);

    /// <summary>A hold's registration is gone (cleared, restored, or never made): the next hub need not report it.</summary>
    void HoldCleared(RunRecord run, string id, string logic);

    void Ended(RunRecord run);

    void WorldChanged(string? world, List<string> holdsDropped);

    /// <summary>True when the journal wants a confirmed logic write's before values ahead of the call line.</summary>
    bool WantsBefore { get; }
}

/// <summary>A logic target a stopped or killed run left changed: its value now, and the value before the run.</summary>
public sealed class LeftTarget
{
    public LeftTarget(string id, string logic, JsonNode? value, JsonNode? before)
    {
        Id = id;
        Logic = logic;
        Value = value;
        Before = before;
    }

    public string Id { get; }

    public string Logic { get; }

    public JsonNode? Value { get; }

    public JsonNode? Before { get; }

    /// <summary>"122643 Open=1 (was 0)".</summary>
    public override string ToString() => $"{Id} {Logic}={Run.Shown(Value)} (was {Run.Shown(Before)})";
}

/// <summary>No journal: the writes are only kept in memory, on the run's record.</summary>
public sealed class NoRunJournal : IRunJournal
{
    public static readonly NoRunJournal Instance = new();

    private NoRunJournal()
    {
    }

    public void Before(RunRecord run, WriteEntry write)
    {
    }

    public void After(RunRecord run, WriteEntry write)
    {
    }

    public void NotSent(RunRecord run, WriteEntry write)
    {
    }

    public void Started(RunRecord run)
    {
    }

    public void Held(RunRecord run, string id, string logic, JsonNode? after)
    {
    }

    public void HoldCleared(RunRecord run, string id, string logic)
    {
    }

    public void Ended(RunRecord run)
    {
    }

    public void WorldChanged(string? world, List<string> holdsDropped)
    {
    }

    public bool WantsBefore => false;
}

/// <summary>
/// A run of a saved script: its name, the version it ran (the journal records it), and the arguments its main(...)
/// got. Null on a run of inline code.
/// </summary>
public sealed record RunScript(string Name, int Version, JsonObject? Args);

/// <summary>
/// What a run did, as the hub keeps it for its reply, its notices and (C5) its journal and undo: the code, the route,
/// the end state, the writes that acted in order, the rows it reported or had refused, and what it printed and
/// returned. Lists are read under Sync.
/// </summary>
public sealed class RunRecord
{
    public RunRecord(string id, string code, bool confirm, long stream, string? world, RunScript? script = null)
    {
        Id = id;
        Code = code;
        Confirm = confirm;
        Stream = stream;
        World = world;
        Script = script;
        FirstLine = script != null ? script.Name + " v" + script.Version : FirstCodeLine(code);
        Started = DateTime.UtcNow;
    }

    /// <summary>The lock the lists and states below are read and changed under.</summary>
    public object Sync { get; } = new();

    public string Id { get; }

    public string Code { get; }

    /// <summary>The saved script the run came from (its name, version and main(...) arguments); null for inline code.</summary>
    public RunScript? Script { get; }

    /// <summary>The first non-blank code line (a saved script's name and version), as notices and cost lines name the run.</summary>
    public string FirstLine { get; }

    public bool Confirm { get; }

    public long Stream { get; }

    /// <summary>The world the run started in.</summary>
    public string? World { get; }

    public DateTime Started { get; }

    public DateTime? Ended { get; internal set; }

    public RunState State { get; internal set; } = RunState.Running;

    /// <summary>The stop that ended the run: its code and message (null when none was delivered).</summary>
    public string? StopCode { get; internal set; }

    public string? StopMessage { get; internal set; }

    /// <summary>A write-rule stop's refused targets as rows, and its fix.</summary>
    public JsonArray? StopRows { get; internal set; }

    public string? StopFix { get; internal set; }

    /// <summary>True when the world changed under the run (no cleanup acted).</summary>
    public bool WorldChanged { get; internal set; }

    /// <summary>The line a report pass ended at, when it did.</summary>
    public int? PassEndLine { get; internal set; }

    public JsonNode? Returned { get; internal set; }

    public string Printed { get; internal set; } = string.Empty;

    /// <summary>The failed-run text from the worker, or the hub's own (a worker that did not start).</summary>
    public string? Error { get; internal set; }

    /// <summary>Every write that acted (or may have), in the order sent.</summary>
    public List<WriteEntry> Writes { get; } = [];

    public List<ListedRow> Reported { get; } = [];

    public List<ListedRow> Refused { get; } = [];

    public List<string> Warnings { get; } = [];

    public List<string> Passed { get; } = [];

    public List<string> Contested { get; } = [];

    /// <summary>After a stop or kill: per target, the value it holds now that differs from the one before the run.</summary>
    public List<LeftTarget> Left { get; } = [];

    /// <summary>
    /// True once undo has reverted every target it can (or found it cannot): a further undo answers already_undone. A
    /// target an undo left (changed since the run, no overwrite) keeps the run undoable for it.
    /// </summary>
    public bool Undone { get; internal set; }

    /// <summary>True while an undo of the run is going: a second one at the same time answers undo_running.</summary>
    internal bool UndoRunning { get; set; }

    /// <summary>The (id, logic) targets undo has dealt with: reverted, already back, or with no value to go back to.</summary>
    internal HashSet<string> UndoneTargets { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Set when the hub let go of the run's write log to stay within its memory bound: what undo can no longer revert.
    /// </summary>
    public string? Forgotten { get; internal set; }

    /// <summary>For a run adopted from an earlier hub's journal: what its held restores did ("applied ..., skipped ...").</summary>
    public string? Restores { get; internal set; }

    /// <summary>True for a run of an earlier hub, read from its journal.</summary>
    public bool Adopted { get; internal set; }

    /// <summary>The sg calls the worker made.</summary>
    public int Calls { get; internal set; }

    /// <summary>The run's stream costs, read at its end (null until then, or when the game did not answer).</summary>
    public Client.StreamCost? Cost { get; internal set; }

    public bool IsLive
    {
        get
        {
            lock (Sync)
            {
                return State == RunState.Running;
            }
        }
    }

    internal static string FirstCodeLine(string code)
    {
        foreach (string line in code.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed.Length <= 60 ? trimmed : trimmed[..57] + "...";
            }
        }

        return string.Empty;
    }
}
