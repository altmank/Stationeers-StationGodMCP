#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Shaping;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// server console (catalogue server.json): the latest in-game console lines, oldest first — command output, errors and
/// their stack traces (ConsoleWindow.ConsoleBuffer; on a dedicated server the last lines the game wrote to the
/// server's own console after the mod loaded). Read only.
/// </summary>
internal static class ServerConsole
{
    internal static VerbOutcome Run(ServerConsoleOptions options)
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        int? lines = ConsoleOrders.Lines(options.Lines, ReplyDefaults.ConsoleLines, "lines", problems);
        if (lines == null)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(ServerConsoleOptions.Spec.Label, problems));
        }

        bool available = ConsoleBridge.CanCapture();
        List<ConsoleLineView> recent = ConsoleBridge.ReadRecent(lines.Value);
        if (recent.Count == lines.Value && lines.Value < ConsoleBridge.MaximumConsoleLines)
        {
            Truncations.Capped("lines", recent.Count,
                ConsoleBridge.ReadRecent(ConsoleBridge.MaximumConsoleLines).Count, "lines",
                ConsoleBridge.MaximumConsoleLines);
        }

        return Answer.Of(new ConsoleReadView(lines.Value, available, recent));
    }
}
