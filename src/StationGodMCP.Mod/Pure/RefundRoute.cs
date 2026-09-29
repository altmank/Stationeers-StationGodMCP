#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;

namespace StationGodMCP.Pure;

/// <summary>
/// One place a refund_to list names, tried in the order given: the player's inventory, the from_id stack topped up,
/// the from_id holder's storage (its slots, then those of the container it stands in), one container named by id, or
/// the ground.
/// </summary>
internal abstract class RefundTarget
{
    internal const string InventoryName = "inventory";
    internal const string SourceName = "source";
    internal const string StorageName = "storage";
    internal const string ContainerName = "container";
    internal const string GroundName = "ground";

    private RefundTarget()
    {
    }

    internal static RefundTarget Inventory { get; } = new InventoryTarget();

    internal static RefundTarget Source { get; } = new SourceTarget();

    internal static RefundTarget Storage { get; } = new StorageTarget();

    internal static RefundTarget Ground { get; } = new GroundTarget();

    /// <summary>What the reply calls it: inventory, source, storage, container or ground.</summary>
    internal abstract string Kind { get; }

    /// <summary>How the request names it: the word, or the container's id as a decimal string.</summary>
    internal virtual string Word => Kind;

    /// <summary>A word (inventory, source, storage, ground) or a container's reference id; null for anything else.</summary>
    internal static RefundTarget? Of(string text)
    {
        string word = text.Trim().ToLowerInvariant();
        return word switch
        {
            InventoryName => Inventory,
            SourceName => Source,
            StorageName => Storage,
            GroundName => Ground,
            _ => long.TryParse(word, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) && id > 0
                ? new Container(id)
                : null
        };
    }

    private sealed class InventoryTarget : RefundTarget
    {
        internal override string Kind => InventoryName;
    }

    private sealed class SourceTarget : RefundTarget
    {
        internal override string Kind => SourceName;
    }

    private sealed class StorageTarget : RefundTarget
    {
        internal override string Kind => StorageName;
    }

    private sealed class GroundTarget : RefundTarget
    {
        internal override string Kind => GroundName;
    }

    /// <summary>Exactly this container: its own slots and those of what is stored in them, never its holder's.</summary>
    internal sealed class Container : RefundTarget
    {
        internal Container(long id)
        {
            Id = id;
        }

        internal long Id { get; }

        internal override string Kind => ContainerName;

        internal override string Word => Id.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// What refund_to asks for. The three single words of before keep their meaning: source (Holder: the source's
/// inventory tree, then the ground in front of it), ground (WherePieceStood) and none. Every other form is a Chain of
/// targets tried in turn per item until it fits; the default is inventory, source, storage, ground.
/// </summary>
internal abstract class RefundRoute
{
    internal const int MaximumTargets = 8;

    private RefundRoute()
    {
    }

    internal static RefundRoute Holder { get; } = new HolderRoute();

    internal static RefundRoute WherePieceStood { get; } = new GroundRoute();

    internal static RefundRoute Nothing { get; } = new NothingRoute();

    /// <summary>Used when refund_to is left out: the player's inventory, the source stack, storage, then the ground.</summary>
    internal static RefundRoute Default { get; } = new Chain(new List<RefundTarget>
        { RefundTarget.Inventory, RefundTarget.Source, RefundTarget.Storage, RefundTarget.Ground });

    /// <summary>Whether anything is given back.</summary>
    internal virtual bool GivesBack => true;

    /// <summary>Whether the route needs a source to give to (the single word source); a chain skips what is missing.</summary>
    internal virtual bool NeedsHolder => false;

    /// <summary>How the request names it: one word, or the chain's words in order.</summary>
    internal abstract IReadOnlyList<string> Words { get; }

    /// <summary>Whether the request named it with one of the single words (source, ground, none).</summary>
    internal virtual bool IsSingleWord => true;

    /// <summary>The words joined, as the notes quote them.</summary>
    internal string Name => IsSingleWord ? Words[0] : "[" + string.Join(", ", Words) + "]";

    /// <summary>
    /// The route one word names: source, ground and none as before; any other target word (or a container id) is a
    /// chain of that one target. Null when the word names nothing.
    /// </summary>
    internal static RefundRoute? OfWord(string text)
    {
        string word = text.Trim().ToLowerInvariant();
        switch (word)
        {
            case RefundTarget.SourceName:
                return Holder;
            case RefundTarget.GroundName:
                return WherePieceStood;
            case "none":
                return Nothing;
        }

        RefundTarget? target = RefundTarget.Of(word);
        return target != null ? new Chain(new List<RefundTarget> { target }) : null;
    }

    /// <summary>
    /// A chain of targets in the order given: at least one, at most MaximumTargets, none twice, none of them "none".
    /// The error in words when the list is not one.
    /// </summary>
    internal static RefundRoute? OfWords(IReadOnlyList<string> words, out string? error)
    {
        error = null;
        if (words.Count == 0 || words.Count > MaximumTargets)
        {
            error = $"refund_to as a list takes 1 to {MaximumTargets} targets.";
            return null;
        }

        List<RefundTarget> targets = new List<RefundTarget>(words.Count);
        foreach (string word in words)
        {
            RefundTarget? target = RefundTarget.Of(word);
            if (target == null)
            {
                error = $"refund_to target '{word}' must be inventory, source, storage, ground or a container's " +
                        "reference id.";
                return null;
            }

            if (targets.Exists(known => known.Word == target.Word))
            {
                error = $"refund_to names {target.Word} twice.";
                return null;
            }

            targets.Add(target);
        }

        return new Chain(targets);
    }

    private sealed class HolderRoute : RefundRoute
    {
        internal override bool NeedsHolder => true;

        internal override IReadOnlyList<string> Words { get; } = new[] { RefundTarget.SourceName };
    }

    private sealed class GroundRoute : RefundRoute
    {
        internal override IReadOnlyList<string> Words { get; } = new[] { RefundTarget.GroundName };
    }

    private sealed class NothingRoute : RefundRoute
    {
        internal override bool GivesBack => false;

        internal override IReadOnlyList<string> Words { get; } = new[] { "none" };
    }

    /// <summary>Targets tried in turn per item until it fits; what fits none goes on the ground in front of the holder.</summary>
    internal sealed class Chain : RefundRoute
    {
        internal Chain(IReadOnlyList<RefundTarget> targets)
        {
            Targets = targets;
            List<string> words = new List<string>(targets.Count);
            foreach (RefundTarget target in targets)
            {
                words.Add(target.Word);
            }

            Words = words;
        }

        internal IReadOnlyList<RefundTarget> Targets { get; }

        internal override IReadOnlyList<string> Words { get; }

        internal override bool IsSingleWord => false;
    }
}

/// <summary>What the game offers a refund chain: who and what exists to take items.</summary>
internal sealed class RefundReach
{
    internal RefundReach(bool player, bool sourceStack, bool storage)
    {
        Player = player;
        SourceStack = sourceStack;
        Storage = storage;
    }

    /// <summary>A player whose inventory takes items (from_id a player, else the local player).</summary>
    internal bool Player { get; }

    /// <summary>from_id is a stack (a coil, a sheet stack) that can be topped up.</summary>
    internal bool SourceStack { get; }

    /// <summary>A holder whose slots store items (from_id, else the local player).</summary>
    internal bool Storage { get; }
}

/// <summary>Which of a chain's targets the game can use now, and why each other one is skipped.</summary>
internal static class RefundChainRule
{
    /// <summary>
    /// The targets usable in order; each word target without what it needs is skipped with the reason. A container
    /// target is kept: the game checks that the id is a container.
    /// </summary>
    internal static List<RefundTarget> Usable(IReadOnlyList<RefundTarget> targets, RefundReach reach,
        List<string> skipped)
    {
        List<RefundTarget> usable = new List<RefundTarget>(targets.Count);
        foreach (RefundTarget target in targets)
        {
            string? why = target.Kind switch
            {
                RefundTarget.InventoryName when !reach.Player =>
                    "inventory skipped: there is no player (a dedicated server; pass from_id of a player).",
                RefundTarget.SourceName when !reach.SourceStack =>
                    "source skipped: from_id is not a stack to top up.",
                RefundTarget.StorageName when !reach.Storage =>
                    "storage skipped: there is no from_id and no local player whose slots could take it.",
                _ => null
            };
            if (why != null)
            {
                skipped.Add(why);
            }
            else
            {
                usable.Add(target);
            }
        }

        return usable;
    }
}

/// <summary>One target's places for one item: matching stacks with room and empty slots that take it, by key.</summary>
internal sealed class RefundOffer
{
    internal RefundOffer(IReadOnlyList<long> stacks, IReadOnlyList<int> rooms, IReadOnlyList<long> slots, bool ground)
    {
        Stacks = stacks;
        Rooms = rooms;
        Slots = slots;
        Ground = ground;
    }

    /// <summary>An offer that only puts things down on the ground.</summary>
    internal static RefundOffer OnGround { get; } =
        new RefundOffer(Array.Empty<long>(), Array.Empty<int>(), Array.Empty<long>(), true);

    /// <summary>Matching stacks, in inventory order, each with its room (Rooms, same order) when surveyed.</summary>
    internal IReadOnlyList<long> Stacks { get; }

    internal IReadOnlyList<int> Rooms { get; }

    /// <summary>Empty slots that take the item, in inventory order.</summary>
    internal IReadOnlyList<long> Slots { get; }

    /// <summary>The ground: takes whatever is left.</summary>
    internal bool Ground { get; }
}

/// <summary>A refund step and the target it lands in: an index into the offers, or Offers.Count for the fallback ground.</summary>
internal sealed class RefundChainStep
{
    internal RefundChainStep(int target, RefundStep step, long? key)
    {
        Target = target;
        Step = step;
        Key = key;
    }

    internal int Target { get; }

    internal RefundStep Step { get; }

    /// <summary>The stack merged into or the slot filled; null on the ground.</summary>
    internal long? Key { get; }
}

/// <summary>
/// Plans a refund along a chain, item after item, remembering what earlier items and targets used: a stack's room
/// taken and the slots filled, so two targets that share a slot (inventory and storage of the same player) never
/// count it twice. Each target in turn takes what it can in RefundPlacement's order (stacks, then empty slots); a
/// ground target takes the rest; what no target takes goes on the ground as the fallback.
/// </summary>
internal sealed class RefundLedger
{
    private readonly Dictionary<long, int> _taken = new Dictionary<long, int>();
    private readonly HashSet<long> _filled = new HashSet<long>();

    internal List<RefundChainStep> Plan(int quantity, int maxStack, IReadOnlyList<RefundOffer> offers)
    {
        List<RefundChainStep> steps = new List<RefundChainStep>();
        int left = Math.Max(0, quantity);
        for (int target = 0; target < offers.Count && left > 0; target++)
        {
            left = Take(target, offers[target], left, maxStack, steps);
        }

        if (left > 0)
        {
            Take(offers.Count, RefundOffer.OnGround, left, maxStack, steps);
        }

        return steps;
    }

    private int Take(int target, RefundOffer offer, int quantity, int maxStack, List<RefundChainStep> steps)
    {
        List<int> rooms = new List<int>(offer.Stacks.Count);
        for (int index = 0; index < offer.Stacks.Count; index++)
        {
            int taken = _taken.TryGetValue(offer.Stacks[index], out int sum) ? sum : 0;
            rooms.Add(Math.Max(0, offer.Rooms[index] - taken));
        }

        List<long> free = new List<long>(offer.Slots.Count);
        foreach (long slot in offer.Slots)
        {
            if (!_filled.Contains(slot) && !free.Contains(slot))
            {
                free.Add(slot);
            }
        }

        int left = 0;
        foreach (RefundStep step in RefundPlacement.Plan(quantity, maxStack, rooms, free.Count))
        {
            switch (step)
            {
                case RefundStep.Merge merge:
                    long stack = offer.Stacks[merge.Stack];
                    _taken[stack] = (_taken.TryGetValue(stack, out int sum) ? sum : 0) + step.Quantity;
                    steps.Add(new RefundChainStep(target, new RefundStep.Merge(merge.Stack, step.Quantity), stack));
                    break;
                case RefundStep.IntoSlot slot:
                    long key = free[slot.Slot];
                    _filled.Add(key);
                    steps.Add(new RefundChainStep(target, new RefundStep.IntoSlot(IndexOf(offer.Slots, key),
                        step.Quantity), key));
                    break;
                default:
                    if (offer.Ground)
                    {
                        steps.Add(new RefundChainStep(target, step, null));
                    }
                    else
                    {
                        left += step.Quantity;
                    }

                    break;
            }
        }

        return left;
    }

    private static int IndexOf(IReadOnlyList<long> keys, long key)
    {
        for (int index = 0; index < keys.Count; index++)
        {
            if (keys[index] == key)
            {
                return index;
            }
        }

        return -1;
    }
}
