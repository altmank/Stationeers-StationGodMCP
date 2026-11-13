#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// remove item (catalogue remove.json): loose items deleted by id with OnServer.Destroy, as Materials and Deliveries
/// delete items, so clients see them go. A cheat (the cheat switch admits it outside creative): without confirm the
/// call reports each item that would go [id, name, where -> "deleted"] and deletes nothing. Each id is judged alone by
/// RemoveItemRule: a refused id gets its row and the others still go. Nothing is refunded and it cannot be undone.
/// </summary>
[ReplyFact(FactRule.Live, "what it changes, read and checked when it acts")]
internal static class RemoveItem
{
    private const int MostContentDepth = 8;

    internal static VerbOutcome Run(RemoveItemOptions options)
    {
        string label = RemoveItemOptions.Spec.Label;
        PerIdList? ids = options.Selector.Ids;
        List<OptionProblem> problems = new List<OptionProblem>();
        if (ids == null)
        {
            problems.Add(new OptionProblem("id", "is needed: the items to delete"));
        }
        else if (RemoveItemRule.TooMany(ids.Ids.Count) is { } tooMany)
        {
            problems.Add(new OptionProblem("id", tooMany));
        }

        if (problems.Count > 0)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(label, problems));
        }

        ItemAllowance allow = ItemAllowance.Of(options.Allow);
        string undoMark = RemoveItemOptions.Spec.Write!.UndoMark;
        bool notes = options.Fields.Includes("notes");
        return WriteCheck<ItemRemovalPlan>.Run(() => Plan(ids!, allow, label))
            .Outcome(options.Report, plan => Act(plan, ids!), plan => Described(plan, undoMark, notes));
    }

    // Every id read and judged before anything goes; only host-side refusals are thrown.
    private static ItemRemovalPlan Plan(PerIdList ids, ItemAllowance allow, string label)
    {
        HostOnly.Require();

        List<PlannedItem> planned = new List<PlannedItem>(ids.Ids.Count);
        HashSet<long> claimed = new HashSet<long>();
        foreach (string id in ids.Ids)
        {
            planned.Add(PlanOne(id, allow, label, claimed));
        }

        return new ItemRemovalPlan(planned);
    }

    private static PlannedItem PlanOne(string id, ItemAllowance allow, string label, HashSet<long> claimed)
    {
        if (TargetIds.TryOf(id, label, out ThingId read) is { } unread)
        {
            return new PlannedItem.Refused(id, VerbError.Of(unread));
        }

        if (!GameLookup.TryFindThing(read, out Thing thing) || thing.IsBeingDestroyed)
        {
            return new PlannedItem.Refused(read.ToString(), VerbError.Of(ApiErrors.ThingNotFound(read)));
        }

        List<Item> inside = new List<Item>();
        if (thing is Item)
        {
            Inside(thing, inside, 0);
        }

        ItemFacts facts = new ItemFacts(KindOf(thing), Names.Of(thing),
            thing.RootParentHuman != null, inside.Count);
        if (RemoveItemRule.Decide(facts, allow) is ItemVerdict.Refused refused)
        {
            return new PlannedItem.Refused(read.ToString(),
                new VerbError(RemoveItemRule.RefusedCode, $"{label}: {refused.Message}"));
        }

        if (!claimed.Add(thing.ReferenceId))
        {
            return new PlannedItem.Refused(read.ToString(), new VerbError(RemoveItemRule.RefusedCode,
                $"{label}: {facts.Name} is already going in this call (named twice, or held by an item named earlier)."));
        }

        List<Item> going = new List<Item>(inside.Count);
        List<DeletedItemView> contents = new List<DeletedItemView>(inside.Count);
        foreach (Item held in inside)
        {
            if (claimed.Add(held.ReferenceId))
            {
                going.Add(held);
                contents.Add(View(held));
            }
        }

        return new PlannedItem.Deleting((Item)thing, going,
            new ItemRemovalView(View(thing), contents));
    }

    private static VerbOutcome Act(ItemRemovalPlan plan, PerIdList ids)
    {
        Rows rows = new Rows();
        VerbError? onlyRefusal = null;
        ItemRemovalView? onlyDeleted = null;
        foreach (PlannedItem entry in plan.Entries)
        {
            switch (entry)
            {
                case PlannedItem.Deleting deleting:
                    deleting.Delete();
                    rows.Succeeded(deleting.View.ReferenceId, deleting.View);
                    onlyDeleted = deleting.View;
                    break;
                case PlannedItem.Refused refused:
                    rows.Failed(refused.ReferenceId, refused.Error);
                    onlyRefusal = refused.Error;
                    break;
            }
        }

        if (ids.AnswersRows)
        {
            return new VerbOutcome.Answered(rows.Reply());
        }

        return onlyRefusal != null
            ? new VerbOutcome.Refused(onlyRefusal)
            : new VerbOutcome.Answered(onlyDeleted!);
    }

    private static ReportReply Described(ItemRemovalPlan plan, string undoMark, bool notes)
    {
        List<ItemRemovalView> removals = new List<ItemRemovalView>();
        List<ReportProblem> problems = new List<ReportProblem>();
        for (int index = 0; index < plan.Entries.Count; index++)
        {
            switch (plan.Entries[index])
            {
                case PlannedItem.Deleting deleting:
                    removals.Add(deleting.View);
                    break;
                case PlannedItem.Refused refused:
                    problems.Add(new ReportProblem(index, refused.ReferenceId, refused.Error));
                    break;
            }
        }

        return ItemRemovalReplies.Reported(removals, problems, undoMark, notes);
    }

    private static RemovableKind KindOf(Thing thing) => thing switch
    {
        Human => RemovableKind.Player,
        Entity => RemovableKind.Entity,
        Structure => RemovableKind.Structure,
        Item => RemovableKind.Item,
        _ => RemovableKind.Other
    };

    // The items in a holder's slots and, inside them, theirs, outermost first.
    private static void Inside(Thing holder, List<Item> found, int depth)
    {
        if (holder.Slots == null || depth >= MostContentDepth)
        {
            return;
        }

        foreach (Slot slot in holder.Slots)
        {
            if (slot?.Get() is Item item && item != null && !(item is Organ) && !item.IsBeingDestroyed)
            {
                found.Add(item);
                Inside(item, found, depth + 1);
            }
        }
    }

    private static DeletedItemView View(Thing thing)
    {
        Slot? slot = thing is DynamicThing { ParentSlot: { } } held ? held.ParentSlot : null;
        Thing? holder = slot?.Parent;
        Human? carrier = thing.RootParentHuman;
        return new DeletedItemView(new ThingId(thing.ReferenceId).ToString(), Names.Of(thing),
            RemoveItemRule.Where(holder != null ? Names.Of(holder) : null, slot?.SlotIndex,
                carrier != null ? Names.Of(carrier) : null));
    }
}

/// <summary>What a call decided for each id it gave, in order.</summary>
internal sealed class ItemRemovalPlan
{
    internal ItemRemovalPlan(IReadOnlyList<PlannedItem> entries)
    {
        Entries = entries;
    }

    internal IReadOnlyList<PlannedItem> Entries { get; }
}

/// <summary>One id's place in the plan: items to delete (with what goes inside them), or the refusal that names why not.</summary>
internal abstract class PlannedItem
{
    private PlannedItem()
    {
    }

    internal sealed class Deleting : PlannedItem
    {
        private readonly Item _item;
        private readonly IReadOnlyList<Item> _inside;

        internal Deleting(Item item, IReadOnlyList<Item> inside, ItemRemovalView view)
        {
            _item = item;
            _inside = inside;
            View = view;
        }

        internal ItemRemovalView View { get; }

        /// <summary>The items inside first, innermost last-listed first, then the item itself; all as OnServer.Destroy.</summary>
        internal void Delete()
        {
            for (int at = _inside.Count - 1; at >= 0; at--)
            {
                Destroy(_inside[at]);
            }

            Destroy(_item);
        }

        private static void Destroy(Item item)
        {
            if (item != null && !item.IsBeingDestroyed)
            {
                OnServer.Destroy(item);
            }
        }
    }

    internal sealed class Refused : PlannedItem
    {
        internal Refused(string referenceId, VerbError error)
        {
            ReferenceId = referenceId;
            Error = error;
        }

        internal string ReferenceId { get; }

        internal VerbError Error { get; }
    }
}
