#nullable enable

using System.Collections.Generic;

using StationGodMCP.Api.Shared;

using StationGodMCP.Pure.Shaping;

namespace StationGodMCP.Api.Views;

/// <summary>
/// A Lua chip's runtime as StationeersLua holds it: whether its source is compiling on a worker thread, whether a
/// runtime exists and finished its module-level init, the last compile or runtime error, and the tail of its print()
/// log. Unavailable names what could not be read when StationeersLua's internals have changed.
/// </summary>
internal sealed class LuaStateView
{
    internal LuaStateView(LuaRuntimeFlags flags, long? sourceVersion, LuaErrorView? lastError, LuaLogView? log,
        string? unavailable)
    {
        Compiling = flags.Compiling;
        HasRuntime = flags.HasRuntime;
        InitComplete = flags.InitComplete;
        Library = flags.Library;
        SourceVersion = sourceVersion;
        LastError = lastError;
        Log = log;
        Unavailable = unavailable;
    }

    /// <summary>The source is being compiled on a worker thread; read again for the outcome.</summary>
    public bool Compiling { get; }

    /// <summary>A runtime exists: compiled, and not faulted since.</summary>
    public bool HasRuntime { get; }

    /// <summary>Module-level code has finished; tick(dt) runs from here on.</summary>
    public bool InitComplete { get; }

    /// <summary>
    /// Compiled, initialised, no error, and not a library: the script is running, provided its holder is operable.
    /// </summary>
    public bool Running => HasRuntime && InitComplete && !Compiling && !Library && LastError == null;

    /// <summary>A --@module library chip: other chips require it; it never ticks itself.</summary>
    public bool Library { get; }

    /// <summary>StationeersLua's count of source writes to this chip since the game started; null if unreadable.</summary>
    public long? SourceVersion { get; }

    public LuaErrorView? LastError { get; }

    /// <summary>The tail of the chip's print() log; only get_ic_status reads it.</summary>
    public LuaLogView? Log { get; }

    public string? Unavailable { get; }
}

/// <summary>The runtime flags of a Lua chip, read together.</summary>
internal readonly struct LuaRuntimeFlags
{
    internal LuaRuntimeFlags(bool compiling, bool hasRuntime, bool initComplete, bool library)
    {
        Compiling = compiling;
        HasRuntime = hasRuntime;
        InitComplete = initComplete;
        Library = library;
    }

    internal bool Compiling { get; }

    internal bool HasRuntime { get; }

    internal bool InitComplete { get; }

    internal bool Library { get; }
}

/// <summary>StationeersLua's last error for a chip: compile (parse, compile or init) or runtime (tick).</summary>
internal sealed class LuaErrorView
{
    internal LuaErrorView(string kind, int line, string message, string? traceback)
    {
        Kind = kind;
        Line = line;
        Message = message;
        Traceback = string.IsNullOrEmpty(traceback) ? null : traceback;
    }

    /// <summary>compile or runtime.</summary>
    public string Kind { get; }

    /// <summary>The script line, 1-based; 0 when the error names none.</summary>
    public int Line { get; }

    public string Message { get; }

    public string? Traceback { get; }
}

/// <summary>The last lines of a Lua chip's print() log, oldest first.</summary>
internal sealed class LuaLogView : ITruncatingView
{
    internal LuaLogView(List<string> lines, int lineCount)
    {
        Lines = lines;
        LineCount = lineCount;
    }

    public List<string> Lines { get; }

    /// <summary>Lines the chip's log holds; StationeersLua keeps about the last 32000 characters.</summary>
    public int LineCount { get; }

    public bool Truncated => LineCount > Lines.Count;

    public void NoteTruncations(string path) =>
        Truncations.Capped(path + "lines", Lines.Count, LineCount, "log_lines", ReplyDefaults.LuaLogLinesMaximum);
}
