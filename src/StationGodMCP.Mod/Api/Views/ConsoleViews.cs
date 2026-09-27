#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Api.Views;

/// <summary>One console line: its time stamp, level and text (segments and continuation lines joined).</summary>
internal sealed class ConsoleLineView
{
    internal ConsoleLineView(string? time, string? level, string? text)
    {
        Time = time;
        Level = level;
        Text = text;
    }

    public string? Time { get; }

    /// <summary>The level of the line's colour (ConsoleWindow.GetConsoleColor); null if the game has none.</summary>
    public string? Level { get; }

    public string? Text { get; }
}

/// <summary>The command as the console registry knows it (CommandBase).</summary>
internal sealed class ConsoleCommandInfo
{
    internal ConsoleCommandInfo(string command, string? commandKey, string scope, string? helpText,
        string[]? arguments)
    {
        Command = command;
        CommandKey = commandKey;
        Scope = scope;
        HelpText = helpText;
        Arguments = arguments;
    }

    internal string Command { get; }

    internal string? CommandKey { get; }

    internal string Scope { get; }

    internal string? HelpText { get; }

    internal string[]? Arguments { get; }
}

/// <summary>What a console run printed on the main thread, and how complete that is.</summary>
internal sealed class ConsoleCapture
{
    internal ConsoleCapture(bool consoleAvailable, bool asynchronous, int outputLineCount, bool truncated,
        long concurrentLines)
    {
        ConsoleAvailable = consoleAvailable;
        Asynchronous = asynchronous;
        OutputLineCount = outputLineCount;
        Truncated = truncated;
        ConcurrentLines = concurrentLines;
    }

    internal bool ConsoleAvailable { get; }

    internal bool Asynchronous { get; }

    internal int OutputLineCount { get; }

    internal bool Truncated { get; }

    internal long ConcurrentLines { get; }
}

/// <summary>run_console_command: the command, what it printed, and how far that output can be trusted.</summary>
internal sealed class ConsoleRunView
{
    internal ConsoleRunView(ConsoleCommandInfo info, bool runSimulation, ConsoleCapture capture,
        List<ConsoleLineView> output, string note)
    {
        Command = info.Command;
        CommandKey = info.CommandKey;
        Scope = info.Scope;
        HelpText = info.HelpText;
        Arguments = info.Arguments;
        RunSimulation = runSimulation;
        ConsoleAvailable = capture.ConsoleAvailable;
        CompletesAsynchronously = capture.Asynchronous;
        OutputComplete = capture.ConsoleAvailable && !capture.Asynchronous;
        OutputLineCount = capture.OutputLineCount;
        Truncated = capture.Truncated;
        ConcurrentLogLines = capture.ConcurrentLines;
        Output = output;
        Note = note;
    }

    public string Command { get; }

    public string? CommandKey { get; }

    public string Scope { get; }

    public string? HelpText { get; }

    public string[]? Arguments { get; }

    /// <summary>False on a multiplayer client, where several commands only queue a request.</summary>
    public bool RunSimulation { get; }

    public bool ConsoleAvailable { get; }

    public bool CompletesAsynchronously { get; }

    public bool OutputComplete { get; }

    public int OutputLineCount { get; }

    public bool Truncated { get; }

    public long ConcurrentLogLines { get; }

    public List<ConsoleLineView> Output { get; }

    public string Note { get; }
}

/// <summary>read_console: the most recent console lines, oldest first.</summary>
internal sealed class ConsoleReadView
{
    internal ConsoleReadView(int requestedLines, bool consoleAvailable, List<ConsoleLineView> lines)
    {
        RequestedLines = requestedLines;
        LineCount = lines.Count;
        ConsoleAvailable = consoleAvailable;
        Lines = lines;
    }

    public int RequestedLines { get; }

    public int LineCount { get; }

    public bool ConsoleAvailable { get; }

    public List<ConsoleLineView> Lines { get; }
}
