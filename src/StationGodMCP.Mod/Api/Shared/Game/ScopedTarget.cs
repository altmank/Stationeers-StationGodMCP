#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Motherboards;
using Assets.Scripts.Objects.Pipes;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// One logic-capable thing the API may touch: a Device in the call's scope (a gateway's data networks, or the whole
/// world), or a logic-capable item worn or held by a player (suit, helmet, backpack, glasses, toolbelt, hands). Worn
/// items are not Devices and never sit on a cable network, so the API works against this adapter (ILogicable).
/// </summary>
internal sealed class ScopedTarget
{
    private readonly ILogicable _logicable;

    private ScopedTarget(ILogicable logicable, Thing thing, string? wornBy, string? wornSlot)
    {
        _logicable = logicable;
        Thing = thing;
        WornBy = wornBy;
        WornSlot = wornSlot;
    }

    /// <summary>The underlying game object, for type tests such as IMemory or ICircuitHolder.</summary>
    internal Thing Thing { get; }

    internal string? WornBy { get; }

    internal string? WornSlot { get; }

    internal long ReferenceId => Thing.ReferenceId;

    internal int PrefabHash => Thing.PrefabHash;

    internal string PrefabName => Thing.PrefabName;

    internal string DisplayName => Thing.DisplayName;

    internal List<Slot>? Slots => Thing.Slots;

    internal int TotalSlots => _logicable.TotalSlots;

    internal Slot GetSlot(int index) => _logicable.GetSlot(index);

    internal int GetNameHash() => _logicable.GetNameHash();

    internal bool CanLogicRead(LogicType logicType) => _logicable.CanLogicRead(logicType);

    internal bool CanLogicWrite(LogicType logicType) => _logicable.CanLogicWrite(logicType);

    internal bool CanLogicRead(LogicSlotType logicSlotType, int slotId) =>
        _logicable.CanLogicRead(logicSlotType, slotId);

    internal double GetLogicValue(LogicType logicType) => _logicable.GetLogicValue(logicType);

    internal double GetLogicValue(LogicSlotType logicSlotType, int slotId) =>
        _logicable.GetLogicValue(logicSlotType, slotId);

    internal void SetLogicValue(LogicType logicType, double value) => _logicable.SetLogicValue(logicType, value);

    internal static ScopedTarget? From(ILogicable? logicable, string? wornBy = null, string? wornSlot = null)
    {
        Thing? thing = logicable?.GetAsThing;
        return thing == null ? null : new ScopedTarget(logicable!, thing, wornBy, wornSlot);
    }

    /// <summary>Every logic-capable item worn or held by any human in the loaded world (Human.AllHumans).</summary>
    internal static List<ScopedTarget> Worn()
    {
        List<ScopedTarget> worn = new List<ScopedTarget>();
        foreach (Human human in new List<Human>(Human.AllHumans))
        {
            if (human == null)
            {
                continue;
            }

            string wearer = human.DisplayName;
            AddWorn(worn, wearer, "suit", human.SuitSlot);
            AddWorn(worn, wearer, "helmet", human.HelmetSlot);
            AddWorn(worn, wearer, "backpack", human.BackpackSlot);
            AddWorn(worn, wearer, "toolbelt", human.ToolbeltSlot);
            AddWorn(worn, wearer, "glasses", human.GlassesSlot);
            AddWorn(worn, wearer, "uniform", human.UniformSlot);
            AddWorn(worn, wearer, "left_hand", human.LeftHandSlot);
            AddWorn(worn, wearer, "right_hand", human.RightHandSlot);
        }

        return worn;
    }

    /// <summary>The worn or held item with this reference id, or null.</summary>
    internal static ScopedTarget? FindWorn(long referenceId)
    {
        foreach (ScopedTarget worn in Worn())
        {
            if (worn.ReferenceId == referenceId)
            {
                return worn;
            }
        }

        return null;
    }

    private static void AddWorn(List<ScopedTarget> worn, string wearer, string slotName, Slot? slot)
    {
        ILogicable? logicable = null;
        try
        {
            logicable = slot?.Get() as ILogicable;
        }
        catch (Exception)
        {
            // Slot.Get on a slot mid-swap can throw; skip it this request.
        }

        ScopedTarget? target = From(logicable, wearer, slotName);
        if (target != null)
        {
            worn.Add(target);
        }
    }
}
