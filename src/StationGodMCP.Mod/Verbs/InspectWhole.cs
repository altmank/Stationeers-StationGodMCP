#nullable enable

using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>inspect player (catalogue inspect.json): the local player's hunger, thirst and mood (PlayerVitals). Read only.</summary>
[ReplyFact(FactRule.Live, "the thing's values as the game holds them now")]
internal static class InspectPlayer
{
    internal static VerbOutcome Run(InspectPlayerOptions options) => Answer.Of(PlayerVitals.Read());
}

/// <summary>
/// inspect planet (catalogue inspect.json): the planet's own atmosphere, its reservoirs, temperature range and starting
/// air (PlanetReport); only the terraforming mod's state while no world is loaded. Read only.
/// </summary>
[ReplyFact(FactRule.Live, "the thing's values as the game holds them now")]
internal static class InspectPlanet
{
    internal static VerbOutcome Run(InspectPlanetOptions options) =>
        WorkResult<object>.Of(PlanetReport.Reading, static view => view);
}

/// <summary>inspect weather (catalogue inspect.json): the storm schedule, the world's events and the season (WeatherReport). Read only.</summary>
[ReplyFact(FactRule.Live, "the thing's values as the game holds them now")]
internal static class InspectWeather
{
    internal static VerbOutcome Run(InspectWeatherOptions options) => Answer.Of(WeatherReport.Read());
}

/// <summary>read clock (catalogue read.json): the game's clock (WorldClock). Read only.</summary>
[ReplyFact(FactRule.Live, "the game's clock")]
internal static class ReadClock
{
    internal static VerbOutcome Run(ReadClockOptions options) => Answer.Of(WorldClock.Now());
}

/// <summary>
/// inspect vault (catalogue inspect.json): what each Ingot Vault stores, and every Remote Vault with the vault it
/// reaches (VaultStocks); an id keeps one vault, a list of ids answers one row each. Read only.
/// </summary>
[ReplyFact(FactRule.Live, "the thing's values as the game holds them now")]
internal static class InspectVault
{
    internal static VerbOutcome Run(InspectVaultOptions options) =>
        options.Selector.Ids is { } ids
            ? IdAnswers.Things(ids, InspectVaultOptions.Spec.Label, id => VaultStocks.Read(id))
            : Answer.Of(VaultStocks.Read(null));
}

/// <summary>A view answered as it is, in the reply views' names.</summary>
internal static class Answer
{
    internal static VerbOutcome Of(object view) =>
        new VerbOutcome.Answered(view);
}
