#nullable enable

using Assets.Scripts;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// The game's clock, for clients that must count game time. GameManager.GameTime is Unity Time.time: it
/// stops while paused (WorldManager.SetGamePause sets timeScale 0) and restarts from zero on every launch, so clients
/// should use its differences. OrbitalSimulation.TimeOfDay is the fraction of the local day from the planet's
/// accumulated rotation. Read only.
/// </summary>
internal static class WorldClock
{
    internal static GameClockView Now() =>
        new GameClockView(GameManager.GameTime, WorldManager.IsGamePaused, OrbitalSimulation.TimeOfDay,
            WorldManager.DaysPast);
}
