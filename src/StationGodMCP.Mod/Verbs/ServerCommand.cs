#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// server command (catalogue server.json): run any console command and answer what it printed — spawn, delete,
/// teleport, storms, settings. The command line is run as if typed (CommandLine.Process) and the lines it printed on
/// the main thread while it ran are the answer (ConsoleCommands; on a dedicated server, the lines
/// ConsoleWindow.Print sends to its system console). A command the registry does not know, or one the console would
/// only queue for later, is refused as command_not_found or command_not_ready. Cheat: confirm: true runs it, and
/// without confirm the call reports the command it would run and runs nothing.
/// </summary>
[ReplyFact(FactRule.Live, "the game's console and the mod's profiler")]
internal static class ServerCommand
{
    internal static VerbOutcome Run(ServerCommandOptions options)
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        ConsoleOrder? order = ConsoleOrder.Of(options, ReplyDefaults.ConsoleOutputLines, problems);
        if (order == null)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(ServerCommandOptions.Spec.Label, problems));
        }

        return WriteCheck<ConsoleCommands.ConsolePlan>.Run(() => ConsoleCommands.Check(order))
            .Outcome(order.Report, ConsoleCommands.Run, ConsoleCommands.Reported);
    }
}
