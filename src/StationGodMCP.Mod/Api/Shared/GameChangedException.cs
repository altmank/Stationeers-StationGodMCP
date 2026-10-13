#nullable enable

using System;

namespace StationGodMCP;

/// <summary>
/// A game member the mod reaches by reflection, or a Harmony target, that this build of the game no longer has. API
/// methods turn it into the error code game_changed, naming the member.
/// </summary>
internal sealed class GameChangedException : Exception
{
    internal GameChangedException(string member)
        : base(
            $"The game has changed: {member} is gone, so this StationGod MCP build cannot do this. It needs an update " +
                $"for this game version.")
    {
        Member = member;
    }

    internal string Member { get; }
}
