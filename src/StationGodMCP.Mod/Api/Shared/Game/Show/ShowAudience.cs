#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api.Shared.Game.Show;

/// <summary>
/// The screens show draws on (PlayerSight.Audience): the player the call names (as_player) or the rule picks, else
/// every player in play, so a dedicated server with several players draws for each of them. One player is held to the
/// one-player rules (no_view, and view_stale where distances are measured); with several, a player whose screen cannot
/// be drawn on (no StationGod, left, the drawing too large) is a note (ScreenRound), and only a call that reaches no
/// screen is refused. Distances are measured from the first current camera.
/// </summary>
internal sealed class ShowAudience
{
    private readonly List<ScreenRound.Target<PlayerScreen>> _targets;
    private readonly List<string> _drawnOn = new List<string>();

    private ShowAudience(List<ScreenRound.Target<PlayerScreen>> targets, CameraSight? camera, List<string> notes)
    {
        _targets = targets;
        Camera = camera;
        Notes = notes;
    }

    /// <summary>The camera distances are measured from; null when no player's is current (several players).</summary>
    internal CameraSight? Camera { get; }

    /// <summary>The reply's view: the source of Camera.</summary>
    internal ViewSourceView? Source => Camera?.Source;

    /// <summary>Why a player got nothing, and whose camera the distances are from, when several are drawn for.</summary>
    internal List<string> Notes { get; }

    /// <summary>The players on other machines whose games drew it; null when only this game's screen did.</summary>
    internal List<string>? DrawnOn => _drawnOn.Count > 0 ? _drawnOn : null;

    /// <summary>What drew it: each screen's renderer, once.</summary>
    internal string Renderer
    {
        get
        {
            List<string> renderers = new List<string>(_targets.Count);
            foreach (ScreenRound.Target<PlayerScreen> target in _targets)
            {
                if (!renderers.Contains(target.Screen.Renderer))
                {
                    renderers.Add(target.Screen.Renderer);
                }
            }

            return string.Join(", ", renderers);
        }
    }

    /// <summary>
    /// The screens name (show preview, show highlight) draws on; measures: distances are measured from a camera, so one
    /// player's view must be current (view_stale otherwise). no_view when no screen at all can be drawn on.
    /// </summary>
    internal static ShowAudience ToDraw(string name, bool measures)
    {
        List<PlayerSight> sights = PlayerSight.Audience(out string? absence);
        if (sights.Count == 0)
        {
            throw ApiErrors.Refused("no_view", $"{name}: {absence}");
        }

        if (sights.Count == 1)
        {
            PlayerSight one = sights[0];
            CameraSight? camera = measures ? one.Require(name) : one.AnyCamera;
            PlayerScreen screen = camera?.Screen ?? one.RequireScreen(name);
            return new ShowAudience(new List<ScreenRound.Target<PlayerScreen>> { Target(screen, one.PlayerName) },
                camera, new List<string>());
        }

        List<ScreenRound.Target<PlayerScreen>> targets = new List<ScreenRound.Target<PlayerScreen>>(sights.Count);
        List<string> notes = new List<string>();
        CameraSight? measured = null;
        foreach (PlayerSight sight in sights)
        {
            if (sight is PlayerSight.Unseen unseen)
            {
                notes.Add("Not drawn for one player: " + unseen.Reason);
                continue;
            }

            CameraSight camera = sight.AnyCamera!;
            if (sight is PlayerSight.Seen)
            {
                measured ??= camera;
            }

            targets.Add(Target(camera.Screen, camera.PlayerName));
        }

        if (targets.Count == 0)
        {
            throw ApiErrors.Refused("no_view", $"{name}: no player's screen can be drawn on. {string.Join(" ", notes)}");
        }

        notes.Add(measured != null
            ? $"Drawn for every player in play (as_player names one); distances are from {measured.PlayerName}'s camera."
            : "Drawn for every player in play (as_player names one); no player's view is current, so no distances.");
        return new ShowAudience(targets, measured, notes);
    }

    /// <summary>The screens clear empties: each player's that can be reached, else this game's own.</summary>
    internal static ShowAudience ToClear()
    {
        List<PlayerSight> sights = PlayerSight.Audience(out string? _);
        List<ScreenRound.Target<PlayerScreen>> targets = new List<ScreenRound.Target<PlayerScreen>>(sights.Count + 1);
        CameraSight? first = null;
        foreach (PlayerSight sight in sights)
        {
            CameraSight? camera = sight.AnyCamera;
            if (camera != null)
            {
                first ??= camera;
                targets.Add(Target(camera.Screen, camera.PlayerName));
            }
        }

        if (targets.Count == 0)
        {
            targets.Add(Target(PlayerScreen.Local.Instance, null));
        }

        return new ShowAudience(targets, first, new List<string>());
    }

    /// <summary>Draws (or clears) on each screen (ScreenRound); returns how many earlier drawings went, summed.</summary>
    internal int Each(System.Func<PlayerScreen, int> draw) => ScreenRound.Run(_targets, draw, Notes, _drawnOn);

    private static ScreenRound.Target<PlayerScreen> Target(PlayerScreen screen, string? player) =>
        new ScreenRound.Target<PlayerScreen>(screen, player ?? "this game", screen.DrawnOn);
}
