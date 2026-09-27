#nullable enable

using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api;

/// <summary>
/// read_console: the most recent console lines (ConsoleWindow.ConsoleBuffer), oldest first. Read only.
/// </summary>
internal static class ReadConsoleApi
{
    private const int DefaultLines = 50;

    internal static ConsoleReadView Handle(Args args)
    {
        int lines = args.OptionalInt("lines", 1, ConsoleBridge.MaximumConsoleLines) ?? DefaultLines;
        bool available = ConsoleBridge.CanCapture();
        return new ConsoleReadView(lines, available, ConsoleBridge.ReadRecent(lines));
    }
}
