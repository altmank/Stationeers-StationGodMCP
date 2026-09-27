#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using Util.Commands;

namespace StationGodMCP.Api;

/// <summary>
/// run_console_command: run a console command as if typed (CommandLine.Process) and return the lines it printed on
/// the main thread while it ran (ConsoleBridge). A command the registry does not know (CommandLine.CommandsMap), or one
/// the console would only queue for later (RequiresGameManagerIsInitialized before GameManager.IsInitialized), is
/// refused. Writes: the command does whatever it does.
/// </summary>
internal static class RunConsoleCommandApi
{
    private const int DefaultOutputLines = 100;

    private const string NoCapture =
        "The console buffer is not filled in this build, so nothing could be captured. This is not evidence that " +
        "the command printed nothing or that it succeeded.";

    private const string Silent =
        "This command runs to completion before it returns and it printed nothing. Most such commands report what " +
        "they did, so silence usually means no output rather than success; confirm the effect some other way if it " +
        "matters.";

    private const string Printed =
        "Output is the console lines this command printed on the game's main thread, oldest first, and the command " +
        "had finished when this call returned. A refused scope, an unknown argument or a stack trace appears here " +
        "as a red line rather than as a request error.";

    internal static ConsoleRunView Handle(Args args)
    {
        string command = args.String("command").Trim();
        if (command.Length == 0)
        {
            throw ApiErrors.InvalidArgument("Argument 'command' must not be empty.");
        }

        int maximumLines = args.OptionalInt("max_output_lines", 1, ConsoleBridge.MaximumConsoleLines) ??
                           DefaultOutputLines;
        string? key = ConsoleBridge.ResolveCommandKey(command);
        CommandBase definition = Require(key);
        bool consoleAvailable = ConsoleBridge.CanCapture();
        bool asynchronous = ConsoleBridge.CompletesAsynchronously(definition);
        ConsoleBridge.Echo(command);
        List<ConsoleLineView> output = ConsoleBridge.Execute(command, maximumLines, out int lineCount,
            out bool truncated, out long concurrentLines);
        ConsoleCapture capture = new ConsoleCapture(consoleAvailable, asynchronous, lineCount, truncated,
            concurrentLines);
        ConsoleCommandInfo info = new ConsoleCommandInfo(command, key, definition.Scope.ToString(),
            definition.HelpText, definition.Arguments);
        return new ConsoleRunView(info, ConsoleBridge.RunSimulation, capture, output, Note(capture, key));
    }

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
                $"'{key}' needs an initialised GameManager. The console would not run it now: it would be queued " +
                "and fired later during world init, out of band and with its output attributed to nothing. Retry " +
                "once the game has finished loading.");
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
            ? $"'{key}' finishes on a background task, so it returned before its result was printed. Whatever is " +
              "in 'output' is only what had been printed by the time this call returned, and an EMPTY list is not " +
              "evidence that the command printed nothing, succeeded or failed. Call read_console a second or two " +
              "later to see the outcome."
            : capture.OutputLineCount == 0 ? Silent : Printed;
        if (capture.ConcurrentLines > 0)
        {
            note += $" {capture.ConcurrentLines} further line(s) were printed into the console from a background " +
                    "thread while this command ran; Unity mirrors errors and their stack traces in that way. They " +
                    "are deliberately not mixed into the output above, and are visible through read_console.";
        }

        return note;
    }
}
