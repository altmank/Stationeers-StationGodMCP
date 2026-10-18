#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Api.Shared;

/// <summary>
/// One drawing (or clearing) done on each of several players' screens (show preview and highlight, ShowAudience): the
/// earlier drawings each screen cleared, summed, and the players on other machines whose games drew it. With several
/// screens one that refuses (its game cannot be reached, it has no shader) is a note naming its player and the rest
/// still draw; the call is refused, with the last refusal, only when none drew. One screen refuses as it would alone.
/// </summary>
internal static class ScreenRound
{
    /// <summary>A screen to draw on: the screen, its player's name and, for another machine's, that player.</summary>
    internal readonly struct Target<T>
    {
        internal Target(T screen, string player, string? drawnOn)
        {
            Screen = screen;
            Player = player;
            DrawnOn = drawnOn;
        }

        internal T Screen { get; }

        internal string Player { get; }

        /// <summary>The player whose game draws, when that is another machine; null for this game's screen.</summary>
        internal string? DrawnOn { get; }
    }

    /// <summary>Draws on each target; notes and drawnOn gain what happened. Returns the drawings cleared, summed.</summary>
    internal static int Run<T>(IReadOnlyList<Target<T>> targets, Func<T, int> draw, List<string> notes,
        List<string> drawnOn)
    {
        int cleared = 0;
        int reached = 0;
        ApiException? last = null;
        foreach (Target<T> target in targets)
        {
            try
            {
                cleared += draw(target.Screen);
            }
            catch (ApiException refused) when (targets.Count > 1)
            {
                last = refused;
                notes.Add($"Not drawn for {target.Player}: {refused.Message}");
                continue;
            }

            reached++;
            if (target.DrawnOn != null)
            {
                drawnOn.Add(target.DrawnOn);
            }
        }

        if (reached == 0 && last != null)
        {
            throw last;
        }

        return cleared;
    }
}
