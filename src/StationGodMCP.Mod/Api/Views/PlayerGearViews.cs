#nullable enable

using StationGodMCP.Api.Shared;

namespace StationGodMCP.Api.Views;

/// <summary>inspect player: what the player wears and the suit's live movement numbers.</summary>
internal sealed class PlayerGearView
{
    internal PlayerGearView(ThingView? suit, ThingView? helmet, JetpackView? jetpack, SuitMovementView? movement)
    {
        Suit = suit;
        Helmet = helmet;
        Jetpack = jetpack;
        SuitMovement = movement;
    }

    /// <summary>The thing in the suit slot, or null.</summary>
    public ThingView? Suit { get; }

    /// <summary>The thing in the helmet slot, or null.</summary>
    public ThingView? Helmet { get; }

    /// <summary>The jetpack Human.TryGetJetpack finds (the lookup the movement code uses), or null.</summary>
    public JetpackView? Jetpack { get; }

    /// <summary>Null when no suit is worn.</summary>
    public SuitMovementView? SuitMovement { get; }
}

internal sealed class JetpackView
{
    internal JetpackView(ThingView thing, bool on)
    {
        ReferenceId = thing.ReferenceId;
        PrefabName = thing.PrefabName;
        DisplayName = thing.DisplayName;
        On = on;
    }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public string? DisplayName { get; }

    public bool On { get; }
}

/// <summary>
/// The suit's multipliers as the movement code reads them: MovementSpeedMultiplier from Human.Suit, the storm one from
/// the SuitBase in the suit slot (null when that suit is not a SuitBase).
/// </summary>
internal sealed class SuitMovementView
{
    internal SuitMovementView(float speedMultiplier, float? stormSpeedMultiplier, int slotCount)
    {
        SpeedMultiplier = speedMultiplier;
        StormSpeedMultiplier = stormSpeedMultiplier;
        SlotCount = slotCount;
    }

    public float SpeedMultiplier { get; }

    public float? StormSpeedMultiplier { get; }

    public int SlotCount { get; }
}
