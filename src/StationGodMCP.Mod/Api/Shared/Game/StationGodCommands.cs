#nullable enable

using StationGodMCP.Protocol;
using Util.Commands;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// Adds the stationgod console command (StationGodConsole) to the game's console, through which the owner approves
/// cheat: typed in the host's console, the dedicated server's console, or from a client as serverrun stationgod ...
/// (the game forwards serverrun to the host).
/// </summary>
internal static class StationGodCommands
{
    private static AccessControl? _access;
    private static bool _registered;

    /// <summary>Points the command at the access control in use; adds it to the game's console once.</summary>
    internal static void Register(AccessControl access)
    {
        _access = access;
        if (_registered)
        {
            return;
        }

        CommandLine.AddCommand(StationGodConsole.Key, new BasicCommand(static args => StationGodConsole.Execute(args, _access),
            "Approve cheat tools for a StationGod MCP connection or key for some minutes, take it back, or list the " +
            "connections. Not from run_console_command.",
            new[] { "allow <client_id or key name> [minutes]", "deny <client_id or key name>", "clients" }));
        _registered = true;
    }
}
