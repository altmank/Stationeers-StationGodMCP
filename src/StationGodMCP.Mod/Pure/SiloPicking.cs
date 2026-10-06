#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// An SDB Silo's rules as the silo tools apply them (CODE, Assets.Scripts.Objects.Chutes/Silo.cs): its capacity, the
/// export's food rule, which entries a withdrawal pools and which it moves whole, and when an IC's DispenseSlot would
/// name a different entry after a withdrawal.
/// </summary>
internal static class SiloRules
{
    /// <summary>Silo.MAX_ITEMS (Silo.cs:22): entries, one per imported thing, whatever each holds.</summary>
    internal const int Capacity = 600;

    /// <summary>
    /// Entries a deposit may still add: the capacity less what is stored and less an import the silo is saving now
    /// (TickSaveImport enqueues it when done; TryProcessImport starts one only while not IsFull).
    /// </summary>
    internal static int Room(int stored, bool importing)
    {
        int room = Capacity - stored - (importing ? 1 : 0);
        return room > 0 ? room : 0;
    }

    /// <summary>
    /// The export's food rule (Silo.BeginExport, Silo.cs:262): an INutrition Item with CanDecay that is not a Seed is
    /// damaged past its MaxDamage as decay, so stored food comes out rotten. Only the exported thing itself, never
    /// what it holds.
    /// </summary>
    internal static bool RotsWhenTaken(bool isNutrition, bool canDecay, bool isSeed) => isNutrition && canDecay && !isSeed;

    /// <summary>
    /// Whether a withdrawal pools an entry by quantity (new and topped-up stacks of the prefab) rather than loading the
    /// stored thing itself: a stack (Stackable or Consumable) holding nothing, that does not rot on the way out.
    /// </summary>
    internal static bool Pools(bool countsQuantity, int children, bool rots) => countsQuantity && children == 0 && !rots;

    /// <summary>
    /// Whether taking entries changes what an IC's DispenseSlot names: the slot is an index into the store, read by
    /// BeginNextExport (Silo.cs:213), so touching that entry or removing any before it moves the index onto another.
    /// </summary>
    internal static bool ShiftsDispenseSlot(int dispenseSlot, int firstTouched) =>
        dispenseSlot >= 0 && firstTouched >= 0 && firstTouched <= dispenseSlot;
}

/// <summary>One stored entry as a withdrawal weighs it: whether it is the prefab asked for, how much, and how it moves.</summary>
internal readonly struct SiloEntryFacts
{
    internal SiloEntryFacts(bool matches, double quantity, bool pooled)
    {
        Matches = matches;
        Quantity = quantity;
        Pooled = pooled;
    }

    internal bool Matches { get; }

    /// <summary>A stack's Quantity, 1 for anything else.</summary>
    internal double Quantity { get; }

    internal bool Pooled { get; }
}

/// <summary>What a withdrawal takes from one entry: how much, and what the entry keeps.</summary>
internal readonly struct SiloTake
{
    internal SiloTake(int index, double quantity, double left, bool whole)
    {
        Index = index;
        Quantity = quantity;
        Left = left;
        Whole = whole;
    }

    /// <summary>The entry's place in the store, front (next to export) first.</summary>
    internal int Index { get; }

    internal double Quantity { get; }

    /// <summary>What stays in the entry; 0 when the entry goes.</summary>
    internal double Left { get; }

    /// <summary>The stored thing itself is loaded, with everything it holds, rather than pooled.</summary>
    internal bool Whole { get; }
}

/// <summary>
/// Where a withdrawal goes: the pooled amount as StackPlacement places it (matching stacks, then new stacks in empty
/// slots), then each whole entry into one of the empty slots left, one entry per slot; the ground for either only when
/// allowed.
/// </summary>
internal sealed class SiloPlacement
{
    private SiloPlacement(StackPlan pooled, StackPlan whole, int wholeSlotsFrom)
    {
        Pooled = pooled;
        Whole = whole;
        WholeSlotsFrom = wholeSlotsFrom;
    }

    internal StackPlan Pooled { get; }

    /// <summary>One step of quantity 1 per whole entry: a NewStack names an empty slot counted from WholeSlotsFrom.</summary>
    internal StackPlan Whole { get; }

    /// <summary>The first empty slot (an index into the slots offered) the whole entries may use.</summary>
    internal int WholeSlotsFrom { get; }

    internal bool Fits => Pooled.Fits && Whole.Fits;

    internal static SiloPlacement Plan(double pooled, double fullStack, IReadOnlyList<double> rooms, int emptySlots,
        int whole, bool allowGround)
    {
        StackPlan pooledPlan = StackPlacement.Plan(pooled, fullStack, rooms, emptySlots, allowGround);
        int used = 0;
        foreach (PlacementStep step in pooledPlan.Steps)
        {
            used += step is PlacementStep.NewStack ? 1 : 0;
        }

        StackPlan wholePlan = StackPlacement.Plan(whole, 1.0, new List<double>(), emptySlots - used, allowGround);
        return new SiloPlacement(pooledPlan, wholePlan, used);
    }
}

/// <summary>Which entries a withdrawal takes, or why it cannot.</summary>
internal abstract class SiloPick
{
    /// <summary>Below this an amount is rounding, as in StackPlacement.</summary>
    internal const double Trace = StackPlacement.Trace;

    private SiloPick()
    {
    }

    /// <summary>
    /// Front to back, the silo's export order (Silo.BeginNextExport dequeues the front): pooled entries give what is
    /// still wanted, the last one keeping the rest; an entry moved whole counts all it holds and is never split, so a
    /// quantity ending inside one is refused with the two quantities around it.
    /// </summary>
    internal static SiloPick Choose(IReadOnlyList<SiloEntryFacts> entries, double quantity)
    {
        double available = 0.0;
        foreach (SiloEntryFacts entry in entries)
        {
            available += entry.Matches ? entry.Quantity : 0.0;
        }

        if (available <= Trace)
        {
            return new NoneStored();
        }

        if (quantity > available + Trace)
        {
            return new NotEnough(available);
        }

        List<SiloTake> takes = new List<SiloTake>();
        double left = quantity;
        double pooled = 0.0;
        int whole = 0;
        for (int index = 0; index < entries.Count && left > Trace; index++)
        {
            SiloEntryFacts entry = entries[index];
            if (!entry.Matches || entry.Quantity <= Trace)
            {
                continue;
            }

            if (entry.Pooled)
            {
                double part = left < entry.Quantity ? left : entry.Quantity;
                double kept = entry.Quantity - part;
                takes.Add(new SiloTake(index, part, kept > Trace ? kept : 0.0, whole: false));
                pooled += part;
                left -= part;
                continue;
            }

            if (entry.Quantity > left + Trace)
            {
                double below = quantity - left;
                return new SplitsEntry(index, entry.Quantity, below, below + entry.Quantity);
            }

            takes.Add(new SiloTake(index, entry.Quantity, 0.0, whole: true));
            whole++;
            left -= entry.Quantity;
        }

        return new Picked(takes, pooled, whole);
    }

    /// <summary>The entries taken, in store order; the pooled amount; how many entries move whole.</summary>
    internal sealed class Picked : SiloPick
    {
        internal Picked(List<SiloTake> takes, double pooled, int whole)
        {
            Takes = takes;
            Pooled = pooled;
            Whole = whole;
        }

        internal List<SiloTake> Takes { get; }

        internal double Pooled { get; }

        internal int Whole { get; }

        /// <summary>The first entry touched, -1 for none.</summary>
        internal int FirstIndex => Takes.Count > 0 ? Takes[0].Index : -1;
    }

    /// <summary>The silo holds none of the prefab.</summary>
    internal sealed class NoneStored : SiloPick
    {
    }

    /// <summary>The silo holds less than asked.</summary>
    internal sealed class NotEnough : SiloPick
    {
        internal NotEnough(double available)
        {
            Available = available;
        }

        internal double Available { get; }
    }

    /// <summary>The quantity ends inside an entry that moves whole: Below or Above are quantities that do not.</summary>
    internal sealed class SplitsEntry : SiloPick
    {
        internal SplitsEntry(int index, double entryQuantity, double below, double above)
        {
            Index = index;
            EntryQuantity = entryQuantity;
            Below = below;
            Above = above;
        }

        internal int Index { get; }

        internal double EntryQuantity { get; }

        internal double Below { get; }

        internal double Above { get; }
    }
}
