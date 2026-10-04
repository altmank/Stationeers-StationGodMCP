#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api;

/// <summary>
/// read_console: the most recent console lines (ConsoleWindow.ConsoleBuffer), oldest first. Read only.
/// </summary>
internal static class ReadConsoleApi
{
    private const int DefaultLines = ReplyDefaults.ConsoleLines;

    internal static ConsoleReadView Handle(Args args)
    {
        int lines = args.OptionalInt("lines", 1, ConsoleBridge.MaximumConsoleLines) ?? DefaultLines;
        bool available = ConsoleBridge.CanCapture();
        List<ConsoleLineView> recent = ConsoleBridge.ReadRecent(lines);
        if (recent.Count == lines && lines < ConsoleBridge.MaximumConsoleLines)
        {
            Pure.Shaping.Truncations.Capped("lines", recent.Count,
                ConsoleBridge.ReadRecent(ConsoleBridge.MaximumConsoleLines).Count, "lines", ConsoleBridge.MaximumConsoleLines);
        }

        return new ConsoleReadView(lines, available, recent);
    }
}
