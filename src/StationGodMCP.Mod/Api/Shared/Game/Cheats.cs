#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Scheduling;
using Util.Commands;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// The game's cheat switch (CheatSwitch): its world is creative when WorldManager.IsCreative says so, its timer runs in
/// real time (a paused game still spends minutes), and WorldStores clears it whenever a world is left. Nothing about it
/// is saved, so it is off at every start.
/// </summary>
internal static class Cheats
{
    internal static CheatSwitch Main { get; } =
        new CheatSwitch(StopwatchClock.Instance, static () => DateTime.UtcNow, static () => WorldManager.IsCreative());

    /// <summary>Adds the switch's console command (stationgod) to the game's console; it is listed in help.</summary>
    internal static void Register() => CommandLine.AddCommand(CheatSwitch.CommandKey, new CheatCommand());

    /// <summary>
    /// stationgod cheats on &lt;minutes&gt; | off, or the state: run on the host by someone at its console (the F3
    /// console, a dedicated server's own console, or serverrun with the server's auth secret). Refused when StationGod
    /// itself runs it (server command, scripts) and on a client. It prints only to that console; no player is told.
    /// </summary>
    private sealed class CheatCommand : CommandBase
    {
        public override string HelpText =>
            "Lets StationGod cheat in a world that is not creative, for some minutes. Only typed here, never by StationGod.";

        public override string[] Arguments => new[] { "cheats on <minutes>", "cheats off", "cheats" };

        public override bool IsLaunchCmd => false;

        public override CommandScope Scope => CommandScope.InGame | CommandScope.HostOrSinglePlayer;

        public override string? Execute(string[] args)
        {
            if (ConsoleBridge.RunningForStationGod)
            {
                ConsoleWindow.PrintError(CheatSwitch.ConsoleOnlyRefusal(), suppressStacktrace: true);
                return null;
            }

            if (!EnforceScope(CheatSwitch.CommandKey))
            {
                return null;
            }

            string line = Main.Command(new List<string>(args));
            if (args.Length >= 2)
            {
                StationGodMod.Log($"Console: stationgod {string.Join(" ", args)} -> {line}");
            }

            ConsoleWindow.Print(line);
            return null;
        }
    }
}
