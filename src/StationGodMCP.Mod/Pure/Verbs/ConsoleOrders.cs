#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// The server whats' console options as the values they are: lines, the same 1 to 500 the schema bounds it to, so a
/// call the schema would refuse is refused by the order too and the pure tests can cover it.
/// </summary>
internal static class ConsoleOrders
{
    /// <summary>The fewest lines a call may ask for (the option's minimum).</summary>
    internal const int MinimumLines = 1;

    /// <summary>The most lines a call may ask for (the option's maximum).</summary>
    internal const int MaximumLines = 500;

    /// <summary>
    /// The lines a call asks for: the option's value (the catalogue's default fills it in when the call leaves it out,
    /// else fallback is the what's own default), kept to 1 to 500; null with a problem at the option.
    /// </summary>
    internal static int? Lines(long? given, int fallback, string option, List<OptionProblem> problems)
    {
        long lines = given ?? fallback;
        if (lines < MinimumLines || lines > MaximumLines)
        {
            problems.Add(new OptionProblem(option, $"is {MinimumLines} to {MaximumLines} lines"));
            return null;
        }

        return (int)lines;
    }
}

/// <summary>
/// server command's call, checked without the game: the command line to run (not empty) and the most lines of its
/// output to keep (1 to 500), with confirm from the options (the command runs only with it). The what's label prefixes
/// every refusal ("server command: ...").
/// </summary>
internal sealed class ConsoleOrder
{
    private ConsoleOrder(string command, int lines, ReportConfirm report)
    {
        Command = command;
        Lines = lines;
        Report = report;
    }

    /// <summary>The whole command line, trimmed.</summary>
    public string Command { get; }

    /// <summary>The most lines of what the command printed the answer keeps (the oldest).</summary>
    public int Lines { get; }

    /// <summary>confirm: the call runs the command only with it, and reports what it would run otherwise.</summary>
    public ReportConfirm Report { get; }

    /// <summary>
    /// The order server command's options give; null with every problem added. defaultLines is how many output lines a
    /// call that leaves the option out keeps.
    /// </summary>
    internal static ConsoleOrder? Of(ServerCommandOptions options, int defaultLines, List<OptionProblem> problems)
    {
        int before = problems.Count;
        string command = (options.Command ?? string.Empty).Trim();
        if (command.Length == 0)
        {
            problems.Add(new OptionProblem("command", "is required: the console command to run, e.g. help"));
        }

        int? lines = ConsoleOrders.Lines(options.Lines, defaultLines, "lines", problems);
        return problems.Count > before
            ? null
            : new ConsoleOrder(command, lines!.Value, options.Report);
    }
}
