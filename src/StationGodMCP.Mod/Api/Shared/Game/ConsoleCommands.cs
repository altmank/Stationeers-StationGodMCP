#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Verbs;
using Util.Commands;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// server command over ConsoleBridge: the command line is run as if typed (CommandLine.Process) and the answer is the
/// lines it printed on the main thread while it ran (on a dedicated server, the lines ConsoleWindow.Print sends to its
/// system console). A command the registry does not know (CommandLine.CommandsMap), or one the console would only
/// queue for later (RequiresGameManagerIsInitialized before GameManager.IsInitialized), is refused: the acting call
/// answers command_not_found or command_not_ready, and a report lists that refusal as its problem. Without confirm the
/// command is not run: the report says what would run.
/// </summary>
internal static class ConsoleCommands
{
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

    private const string NothingRun = "Nothing was run. confirm: true runs it.";

    /// <summary>A checked command: what the registry knows of it, and how many printed lines to answer.</summary>
    internal sealed class ConsolePlan
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

    /// <summary>
    /// The checks the acting call and its report both start from: the command the registry resolves, or the refusal
    /// (command_not_found, command_not_ready).
    /// </summary>
    internal static ConsolePlan Check(ConsoleOrder order)
    {
        string? key = ConsoleBridge.ResolveCommandKey(order.Command);
        CommandBase definition = Require(key);
        return new ConsolePlan(new ConsoleCommandInfo(order.Command, key, definition.Scope.ToString(),
            definition.HelpText, definition.Arguments), definition, order.Lines);
    }

    /// <summary>The act: echo the command, run it, and answer what it printed.</summary>
    internal static VerbOutcome Run(ConsolePlan plan)
    {
        string command = plan.Info.Command;
        bool consoleAvailable = ConsoleBridge.CanCapture();
        bool asynchronous = ConsoleBridge.CompletesAsynchronously(plan.Definition);
        ConsoleBridge.Echo(command);
        List<ConsoleLineView> output = ConsoleBridge.Execute(command, plan.MaximumLines, out int lineCount,
            out long concurrentLines);
        ConsoleCapture capture = new ConsoleCapture(consoleAvailable, asynchronous, lineCount, concurrentLines);
        ConsoleRunView run = new ConsoleRunView(plan.Info, ConsoleBridge.RunSimulation, capture, output,
            Note(capture, plan.Info.CommandKey));
        run.NoteTruncations(string.Empty);
        return new VerbOutcome.Answered(JObject.FromObject(run, ApiJson.Serializer));
    }

    /// <summary>
    /// The report: the command the console would run and what it is, as the run's own reply keys, and the note saying
    /// nothing was run; the what takes no target, so the report lists none.
    /// </summary>
    internal static ReportReply Reported(ConsolePlan plan) =>
        Report.Of(Array.Empty<ReportTarget>(), Body(plan), new List<ReportProblem>());

    // The plan as the run's reply says it, without what a run printed: the command, its scope, its help and arguments,
    // and whether this game would run it (run_simulation) and could capture its output.
    private static JObject Body(ConsolePlan plan)
    {
        JObject body = JObject.FromObject(plan.Info, ApiJson.Serializer);
        body["run_simulation"] = ConsoleBridge.RunSimulation;
        body["console_available"] = ConsoleBridge.CanCapture();
        body["completes_asynchronously"] = ConsoleBridge.CompletesAsynchronously(plan.Definition);
        body["note"] = NothingRun;
        return body;
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
                "server {what: \"console\"} a second or two later to see the outcome."
            : capture.OutputLineCount == 0 ? Silent : Printed;
        if (capture.ConcurrentLines > 0)
        {
            note += $" {capture.ConcurrentLines} further line(s) were printed into the console from a background " +
                "thread while this command ran; Unity mirrors errors and their stack traces in that way. They are " +
                "deliberately not mixed into the output above, and are visible through server {what: \"console\"}.";
        }

        return note;
    }
}
