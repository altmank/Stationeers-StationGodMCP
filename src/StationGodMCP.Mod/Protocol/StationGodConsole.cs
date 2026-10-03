#nullable enable

using System;
using System.Globalization;
using System.Threading;
using StationGodMCP.Pure.Access;

namespace StationGodMCP.Protocol;

/// <summary>
/// What the stationgod console command does (allow, deny, clients), apart from the game's console that calls it, and
/// the mark run_console_command sets for the length of its call: while it is set, allow and deny refuse, so no
/// connection can approve itself, others, or more time (unless the test-only AllowArmingFromToolConsole is set).
/// </summary>
internal static class StationGodConsole
{
    internal const string Key = "stationgod";

    private static int _toolConsoleDepth;

    /// <summary>Whether run_console_command is running a command right now.</summary>
    internal static bool ToolConsoleRunning => Volatile.Read(ref _toolConsoleDepth) > 0;

    /// <summary>Whether a console command's key is this command.</summary>
    internal static bool IsStationGod(string? key) => string.Equals(key?.TrimStart('-'), Key, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether run_console_command must refuse this command: stationgod always, unless the test-only
    /// AllowArmingFromToolConsole is set.
    /// </summary>
    internal static bool RefusedFromToolConsole(string? key, AccessControl? access) =>
        IsStationGod(key) && !(access?.Settings.AllowArmingFromToolConsole ?? false);

    /// <summary>run_console_command marks the length of its call.</summary>
    internal static IDisposable ToolConsole()
    {
        Interlocked.Increment(ref _toolConsoleDepth);
        return new ToolConsoleScope();
    }

    /// <summary>The command's work for its arguments (the words after stationgod); the answer is the console's text.</summary>
    internal static string Execute(string[] args, AccessControl? access)
    {
        if (access == null)
        {
            return "StationGod MCP is not listening.";
        }

        string verb = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : string.Empty;
        if (verb != "clients" && ToolConsoleRunning && !access.Settings.AllowArmingFromToolConsole)
        {
            return "Refused: stationgod allow and deny work only when the owner types them, not through run_console_command.";
        }

        switch (verb)
        {
            case "allow" when args.Length >= 2:
                int minutes = Arming.DefaultMinutes;
                if (args.Length >= 3 && !int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out minutes))
                {
                    return $"Minutes must be a whole number from 1 to {Arming.MaximumMinutes}.";
                }

                return access.Allow(args[1].Trim(), minutes);
            case "deny" when args.Length >= 2:
                return access.Deny(args[1].Trim());
            case "clients":
                return access.Report();
            default:
                return "Usage: stationgod allow <client_id or key name> [minutes] | stationgod deny <client_id or key name> | stationgod clients";
        }
    }

    private sealed class ToolConsoleScope : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref _toolConsoleDepth);
            }
        }
    }
}
