#nullable enable

namespace StationGodMCP.Pure;

/// <summary>
/// A thing's condition in one word, from the game's own broken state first. The game's broken state is not a health
/// number: when damage reaches its maximum on a structure that has a broken mesh, ThingDamageState.Destroy swaps in
/// the broken build state (a build state index below 0) and heals the damage (HealAll), so a broken structure reads 0
/// damage and 100 % health. Structure.IsBroken (damage at its maximum, or a build state below 0) is the only honest
/// signal, so broken wins over every number. A burst pipe (Pipe.IsBurst) is broken too: bursting leaves its damage
/// alone; so is a burnt cable (CableRuptured), the separate undamaged piece an overload leaves in a cable's place.
/// </summary>
internal static class HealthCondition
{
    internal const string Broken = "broken";
    internal const string Damaged = "damaged";
    internal const string Intact = "intact";
    internal const string Indestructible = "indestructible";
    internal const string None = "none";

    /// <summary>
    /// Whether a thing counts as broken: the game's broken state, or a burst pipe (Pipe.IsBurst), which the game keeps
    /// apart from damage: a burst pipe reads 0 damage and is not Thing.IsBroken, yet it holds nothing and refunds
    /// nothing; or a burnt cable (CableRuptured), which reads undamaged, carries no power and refunds nothing.
    /// </summary>
    internal static bool IsWreck(bool gameBroken, bool pipeBurst, bool cableBurnt) =>
        gameBroken || pipeBurst || cableBurnt;

    /// <summary>
    /// broken (the game's broken state, a burst pipe or a burnt cable), none (no damage state), indestructible, damaged (any
    /// damage), else intact.
    /// </summary>
    internal static string Of(bool isBroken, bool hasDamageState, bool indestructible, double? damageRatio)
    {
        if (isBroken)
        {
            return Broken;
        }

        if (!hasDamageState)
        {
            return None;
        }

        if (indestructible)
        {
            return Indestructible;
        }

        return damageRatio.HasValue && damageRatio.Value > 0.0 ? Damaged : Intact;
    }

    /// <summary>
    /// The damage ratio a scan ranks and filters by: 1 for a broken thing (it is as far gone as the game goes, whatever
    /// its healed numbers say), else its own.
    /// </summary>
    internal static double RankRatio(bool isBroken, double damageRatio) => isBroken ? 1.0 : damageRatio;

    /// <summary>
    /// Whether thing_health's scan lists a thing: broken things always (broken_only lists only them), others when
    /// measurable and damaged above the floor.
    /// </summary>
    internal static bool ScanKeeps(bool isBroken, bool measurable, double damageRatio, double minDamageRatio,
        bool brokenOnly)
    {
        if (isBroken)
        {
            return true;
        }

        return !brokenOnly && measurable && damageRatio > minDamageRatio;
    }
}
