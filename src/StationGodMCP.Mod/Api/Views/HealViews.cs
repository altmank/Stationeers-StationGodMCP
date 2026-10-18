#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Api.Views;

/// <summary>
/// write health's reply: the player, whether they are in the game, their state (EntityState), each part's damage
/// before and after (the body, then each organ), and whether every part reads no damage after; the report leaves
/// healed out and gives each part's damage after as none, what the heal sets.
/// </summary>
internal sealed class HealView
{
    internal HealView(ThingView player, bool online, string state, List<HealedPartView> parts, bool? healed)
    {
        Player = player;
        Online = online;
        State = state;
        Parts = parts;
        Healed = healed;
    }

    public ThingView Player { get; }

    public bool Online { get; }

    public string State { get; }

    public List<HealedPartView> Parts { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public bool? Healed { get; }
}

/// <summary>
/// One part healed: body, brain or lungs, the thing whose damage state it is, its damage as the game rounds it for the
/// tooltip (ThingDamageState.TotalRounded: Total / MaxDamage as a whole percent, stun not counted) and each kind.
/// </summary>
internal sealed class HealedPartView
{
    internal HealedPartView(string part, ThingView thing, int damagePercentBefore, int damagePercentAfter,
        DamagePartsView before, DamagePartsView after)
    {
        Part = part;
        Thing = thing;
        DamagePercentBefore = damagePercentBefore;
        DamagePercentAfter = damagePercentAfter;
        Before = before;
        After = after;
    }

    public string Part { get; }

    public ThingView Thing { get; }

    public int DamagePercentBefore { get; }

    public int DamagePercentAfter { get; }

    public DamagePartsView Before { get; }

    public DamagePartsView After { get; }

    /// <summary>Whether no kind of damage is left after.</summary>
    internal bool Whole => After.IsNone;
}
