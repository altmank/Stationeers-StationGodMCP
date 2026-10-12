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
/// The write-ahead hook (C5's journal): Before is called just before a write is sent, After once its reply is in.
/// </summary>
public interface IRunJournal
{
    void Before(RunRecord run, WriteEntry write);

    void After(RunRecord run, WriteEntry write);
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
}

/// <summary>
/// What a run did, as the hub keeps it for its reply, its notices and (C5) its journal and undo: the code, the route,
/// the end state, the writes that acted in order, the rows it reported or had refused, and what it printed and
/// returned. Lists are read under Sync.
/// </summary>
public sealed class RunRecord
{
    public RunRecord(string id, string code, bool confirm, long stream, string? world)
    {
        Id = id;
        Code = code;
        Confirm = confirm;
        Stream = stream;
        World = world;
        FirstLine = FirstCodeLine(code);
        Started = DateTime.UtcNow;
    }

    /// <summary>The lock the lists and states below are read and changed under.</summary>
    public object Sync { get; } = new();

    public string Id { get; }

    public string Code { get; }

    /// <summary>The first non-blank code line, as notices and cost lines name the run.</summary>
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
    public List<string> Left { get; } = [];

    /// <summary>The sg calls the worker made.</summary>
    public int Calls { get; internal set; }

    /// <summary>The run's stream costs, read at its end (null until then, or when the game did not answer).</summary>
    public Client.StreamCost? Cost { get; internal set; }

    public bool IsLive => State == RunState.Running;

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
