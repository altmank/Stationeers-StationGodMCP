#nullable enable

namespace StationGodMCP.Pure.Rockets;

/// <summary>
/// The rate a rocket engine's combustion runs at, and who set it: the game's RocketEngineBase.ENGINE_COMBUSTION_RATE
/// (0.96f, what CombustEngine passes to Atmosphere.TryCombust, RocketEngineBase.cs:81, 521), or Terraforming
/// Reloaded's TerraformingReloaded.Patching.Rockets.CombustionRate() when that mod is loaded (its per-world
/// RocketsBurnCompletely option: 1.0, else the game's rate). A value the mod returns that is no rate (not a finite
/// number in (0, 1]) is not used: the game's rate is, with a note.
/// </summary>
internal sealed class CombustionRate
{
    internal const double GameRate = 0.9599999785423279;
    internal const string GameSource = "game";
    internal const string TerraformingSource = "terraforming_reloaded";

    private CombustionRate(double rate, string source, string? note)
    {
        Rate = rate;
        Source = source;
        Note = note;
    }

    internal static CombustionRate Game { get; } = new CombustionRate(GameRate, GameSource, null);

    internal double Rate { get; }

    internal string Source { get; }

    /// <summary>Why Terraforming Reloaded's rate was not used although the mod answered; null otherwise.</summary>
    internal string? Note { get; }

    /// <summary>The rate Terraforming Reloaded's CombustionRate() returned, or why it could not be used.</summary>
    internal static CombustionRate FromTerraforming(object? returned) =>
        returned is double rate && !double.IsNaN(rate) && !double.IsInfinity(rate) && rate > 0.0 && rate <= 1.0
            ? new CombustionRate(rate, TerraformingSource, null)
            : Unusable($"Terraforming Reloaded's CombustionRate() returned {returned ?? "null"}, which is no rate " +
                       "in (0, 1]; the game's rate is used.");

    /// <summary>The game's rate, noting why Terraforming Reloaded's could not be used.</summary>
    internal static CombustionRate Unusable(string why) => new CombustionRate(GameRate, GameSource, why);
}
