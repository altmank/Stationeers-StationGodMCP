#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Views;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>run_console_command and read_console: the old StationApi and ConsoleBridge shapes; no renames.</summary>
public sealed class ConsoleWireTests
{
    [Fact]
    public void RunAndReadSameWire()
    {
        var old = new
        {
            command = "help", command_key = "help", scope = "None", help_text = "Lists commands",
            arguments = new[] { "command" }, run_simulation = true, console_available = true,
            completes_asynchronously = false, output_complete = true, output_line_count = 1, truncated = false,
            concurrent_log_lines = 0L,
            output = new List<object> { new { time = "12:00:00", level = "Info", text = "help: lists commands" } },
            note = "n"
        };
        ConsoleRunView view = new ConsoleRunView(
            new ConsoleCommandInfo("help", "help", "None", "Lists commands", new[] { "command" }), true,
            new ConsoleCapture(true, false, 1, false, 0L),
            new List<ConsoleLineView> { new ConsoleLineView("12:00:00", "Info", "help: lists commands") }, "n");
        WireCheck.Same(old, view);
        WireCheck.Same(
            new
            {
                requested_lines = 50, line_count = 1, console_available = true,
                lines = new List<object> { new { time = "12:00:00", level = (string?)null, text = "x" } }
            },
            new ConsoleReadView(50, true, new List<ConsoleLineView> { new ConsoleLineView("12:00:00", null, "x") }));
    }
}
