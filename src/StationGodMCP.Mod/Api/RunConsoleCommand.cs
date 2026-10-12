#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Protocol;
using StationGodMCP.Pure.Protocol;
using Util.Commands;

namespace StationGodMCP.Api;

/// <summary>
/// run_console_command: run a console command as if typed (CommandLine.Process) and return the lines it printed on
/// the main thread while it ran (ConsoleBridge; on a dedicated server, the lines ConsoleWindow.Print sends to its
/// system console). A command the registry does not know (CommandLine.CommandsMap), or one
/// the console would only queue for later (RequiresGameManagerIsInitialized before GameManager.IsInitialized), is
/// refused. Writes: the command does whatever it does; dry_run reports the command from the same checks and runs
/// nothing (ReportOnly).
/// </summary>
internal static class RunConsoleCommandApi
{
    private const int DefaultOutputLines = ReplyDefaults.ConsoleOutputLines;

    private const string NoCapture =
        "The console's printed lines cannot be read here (no console buffer yet, or on a dedicated server the " +
        "patch on ConsoleWindow.Print did not apply; the log says which), so nothing could be captured. This is " +
        "not evidence that the command printed nothing or that it succeeded.";

    private const string Silent =
        "This command runs to completion before it returns and it printed nothing. Most such commands report what " +
        "they did, so silence usually means no output rather than success; confirm the effect some other way if it " +
        "matters.";

    private const string Printed =
        "Output is the console lines this command printed on the game's main thread, oldest first, and the command " +
        "had finished when this call returned. A refused scope, an unknown argument or a stack trace appears here " +
        "as a red line rather than as a request error.";

    internal static object Handle(Args args)
    {
        bool report = ReportOnly.Asked(args);
        string command = args.String("command").Trim();
        if (command.Length == 0)
        {
            throw ApiErrors.InvalidArgument(Vocabulary.Text("Argument '{arg:command}' must not be empty."));
        }

        int maximumLines = args.OptionalInt("max_output_lines", 1, ConsoleBridge.MaximumConsoleLines) ??
                           DefaultOutputLines;
        return WriteCheck<ConsolePlan>.Run(() => Check(command, maximumLines)).Answer(report, Run, Describe);
    }

    private static ConsolePlan Check(string command, int maximumLines)
    {
        string? key = ConsoleBridge.ResolveCommandKey(command);
        CommandBase definition = Require(key);
        return new ConsolePlan(new ConsoleCommandInfo(command, key, definition.Scope.ToString(),
            definition.HelpText, definition.Arguments), definition, maximumLines);
    }

    private static object Run(ConsolePlan plan)
    {
        string command = plan.Info.Command;
        bool consoleAvailable = ConsoleBridge.CanCapture();
        bool asynchronous = ConsoleBridge.CompletesAsynchronously(plan.Definition);
        ConsoleBridge.Echo(command);
        List<ConsoleLineView> output;
        int lineCount;
        bool truncated;
        long concurrentLines;
        output = ConsoleBridge.Execute(command, plan.MaximumLines, out lineCount, out truncated, out concurrentLines);
        ConsoleCapture capture = new ConsoleCapture(consoleAvailable, asynchronous, lineCount, truncated,
            concurrentLines);
        return new ConsoleRunView(plan.Info, ConsoleBridge.RunSimulation, capture, output,
            Note(capture, plan.Info.CommandKey));
    }

    private static object Describe(ConsolePlan plan) =>
        new ConsoleReportView(plan.Info, ConsoleBridge.RunSimulation, ConsoleBridge.CanCapture(),
            ConsoleBridge.CompletesAsynchronously(plan.Definition));

    private static CommandBase Require(string? key)
    {
        CommandBase? definition = ConsoleBridge.FindCommand(key);
        if (definition == null)
        {
            throw ApiErrors.Refused("command_not_found",
                $"No console command is registered as '{key}'. Run the console command 'help' to list them.");
        }

        if (ConsoleBridge.WouldBeDeferredToLaunchQueue(definition))
        {
            throw ApiErrors.Refused("command_not_ready",
                $"'{key}' needs a loaded world. The console would not run it now: it would be queued and fired " +
                "later while the world loads, with its output attributed to nothing. Retry once the game has finished " +
                "loading.");
        }

        return definition;
    }

    private static string Note(ConsoleCapture capture, string? key)
    {
        if (!capture.ConsoleAvailable)
        {
            return NoCapture;
        }

        // An asynchronous command started a UniTask and returned; its result is printed frames later.
        string note = capture.Asynchronous
            ? $"'{key}' finishes on a background task, so it returned before its result was printed. Whatever is in " +
                "'output' is only what had been printed by the time this call returned, and an EMPTY list is not " +
                "evidence that the command printed nothing, succeeded or failed. Call " +
                $"{Vocabulary.Tool("read_console")} a second or two later to see the outcome."
            : capture.OutputLineCount == 0 ? Silent : Printed;
        if (capture.ConcurrentLines > 0)
        {
            note += $" {capture.ConcurrentLines} further line(s) were printed into the console from a background " +
                "thread while this command ran; Unity mirrors errors and their stack traces in that way. They are " +
                "deliberately not mixed into the output above, and are visible through " +
                $"{Vocabulary.Tool("read_console")}.";
        }

        return note;
    }

    /// <summary>A checked command: what the registry knows of it, and how many printed lines to answer.</summary>
    private sealed class ConsolePlan
    {
        internal ConsolePlan(ConsoleCommandInfo info, CommandBase definition, int maximumLines)
        {
            Info = info;
            Definition = definition;
            MaximumLines = maximumLines;
        }

        internal ConsoleCommandInfo Info { get; }

        internal CommandBase Definition { get; }

        internal int MaximumLines { get; }
    }
}
